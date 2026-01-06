using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.OnnxRuntime;
using Scalerize.InfiniteGpu.Desktop.Constants;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Scalerize.InfiniteGpu.Desktop.Services
{
    public sealed class BackgroundWorkService : IAsyncDisposable
    {
        private const string ExecutionRequestedEventName = "OnExecutionRequested";
        private static readonly TimeSpan ConnectionRetryDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan NoTokenBackoff = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ConnectionStatePollInterval = TimeSpan.FromSeconds(1);
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private readonly DeviceIdentifierService _deviceIdentifierService;
        private readonly OnnxRuntimeService _onnxRuntimeService;
        private readonly OnnxParsingService _onnxParsingService;
        private readonly OnnxPartitionerService _onnxPartitionerService;
        private readonly HardwareMetricsService _hardwareMetricsService;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly InputParsingService _inputParsingService;
        private readonly OutputParsingService _outputParsingService;
        private string? _deviceIdentifier;
        private readonly object _syncRoot = new();

        private CancellationTokenSource? _cts;
        private Task? _connectionLoopTask;
        private Task? _workerLoopTask;
        private Channel<ExecutionQueueItem>? _workChannel;
        private HubConnection? _hubConnection;
        private IDisposable? _executionRequestedSubscription;
        private string? _authToken;
        private int _tokenVersion;

        public BackgroundWorkService(DeviceIdentifierService deviceIdentifierService,
        OnnxRuntimeService onnxRuntimeService,
        HttpClient httpClient,
         InputParsingService inputParsingService,
          OutputParsingService outputParsingService,
          OnnxParsingService onnxParsingService,
          OnnxPartitionerService onnxPartitionerService,
          HardwareMetricsService hardwareMetricsService)
        {
            _deviceIdentifierService = deviceIdentifierService ?? throw new ArgumentNullException(nameof(deviceIdentifierService));
            _onnxRuntimeService = onnxRuntimeService ?? throw new ArgumentNullException(nameof(onnxRuntimeService));
            _inputParsingService = inputParsingService ?? throw new ArgumentNullException(nameof(inputParsingService));
            _outputParsingService = outputParsingService ?? throw new ArgumentNullException(nameof(outputParsingService));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _onnxParsingService = onnxParsingService ?? throw new ArgumentNullException(nameof(onnxParsingService));
            _onnxPartitionerService = onnxPartitionerService ?? throw new ArgumentNullException(nameof(onnxPartitionerService));
            _hardwareMetricsService = hardwareMetricsService ?? throw new ArgumentNullException(nameof(hardwareMetricsService));
        }

        public void Start()
        {
            lock (_syncRoot)
            {
                if (_cts is not null)
                {
                    return;
                }

                _cts = new CancellationTokenSource();
                _workChannel = Channel.CreateUnbounded<ExecutionQueueItem>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });

                var token = _cts.Token;
                _connectionLoopTask = Task.Run(() => RunConnectionLoopAsync(token), token);
                _workerLoopTask = Task.Run(() => RunWorkerLoopAsync(token), token);
            }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cts;
            Task? connectionTask;
            Task? workerTask;
            Channel<ExecutionQueueItem>? channel;

            lock (_syncRoot)
            {
                cts = _cts;
                if (cts is null)
                {
                    return;
                }

                connectionTask = _connectionLoopTask;
                workerTask = _workerLoopTask;
                channel = _workChannel;

                _cts = null;
                _connectionLoopTask = null;
                _workerLoopTask = null;
                _workChannel = null;
            }

            channel?.Writer.TryComplete();
            cts.Cancel();

            if (workerTask is not null)
            {
                try
                {
                    await workerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
            }

            if (connectionTask is not null)
            {
                try
                {
                    await connectionTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
            }

            await DisposeHubConnectionAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        public void UpdateAuthToken(string? token)
        {
            var sanitized = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
            var current = Volatile.Read(ref _authToken);

            if (string.Equals(current, sanitized, StringComparison.Ordinal))
            {
                return;
            }

            Volatile.Write(ref _authToken, sanitized);
            Interlocked.Increment(ref _tokenVersion);

            _ = Task.Run(ForceReconnectAsync);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);

            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }

        private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var token = Volatile.Read(ref _authToken);
                if (string.IsNullOrWhiteSpace(token))
                {
                    await Task.Delay(NoTokenBackoff, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(_deviceIdentifier))
                {
                    _deviceIdentifier = await _deviceIdentifierService.GetOrCreateIdentifierAsync(cancellationToken).ConfigureAwait(false);
                }

                var versionSnapshot = Volatile.Read(ref _tokenVersion);
                HubConnection? connection = null;
                Task? closedTask = null;

                try
                {
                    connection = BuildHubConnection(token, _deviceIdentifier!);
                    RegisterHubHandlers(connection);

                    var closedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    closedTask = closedTcs.Task;

                    connection.Closed += error =>
                    {
                        if (error is not null)
                        {
                            Debug.WriteLine($"[BackgroundWorkService] Hub connection closed with error: {error}");
                        }

                        closedTcs.TrySetResult();
                        return Task.CompletedTask;
                    };

                    await connection.StartAsync(cancellationToken).ConfigureAwait(false);

                    lock (_syncRoot)
                    {
                        _hubConnection = connection;
                    }

                    // Collect hardware capabilities including CPU, GPU, NPU TOPS and total RAM
                    var cpuInfo = _hardwareMetricsService.GetCpuInfo();
                    var gpuInfo = _hardwareMetricsService.GetGpuInfo();
                    var npuInfo = _hardwareMetricsService.GetNpuInfo();
                    var memoryInfo = _hardwareMetricsService.GetMemoryInfo();

                    var hardwareCapabilities = new
                    {
                        CpuEstimatedTops = cpuInfo.EstimatedTops,
                        GpuEstimatedTops = gpuInfo?.EstimatedTops,
                        NpuEstimatedTops = npuInfo?.EstimatedTops,
                        TotalRamBytes = (long)(memoryInfo.TotalGb.Value * 1024 * 1024 * 1024)
                    };

                    await connection.InvokeAsync("JoinAvailableTasks", string.Empty, "Provider", hardwareCapabilities, cancellationToken).ConfigureAwait(false);

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (versionSnapshot != Volatile.Read(ref _tokenVersion))
                        {
                            try
                            {
                                await connection.StopAsync().ConfigureAwait(false);
                            }
                            catch (Exception stopEx)
                            {
                                Debug.WriteLine($"[BackgroundWorkService] Failed to stop hub connection during token refresh: {stopEx}");
                            }
                            break;
                        }

                        var completed = await Task.WhenAny(closedTask, Task.Delay(ConnectionStatePollInterval, cancellationToken)).ConfigureAwait(false);
                        if (completed == closedTask)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown requested.
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BackgroundWorkService] Hub connection error: {ex}");
                    await Task.Delay(ConnectionRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _executionRequestedSubscription?.Dispose();
                    _executionRequestedSubscription = null;

                    if (connection is not null)
                    {
                        try
                        {
                            if (connection.State != HubConnectionState.Disconnected)
                            {
                                await connection.StopAsync().ConfigureAwait(false);
                            }
                        }
                        catch (Exception stopEx)
                        {
                            Debug.WriteLine($"[BackgroundWorkService] Error stopping hub connection: {stopEx}");
                        }

                        try
                        {
                            await connection.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception disposeEx)
                        {
                            Debug.WriteLine($"[BackgroundWorkService] Error disposing hub connection: {disposeEx}");
                        }

                        lock (_syncRoot)
                        {
                            if (ReferenceEquals(_hubConnection, connection))
                            {
                                _hubConnection = null;
                            }
                        }
                    }
                }
            }
        }

        private async Task RunWorkerLoopAsync(CancellationToken cancellationToken)
        {
            var channel = _workChannel;
            if (channel is null)
            {
                return;
            }

            var reader = channel.Reader;

            try
            {
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            await ProcessExecutionRequestAsync(item.Payload, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[BackgroundWorkService] Failed processing execution request: {ex}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown.
            }
        }

        private void RegisterHubHandlers(HubConnection connection)
        {
            _executionRequestedSubscription = connection.On<ExecutionRequestedPayload>(ExecutionRequestedEventName, payload => HandleExecutionRequestedAsync(payload));
        }

        private Task HandleExecutionRequestedAsync(ExecutionRequestedPayload? payload)
        {
            if (payload?.Subtask is null)
            {
                return Task.CompletedTask;
            }

            var channel = _workChannel;
            if (channel is null)
            {
                return Task.CompletedTask;
            }

            var writer = channel.Writer;
            var item = new ExecutionQueueItem(payload);

            if (writer.TryWrite(item))
            {
                return Task.CompletedTask;
            }

            var token = _cts?.Token ?? CancellationToken.None;
            return writer.WriteAsync(item, token).AsTask();
        }

        private async Task ProcessExecutionRequestAsync(ExecutionRequestedPayload payload, CancellationToken cancellationToken)
        {
            var subtask = payload.Subtask;
            var authToken = Volatile.Read(ref _authToken);
            if (authToken == null)
                return;

            var connection = await WaitForActiveConnectionAsync(cancellationToken);

            await connection.InvokeAsync("AcknowledgeExecutionStart", subtask.Id, cancellationToken);
            await connection.InvokeAsync("ReportProgress", subtask.Id, 5, cancellationToken);

            try
            {
                var modelBytes = await DownloadModelAsync(subtask, cancellationToken);

                var inputs = await _inputParsingService.BuildNamedInputsAsync(
                    subtask.ParametersJson, cancellationToken);

                var stopwatch = Stopwatch.StartNew();

                var inferenceResult = await _onnxRuntimeService.ExecuteOnnxModelAsync(
                    modelBytes, inputs, cancellationToken);

                var processedOutputs = await _outputParsingService.ProcessOutputsAsync(
                    subtask.TaskId,
                    subtask.Id,
                    subtask.ParametersJson,
                    inferenceResult.Outputs,
                    authToken,
                    cancellationToken);

                stopwatch.Stop();

                var resultPayload = new
                {
                    subtaskId = subtask.Id,
                    completedAtUtc = DateTimeOffset.UtcNow,
                    metrics = new
                    {
                        durationSeconds = stopwatch.Elapsed.TotalSeconds,
                        device = _onnxRuntimeService.GetExecutionProvider().ToString().ToLowerInvariant(),
                        memoryGBytes = _hardwareMetricsService.GetMemoryInfo().TotalGb.Value
                    },
                    outputs = processedOutputs
                };

                var resultJson = JsonSerializer.Serialize(resultPayload, SerializerOptions);

                await connection.InvokeAsync("SubmitResult", subtask.Id, resultJson, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundWorkService] Execution failed for subtask {subtask.Id}: {ex}");

                var errorPayload = new
                {
                    subtaskId = subtask.Id,
                    failedAtUtc = DateTimeOffset.UtcNow,
                    error = ex.Message
                };

                var errorJson = JsonSerializer.Serialize(errorPayload, SerializerOptions);

                try
                {
                    await connection.InvokeAsync("FailedResult", subtask.Id, errorJson, cancellationToken);
                }
                catch (Exception submitEx)
                {
                    Debug.WriteLine($"[BackgroundWorkService] Failed to submit error payload: {submitEx}");
                }
            }
        }

        private async Task<HubConnection> WaitForActiveConnectionAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HubConnection? connection;
                lock (_syncRoot)
                {
                    connection = _hubConnection;
                }

                if (connection is { State: HubConnectionState.Connected })
                {
                    return connection;
                }

                await Task.Delay(ConnectionStatePollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new OperationCanceledException();
        }

        private async Task ForceReconnectAsync()
        {
            HubConnection? connection;
            lock (_syncRoot)
            {
                connection = _hubConnection;
            }

            if (connection is null)
            {
                return;
            }

            try
            {
                await connection.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundWorkService] Forced reconnect failed: {ex}");
            }
        }

        private HubConnection BuildHubConnection(string token, string deviceIdentifier)
        {
            var hubUri = new Uri(Constants.Constants.BackendBaseUri, $"taskhub?deviceIdentifier={Uri.EscapeDataString(deviceIdentifier)}");

            return new HubConnectionBuilder()
                .WithUrl(hubUri, options =>
                {
                    options.AccessTokenProvider = () => Task.FromResult(token)!;
                })
                .WithAutomaticReconnect()
                .AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true)
                .Build();
        }

        private async Task DisposeHubConnectionAsync()
        {
            HubConnection? connection;

            lock (_syncRoot)
            {
                connection = _hubConnection;
                _hubConnection = null;
            }

            _executionRequestedSubscription?.Dispose();
            _executionRequestedSubscription = null;

            if (connection is null)
            {
                return;
            }

            try
            {
                if (connection.State != HubConnectionState.Disconnected)
                {
                    await connection.StopAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundWorkService] Error stopping hub connection during disposal: {ex}");
            }

            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundWorkService] Error disposing hub connection: {ex}");
            }
        }

        private async Task<byte[]> DownloadModelAsync(SubtaskPayload subtask, CancellationToken cancellationToken)
        {
            var uri = ResolveModelUri(subtask);
            if (uri is null)
            {
                throw new InvalidOperationException("Execution request does not contain a valid model URI.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        private static Uri? ResolveModelUri(SubtaskPayload subtask)
        {
            if (TryCreateUri(subtask.OnnxModel?.ReadUri, out var readUri))
            {
                return readUri;
            }

            return null;
        }

        private static bool TryCreateUri(string? value, out Uri? uri)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                uri = null;
                return false;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            {
                uri = absolute;
                return true;
            }

            if (Uri.TryCreate(Constants.Constants.BackendBaseUri, value, out var relative))
            {
                uri = relative;
                return true;
            }

            uri = null;
            return false;
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(2),
                BaseAddress = Constants.Constants.BackendBaseUri
            };

            return client;
        }

        private sealed record ExecutionQueueItem(ExecutionRequestedPayload Payload);

        private sealed class ExecutionRequestedPayload
        {
            public SubtaskPayload Subtask { get; init; } = new();
            public string ProviderUserId { get; init; } = string.Empty;
            public DateTime RequestedAtUtc { get; init; }
        }

        private sealed class SubtaskPayload
        {
            public Guid Id { get; init; }
            public Guid TaskId { get; init; }
            public string ParametersJson { get; init; } = "{}";
            public ExecutionSpecPayload? ExecutionSpec { get; init; }
            public ExecutionStatePayload? ExecutionState { get; init; }
            public OnnxModelPayload? OnnxModel { get; init; }
        }

        private sealed class ExecutionSpecPayload
        {
            public string? OnnxModelUrl { get; init; }
        }

        private sealed class ExecutionStatePayload
        {
            public IDictionary<string, JsonElement>? ExtendedMetadata { get; init; }
        }

        private sealed class OnnxModelPayload
        {
            public string? ResolvedReadUri { get; init; }
            public string? ReadUri { get; init; }
        }

    }
    public enum ExecutionProviderDevice
    {
        Cpu,
        Gpu,
        Npu
    }

}
