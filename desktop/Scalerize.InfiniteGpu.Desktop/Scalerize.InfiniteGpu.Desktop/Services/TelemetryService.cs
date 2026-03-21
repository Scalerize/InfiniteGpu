using System;
using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace Scalerize.InfiniteGpu.Desktop.Services
{
    /// <summary>
    /// Wraps <see cref="TelemetryClient"/> so it can be injected through DI,
    /// configured once, and flushed on shutdown.
    /// </summary>
    public sealed class TelemetryService : IDisposable
    {
        private const string ConnectionString =
            "InstrumentationKey=a31584fb-4432-4b70-a17a-1df348caf81a;" +
            "IngestionEndpoint=https://francecentral-1.in.applicationinsights.azure.com/;" +
            "LiveEndpoint=https://francecentral.livediagnostics.monitor.azure.com/;" +
            "ApplicationId=ab593dd8-6b84-47b5-8871-95e2e1ec9d6e";

        private readonly TelemetryConfiguration _configuration;
        private readonly TelemetryClient _client;

        public TelemetryService()
        {
            _configuration = TelemetryConfiguration.CreateDefault();
            _configuration.ConnectionString = ConnectionString;

            _client = new TelemetryClient(_configuration);
            _client.Context.Device.OperatingSystem = Environment.OSVersion.ToString();
            _client.Context.Component.Version =
                typeof(TelemetryService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        /// <summary>
        /// The underlying <see cref="TelemetryClient"/> for advanced scenarios.
        /// </summary>
        public TelemetryClient Client => _client;

        /// <summary>
        /// Tracks an exception with optional custom properties.
        /// </summary>
        public void TrackException(Exception exception, IDictionary<string, string>? properties = null)
        {
            var telemetry = new ExceptionTelemetry(exception);
            if (properties is not null)
            {
                foreach (var kvp in properties)
                {
                    telemetry.Properties[kvp.Key] = kvp.Value;
                }
            }

            _client.TrackException(telemetry);
        }

        /// <summary>
        /// Tracks a custom event.
        /// </summary>
        public void TrackEvent(string eventName, IDictionary<string, string>? properties = null)
        {
            _client.TrackEvent(eventName, properties);
        }

        /// <summary>
        /// Flushes any buffered telemetry and tears down the configuration.
        /// </summary>
        public void Dispose()
        {
            _client.Flush();
            _configuration.Dispose();
        }
    }
}
