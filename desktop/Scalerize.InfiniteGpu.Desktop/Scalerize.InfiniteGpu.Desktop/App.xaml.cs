using System;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Hardware.Info;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
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

        private Window? _window;
        private IServiceProvider? _serviceProvider;
        private static Mutex? _singleInstanceMutex;
        private const string AppMutexName = "Scalerize.InfiniteGpu.Desktop.SingleInstance";

        public static IServiceProvider Services =>
            (Current as App)?._serviceProvider ?? throw new InvalidOperationException("The service provider has not been initialized.");

        /// <summary>
        /// Initializes the singleton application object. This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            // Check for single instance
            _singleInstanceMutex = new Mutex(true, AppMutexName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is already running - activate existing window
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                ActivateExistingWindow();
                Environment.Exit(0);
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

            // Release the mutex when the window closes
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }

        private static IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            services.AddSingleton(_ => CreateHttpClient());
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