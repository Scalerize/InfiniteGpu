using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Hardware.Info;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Scalerize.InfiniteGpu.Desktop.Constants;
using Scalerize.InfiniteGpu.Desktop.Services;

namespace Scalerize.InfiniteGpu.Desktop
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        // P/Invoke declarations for window activation
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;
        private const string AppWindowTitle = "InfiniteGpu";
        private const string SingleInstanceKey = "Scalerize.InfiniteGpu.Desktop.Main";

        private Window? _window;
        private IServiceProvider? _serviceProvider;

        public static IServiceProvider Services =>
            (Current as App)?._serviceProvider ?? throw new InvalidOperationException("The service provider has not been initialized.");

        /// <summary>
        /// Initializes the singleton application object. This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            // Use AppInstance API for single instance with activation redirect.
            // This ensures protocol activation URIs are forwarded to the running instance.
            var mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

            if (!mainInstance.IsCurrent)
            {
                // Another instance is already running — redirect our activation to it
                var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                mainInstance.RedirectActivationToAsync(activatedArgs).AsTask().GetAwaiter().GetResult();

                // Also bring the existing window to the foreground as a fallback
                ActivateExistingWindow();
                Process.GetCurrentProcess().Kill();
                return;
            }

            // Main instance: listen for activation redirects from secondary instances
            mainInstance.Activated += OnInstanceActivated;
        }

        /// <summary>
        /// Handles activation events redirected from secondary instances (e.g., protocol deep links
        /// while the app is already running).
        /// </summary>
        private void OnInstanceActivated(object? sender, AppActivationArguments args)
        {
            try
            {
                if (args.Kind == ExtendedActivationKind.Protocol)
                {
                    var protocolArgs = (Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs)args.Data;
                    if (protocolArgs.Uri.Scheme == "infinitegpu" && _window is MainWindow mainWindow)
                    {
                        // Activated event fires on a background thread — dispatch to UI thread
                        mainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            mainWindow.NavigateTo(protocolArgs.Uri);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to handle redirected activation: {ex}");
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _serviceProvider = ConfigureServices();
            _window = _serviceProvider.GetRequiredService<MainWindow>();
            _window.Closed += OnWindowClosed;
            _window.Activate();

            try
            {
                var activatedEventArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (activatedEventArgs.Kind == ExtendedActivationKind.Protocol)
                {
                    var protocolArgs = (Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs)activatedEventArgs.Data;
                    if (protocolArgs.Uri.Scheme == "infinitegpu")
                    {
                        if (_window is MainWindow mainWindow)
                        {
                            mainWindow.NavigateTo(protocolArgs.Uri);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Ignore activation errors
            }
        }

        /// <summary>
        /// Attempts to find and activate an existing application window.
        /// </summary>
        private static void ActivateExistingWindow()
        {
            try
            {
                // Try to find the window by title
                IntPtr hWnd = FindWindow(null, AppWindowTitle);

                if (hWnd == IntPtr.Zero)
                {
                    // Window not found, exit
                    return;
                }

                // If the window is minimized, restore it
                if (IsIconic(hWnd))
                {
                    ShowWindow(hWnd, SW_RESTORE);
                }
                else
                {
                    // Just show the window
                    ShowWindow(hWnd, SW_SHOW);
                }

                // Bring the window to the foreground
                SetForegroundWindow(hWnd);
            }
            catch
            {
                // Silently fail if we can't activate the window
            }
        }

        private async void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_window is not null)
            {
                _window.Closed -= OnWindowClosed;
            }

            if (_serviceProvider is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
                _serviceProvider = null;
            }
        }

        private static IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            services.AddSingleton<TelemetryService>();
            services.AddSingleton(_ => CreateHttpClient());
            services.AddSingleton<ModelCacheService>();
            services.AddSingleton<OnnxRuntimeService>();
            services.AddSingleton<OnnxParsingService>();
            services.AddSingleton<OnnxPartitionerService>();
            services.AddSingleton<OnnxSizeService>();
            services.AddSingleton<HardwareInfo>(); 
            services.AddSingleton<HardwareMetricsService>();
            services.AddSingleton<DeviceIdentifierService>();
            services.AddSingleton<WebViewCommunicationService>();
            services.AddSingleton<InputParsingService>();
            services.AddSingleton<OutputParsingService>();
            services.AddSingleton<BackgroundWorkService>();
            services.AddSingleton<TokenizerService>();
            services.AddSingleton<MainWindow>();

            return services.BuildServiceProvider();
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            };

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(2),
                BaseAddress = Constants.Constants.BackendBaseUri
            };
        }
    }
}