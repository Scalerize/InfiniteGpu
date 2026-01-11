using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.OnnxRuntime;
using Scalerize.InfiniteGpu.Desktop.Constants;
using System;
using System.Collections.Concurrent;
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
        private const string PartitionAssignedEventName = "OnPartitionAssigned";
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
        private readonly TensorSerializer _tensorSerializer;
        private readonly WebRtcPeerService _webRtcPeerService;
        private readonly OnnxSubgraphSerializer _subgraphSerializer;
        private string? _deviceIdentifier;
        private readonly object _syncRoot = new();

        private CancellationTokenSource? _cts;
        private Task? _connectionLoopTask;
        private Task? _workerLoopTask;
        private Task? _partitionWorkerTask;
        private Channel<ExecutionQueueItem>? _workChannel;
        private Channel<PartitionExecutionItem>? _partitionChannel;
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
            HardwareMetricsService hardwareMetricsService,
            TensorSerializer tensorSerializer,
            WebRtcPeerService webRtcPeerService,
            OnnxSubgraphSerializer subgraphSerializer)
        {
            _deviceIdentifierService = deviceIdentifierService ?? throw new ArgumentNullException(nameof(deviceIdentifierService));
            _onnxRuntimeService = onnxRuntimeService ?? throw new ArgumentNullException(nameof(onnxRuntimeService));
            _inputParsingService = inputParsingService ?? throw new ArgumentNullException(nameof(inputParsingService));
            _outputParsingService = outputParsingService ?? throw new ArgumentNullException(nameof(outputParsingService));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _onnxParsingService = onnxParsingService ?? throw new ArgumentNullException(nameof(onnxParsingService));
            _onnxPartitionerService = onnxPartitionerService ?? throw new ArgumentNullException(nameof(onnxPartitionerService));
            _hardwareMetricsService = hardwareMetricsService ?? throw new ArgumentNullException(nameof(hardwareMetricsService));
            _tensorSerializer = tensorSerializer ?? throw new ArgumentNullException(nameof(tensorSerializer));
            _webRtcPeerService = webRtcPeerService ?? throw new ArgumentNullException(nameof(webRtcPeerService));
            _subgraphSerializer = subgraphSerializer ?? throw new ArgumentNullException(nameof(subgraphSerializer));
            
            // Subscribe to partition events
            _webRtcPeerService.PartitionAssigned += OnPartitionAssigned;
            _webRtcPeerService.TensorReceived += OnTensorReceived;
            _webRtcPeerService.PartitionsReady += OnPartitionsReady;
            _webRtcPeerService.SubgraphReceived += OnSubgraphReceived;
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
                
                _partitionChannel = Channel.CreateUnbounded<PartitionExecutionItem>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });

                var token = _cts.Token;
                _connectionLoopTask = Task.Run(() => RunConnectionLoopAsync(token), token);
                _workerLoopTask = Task.Run(() => RunWorkerLoopAsync(token), token);
                _partitionWorkerTask = Task.Run(() => RunPartitionWorkerLoopAsync(token), token);
            }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cts;
            Task? connectionTask;
            Task? workerTask;
            Task? partitionWorkerTask;
            Channel<ExecutionQueueItem>? channel;
            Channel<PartitionExecutionItem>? partitionChannel;

            lock (_syncRoot)
            {
                cts = _cts;
                if (cts is null)
                {
                    return;
                }

                connectionTask = _connectionLoopTask;
                workerTask = _workerLoopTask;
                partitionWorkerTask = _partitionWorkerTask;
                channel = _workChannel;
                partitionChannel = _partitionChannel;

                _cts = null;
                _connectionLoopTask = null;
                _workerLoopTask = null;
                _partitionWorkerTask = null;
                _workChannel = null;
                _partitionChannel = null;
            }

            channel?.Writer.TryComplete();
            partitionChannel?.Writer.TryComplete();
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
            
            if (partitionWorkerTask is not null)
            {
                try
                {
                    await partitionWorkerTask.ConfigureAwait(false);
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
            
            // Initialize WebRTC peer service with the hub connection
            _webRtcPeerService.Initialize(connection);
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

            Stopwatch stopwatch = null;
            try
            {
                OnnxInferenceResult inferenceResult;

                bool isTraining = subtask.ExecutionSpec?.TaskType == 0 && !string.IsNullOrEmpty(subtask.ExecutionSpec?.OptimizerModelUrl);

                if (isTraining)
                {
                    var trainingModelBytes = await DownloadModelAsync(subtask.ExecutionSpec!.OnnxModelUrl!, cancellationToken);
                    var optimizerModelBytes = await DownloadModelAsync(subtask.ExecutionSpec!.OptimizerModelUrl!, cancellationToken);
                    var checkpointBytes = await DownloadModelAsync(subtask.ExecutionSpec!.CheckpointUrl!, cancellationToken);
                    var evalModelBytes = await DownloadModelAsync(subtask.ExecutionSpec!.EvalModelUrl!, cancellationToken);

                    stopwatch = Stopwatch.StartNew();
                    var inputs = await _inputParsingService.BuildNamedInputsAsync(subtask.ParametersJson, cancellationToken);
                    var outputs = await _inputParsingService.BuildNamedOutputsAsync(subtask.ParametersJson, cancellationToken);

                    inferenceResult = await _onnxRuntimeService.ExecuteTrainingSessionAsync(
                        trainingModelBytes,
                        optimizerModelBytes,
                        checkpointBytes,
                        evalModelBytes,
                        inputs,
                        outputs,
                        cancellationToken);
                }
                else
                {
                    var modelBytes = await DownloadModelAsync(subtask.OnnxModel?.ReadUri ?? subtask.ExecutionSpec?.OnnxModelUrl ?? string.Empty, cancellationToken);

                    stopwatch = Stopwatch.StartNew();
                    var inputs = await _inputParsingService.BuildNamedInputsAsync(
                        subtask.ParametersJson, cancellationToken);

                    inferenceResult = await _onnxRuntimeService.ExecuteOnnxModelAsync(
                        modelBytes, inputs, cancellationToken);
                }

                var processedOutputs = await _outputParsingService.ProcessOutputsAsync(
                    subtask.TaskId,
                    subtask.Id,
                    subtask.ParametersJson,
                    inferenceResult.Outputs,
                    authToken,
                    cancellationToken);

                stopwatch?.Stop();

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

        private async Task<byte[]> DownloadModelAsync(string uriString, CancellationToken cancellationToken)
        {
            if (TryCreateUri(uriString, out var uri))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"Invalid URI: {uriString}");
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
            public string? OptimizerModelUrl { get; init; }
            public string? CheckpointUrl { get; init; }
            public string? EvalModelUrl { get; init; }
            public int TaskType { get; init; }
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

        #region Partition Execution

        private sealed record PartitionExecutionItem(PartitionAssignment Assignment, Dictionary<string, ReceivedTensor>? InputTensors = null);

        private sealed class ReceivedTensor
        {
            public string Name { get; init; } = string.Empty;
            public TensorSerializer.TensorDataType DataType { get; init; }
            public int[] Shape { get; init; } = Array.Empty<int>();
            public byte[] Data { get; init; } = Array.Empty<byte>();
        }

        // Track received tensors for partitions waiting for input
        private readonly ConcurrentDictionary<Guid, Dictionary<string, ReceivedTensor>> _partitionInputBuffers = new();

        private async Task RunPartitionWorkerLoopAsync(CancellationToken cancellationToken)
        {
            var channel = _partitionChannel;
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
                            await ProcessPartitionExecutionAsync(item, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[BackgroundWorkService] Failed processing partition {item.Assignment.PartitionId}: {ex}");
                            
                            // Report failure
                            try
                            {
                                await _webRtcPeerService.ReportPartitionFailedAsync(
                                    item.Assignment.SubtaskId,
                                    item.Assignment.PartitionId,
                                    ex.Message,
                                    cancellationToken);
                            }
                            catch (Exception reportEx)
                            {
                                Debug.WriteLine($"[BackgroundWorkService] Failed to report partition failure: {reportEx}");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown.
            }
        }

        private void OnPartitionAssigned(object? sender, PartitionAssignedEventArgs e)
        {
            var assignment = e.Assignment;
            var isParentPeer = assignment.IsParentPeer;
            
            Debug.WriteLine($"[BackgroundWorkService] Partition assigned: {assignment.PartitionId} (index {assignment.PartitionIndex}, isParentPeer={isParentPeer})");

            // Initialize input buffer for this partition
            _partitionInputBuffers[assignment.PartitionId] = new Dictionary<string, ReceivedTensor>();
            
            // Store assignment for later use
            _activeAssignments[assignment.PartitionId] = assignment;

            if (isParentPeer)
            {
                // Parent peer: download full model, partition it, distribute subgraphs
                _ = Task.Run(() => ProcessParentPeerAssignmentAsync(assignment, _cts?.Token ?? CancellationToken.None));
            }
            else if (assignment.UpstreamPeer is null && assignment.PartitionIndex == 0)
            {
                // First partition but not parent peer - wait for subgraph from parent
                Debug.WriteLine($"[BackgroundWorkService] Partition {assignment.PartitionId} waiting for subgraph from parent peer");
            }
            else
            {
                // Non-parent peer: wait for subgraph from parent, then wait for input tensors
                Debug.WriteLine($"[BackgroundWorkService] Partition {assignment.PartitionId} waiting for subgraph from parent peer");
            }
        }
        
        // Store active partition assignments
        private readonly ConcurrentDictionary<Guid, PartitionAssignment> _activeAssignments = new();
        
        // Store received subgraphs
        private readonly ConcurrentDictionary<Guid, byte[]> _receivedSubgraphs = new();

        private void OnTensorReceived(object? sender, TensorReceivedEventArgs e)
        {
            Debug.WriteLine($"[BackgroundWorkService] Tensor received: {e.TensorName} from partition {e.FromPartitionId}");

            // Find which partition this tensor is for based on upstream relationships
            foreach (var kvp in _partitionInputBuffers)
            {
                var partitionId = kvp.Key;
                var inputBuffer = kvp.Value;

                // Check if this partition expects this tensor
                // We'll queue the partition for execution once all expected inputs are received
                inputBuffer[e.TensorName] = new ReceivedTensor
                {
                    Name = e.TensorName,
                    DataType = e.DataType,
                    Shape = e.Shape,
                    Data = e.Data
                };

                // Check if all inputs are received - this would need the assignment info
                // For now, queue execution when any input is received (simplified)
            }
        }

        private void OnPartitionsReady(object? sender, PartitionsReadyEventArgs e)
        {
            Debug.WriteLine($"[BackgroundWorkService] Partitions ready for subtask {e.Notification.SubtaskId}");
            
            // All peer connections established and subgraphs distributed, partitions can start executing
            // Find our partition for this subtask and queue it for execution
            foreach (var kvp in _activeAssignments)
            {
                var assignment = kvp.Value;
                if (assignment.SubtaskId == e.Notification.SubtaskId)
                {
                    // Queue for execution if we have the subgraph
                    if (_receivedSubgraphs.ContainsKey(assignment.PartitionId) || assignment.IsParentPeer)
                    {
                        var channel = _partitionChannel;
                        if (channel is not null)
                        {
                            var inputTensors = _partitionInputBuffers.TryGetValue(assignment.PartitionId, out var inputs) ? inputs : null;
                            channel.Writer.TryWrite(new PartitionExecutionItem(assignment, inputTensors));
                        }
                    }
                }
            }
        }
        
        private void OnSubgraphReceived(object? sender, SubgraphReceivedEventArgs e)
        {
            Debug.WriteLine($"[BackgroundWorkService] Subgraph received for partition {e.PartitionId}: {e.SubgraphBytes.Length} bytes, valid={e.IsValid}");
            
            if (!e.IsValid)
            {
                Debug.WriteLine($"[BackgroundWorkService] Invalid subgraph received for partition {e.PartitionId}");
                return;
            }
            
            // Store the received subgraph
            _receivedSubgraphs[e.PartitionId] = e.SubgraphBytes;
            
            // If this partition is the first in pipeline (no upstream), queue it for execution
            if (_activeAssignments.TryGetValue(e.PartitionId, out var assignment))
            {
                if (assignment.UpstreamPeer is null && assignment.PartitionIndex == 0)
                {
                    var channel = _partitionChannel;
                    if (channel is not null)
                    {
                        channel.Writer.TryWrite(new PartitionExecutionItem(assignment));
                    }
                }
            }
        }
        
        /// <summary>
        /// Parent peer workflow: download full model, partition it, distribute subgraphs to children.
        /// </summary>
        private async Task ProcessParentPeerAssignmentAsync(PartitionAssignment assignment, CancellationToken cancellationToken)
        {
            var connection = await WaitForActiveConnectionAsync(cancellationToken);
            
            Debug.WriteLine($"[BackgroundWorkService] Parent peer starting for partition {assignment.PartitionId}");
            
            try
            {
                // Step 1: Download full ONNX model from blob storage
                if (string.IsNullOrEmpty(assignment.OnnxFullModelBlobUri))
                {
                    throw new InvalidOperationException("Parent peer assignment missing OnnxFullModelBlobUri");
                }
                
                Debug.WriteLine($"[BackgroundWorkService] Parent peer downloading full model from {assignment.OnnxFullModelBlobUri}");
                await connection.InvokeAsync("ReportModelDownloadProgress", assignment.SubtaskId, assignment.PartitionId, 0, 0L, 0L, cancellationToken);
                
                var fullModelBytes = await DownloadModelAsync(assignment.OnnxFullModelBlobUri, cancellationToken);
                
                await connection.InvokeAsync("ReportModelDownloadProgress", assignment.SubtaskId, assignment.PartitionId, 100, fullModelBytes.LongLength, fullModelBytes.LongLength, cancellationToken);
                Debug.WriteLine($"[BackgroundWorkService] Parent peer downloaded model: {fullModelBytes.Length} bytes");
                
                // Step 2: Partition the model using OnnxPartitionerService
                var totalPartitions = assignment.TotalPartitions;
                Debug.WriteLine($"[BackgroundWorkService] Parent peer partitioning model into {totalPartitions} partitions");
                
                await connection.InvokeAsync("ReportPartitioningProgress", assignment.SubtaskId, assignment.PartitionId, 0, 0, totalPartitions, cancellationToken);
                
                var subgraphs = await _onnxPartitionerService.PartitionModelAsync(fullModelBytes, totalPartitions, cancellationToken);
                
                await connection.InvokeAsync("ReportPartitioningProgress", assignment.SubtaskId, assignment.PartitionId, 100, totalPartitions, totalPartitions, cancellationToken);
                Debug.WriteLine($"[BackgroundWorkService] Parent peer created {subgraphs.Count} subgraphs");
                
                // Step 3: Distribute subgraphs to child peers via WebRTC
                if (assignment.ChildPeers is not null && assignment.ChildPeers.Count > 0)
                {
                    var childPartitionIds = assignment.ChildPeers.Select(c => c.PartitionId).ToArray();
                    var subgraphSizes = new long[subgraphs.Count];
                    for (int i = 0; i < subgraphs.Count; i++)
                    {
                        subgraphSizes[i] = subgraphs[i].SubgraphBytes.Length;
                    }
                    
                    await connection.InvokeAsync("ReportSubgraphDistributionStart",
                        assignment.SubtaskId,
                        assignment.PartitionId,
                        childPartitionIds,
                        subgraphSizes,
                        cancellationToken);
                    
                    // Distribute subgraphs to each child peer
                    for (int i = 0; i < assignment.ChildPeers.Count && i < subgraphs.Count; i++)
                    {
                        var childPeer = assignment.ChildPeers[i];
                        var subgraph = subgraphs.FirstOrDefault(s => s.PartitionIndex == childPeer.PartitionIndex)
                                       ?? subgraphs[i];
                        
                        Debug.WriteLine($"[BackgroundWorkService] Distributing subgraph to child partition {childPeer.PartitionId} (index {childPeer.PartitionIndex})");
                        
                        await _webRtcPeerService.SendSubgraphAsync(
                            assignment.SubtaskId,
                            assignment.PartitionId,
                            childPeer.PartitionId,
                            childPeer.PartitionIndex,
                            subgraph.SubgraphBytes,
                            subgraph.InputNames.ToList(),
                            subgraph.OutputNames.ToList(),
                            cancellationToken);
                    }
                    
                    Debug.WriteLine($"[BackgroundWorkService] Parent peer finished distributing subgraphs");
                }
                
                // Step 4: Store our own subgraph for execution
                var parentSubgraph = subgraphs.FirstOrDefault(s => s.PartitionIndex == assignment.PartitionIndex)
                                     ?? subgraphs[0];
                _receivedSubgraphs[assignment.PartitionId] = parentSubgraph.SubgraphBytes;
                
                // Step 5: Queue parent peer's partition for execution (it will run after WebRTC connections are ready)
                var channel = _partitionChannel;
                if (channel is not null)
                {
                    Debug.WriteLine($"[BackgroundWorkService] Parent peer queueing own partition for execution");
                    channel.Writer.TryWrite(new PartitionExecutionItem(assignment));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundWorkService] Parent peer failed: {ex}");
                
                try
                {
                    await _webRtcPeerService.ReportPartitionFailedAsync(
                        assignment.SubtaskId,
                        assignment.PartitionId,
                        $"Parent peer failed: {ex.Message}",
                        cancellationToken);
                }
                catch (Exception reportEx)
                {
                    Debug.WriteLine($"[BackgroundWorkService] Failed to report parent peer failure: {reportEx}");
                }
            }
        }

        private async Task ProcessPartitionExecutionAsync(PartitionExecutionItem item, CancellationToken cancellationToken)
        {
            var assignment = item.Assignment;
            var authToken = Volatile.Read(ref _authToken);
            if (authToken == null)
            {
                throw new InvalidOperationException("No auth token available");
            }

            Debug.WriteLine($"[BackgroundWorkService] Processing partition {assignment.PartitionId} (index {assignment.PartitionIndex}/{assignment.TotalPartitions}, isParentPeer={assignment.IsParentPeer})");

            // Report progress: starting
            await _webRtcPeerService.ReportPartitionProgressAsync(assignment.SubtaskId, assignment.PartitionId, 5, cancellationToken);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Get the subgraph bytes - either from cache (WebRTC received) or download if legacy mode
                byte[] subgraphBytes;
                
                if (_receivedSubgraphs.TryGetValue(assignment.PartitionId, out var cachedSubgraph))
                {
                    // Use subgraph received from parent peer via WebRTC
                    subgraphBytes = cachedSubgraph;
                    Debug.WriteLine($"[BackgroundWorkService] Using cached subgraph for partition {assignment.PartitionId} ({subgraphBytes.Length} bytes)");
                }
                else if (!string.IsNullOrEmpty(assignment.OnnxSubgraphBlobUri))
                {
                    // Fallback: download from blob storage (legacy mode)
                    Debug.WriteLine($"[BackgroundWorkService] Downloading subgraph from blob storage for partition {assignment.PartitionId}");
                    subgraphBytes = await DownloadModelAsync(assignment.OnnxSubgraphBlobUri, cancellationToken);
                }
                else
                {
                    throw new InvalidOperationException($"No subgraph available for partition {assignment.PartitionId}. Expected from parent peer via WebRTC.");
                }
                
                await _webRtcPeerService.ReportPartitionProgressAsync(assignment.SubtaskId, assignment.PartitionId, 20, cancellationToken);

                // Build inputs
                Dictionary<string, OnnxInput> inputs;
                
                if (assignment.PartitionIndex == 0)
                {
                    // First partition: get inputs from task parameters
                    // For now, use empty inputs - real implementation would get from subtask parameters
                    inputs = new Dictionary<string, OnnxInput>();
                    foreach (var inputName in assignment.InputTensorNames)
                    {
                        // In a real implementation, we'd get the actual input data
                        Debug.WriteLine($"[BackgroundWorkService] Partition {assignment.PartitionId} expecting input: {inputName}");
                    }
                }
                else
                {
                    // Subsequent partitions: get inputs from received tensors
                    inputs = new Dictionary<string, OnnxInput>();
                    if (item.InputTensors is not null)
                    {
                        foreach (var kvp in item.InputTensors)
                        {
                            inputs[kvp.Key] = new OnnxInput
                            {
                                Name = kvp.Key,
                                Data = kvp.Value.Data,
                                Shape = kvp.Value.Shape
                            };
                        }
                    }
                }

                await _webRtcPeerService.ReportPartitionProgressAsync(assignment.SubtaskId, assignment.PartitionId, 40, cancellationToken);

                // Execute the subgraph
                var inferenceResult = await _onnxRuntimeService.ExecuteOnnxModelAsync(subgraphBytes, inputs, cancellationToken);

                await _webRtcPeerService.ReportPartitionProgressAsync(assignment.SubtaskId, assignment.PartitionId, 70, cancellationToken);

                // Process outputs
                if (assignment.DownstreamPeer is not null)
                {
                    // Send outputs to downstream partition
                    foreach (var output in inferenceResult.Outputs)
                    {
                        if (assignment.OutputTensorNames.Contains(output.Name))
                        {
                            var dataType = MapToTensorDataType(output.ElementType);
                            await _webRtcPeerService.SendTensorAsync(
                                assignment.SubtaskId,
                                assignment.PartitionId,
                                assignment.DownstreamPeer.PartitionId,
                                output.Name,
                                dataType,
                                output.Shape,
                                output.Data,
                                cancellationToken);
                        }
                    }

                    // Report partition ready (intermediate partition)
                    var totalOutputSize = inferenceResult.Outputs.Sum(o => o.Data.Length);
                    await _webRtcPeerService.ReportPartitionReadyAsync(
                        assignment.SubtaskId,
                        assignment.PartitionId,
                        assignment.OutputTensorNames.ToArray(),
                        totalOutputSize,
                        cancellationToken);
                }
                else
                {
                    // Final partition: report completion
                    stopwatch.Stop();

                    var resultPayload = new
                    {
                        partitionId = assignment.PartitionId,
                        partitionIndex = assignment.PartitionIndex,
                        completedAtUtc = DateTimeOffset.UtcNow,
                        metrics = new
                        {
                            durationSeconds = stopwatch.Elapsed.TotalSeconds,
                            device = _onnxRuntimeService.GetExecutionProvider().ToString().ToLowerInvariant()
                        },
                        outputs = inferenceResult.Outputs.Select(o => new
                        {
                            name = o.Name,
                            shape = o.Shape,
                            sizeBytes = o.Data.Length
                        }).ToList()
                    };

                    var resultJson = JsonSerializer.Serialize(resultPayload, SerializerOptions);
                    await _webRtcPeerService.ReportPartitionCompletedAsync(
                        assignment.SubtaskId,
                        assignment.PartitionId,
                        resultJson,
                        cancellationToken);
                }

                await _webRtcPeerService.ReportPartitionProgressAsync(assignment.SubtaskId, assignment.PartitionId, 100, cancellationToken);

                // Cleanup input buffer
                _partitionInputBuffers.TryRemove(assignment.PartitionId, out _);

                Debug.WriteLine($"[BackgroundWorkService] Partition {assignment.PartitionId} completed successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundWorkService] Partition {assignment.PartitionId} failed: {ex}");
                throw;
            }
        }

        private static TensorSerializer.TensorDataType MapToTensorDataType(Type elementType)
        {
            return elementType.Name switch
            {
                "Single" => TensorSerializer.TensorDataType.Float32,
                "Half" => TensorSerializer.TensorDataType.Float16,
                "Double" => TensorSerializer.TensorDataType.Float64,
                "Int32" => TensorSerializer.TensorDataType.Int32,
                "Int64" => TensorSerializer.TensorDataType.Int64,
                "Int16" => TensorSerializer.TensorDataType.Int16,
                "SByte" => TensorSerializer.TensorDataType.Int8,
                "Byte" => TensorSerializer.TensorDataType.UInt8,
                "UInt32" => TensorSerializer.TensorDataType.UInt32,
                "UInt64" => TensorSerializer.TensorDataType.UInt64,
                "Boolean" => TensorSerializer.TensorDataType.Bool,
                _ => TensorSerializer.TensorDataType.Float32
            };
        }

        private sealed class OnnxInput
        {
            public string Name { get; init; } = string.Empty;
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public int[] Shape { get; init; } = Array.Empty<int>();
        }

        #endregion

    }
    public enum ExecutionProviderDevice
    {
        Cpu,
        Gpu,
        Npu
    }

}
