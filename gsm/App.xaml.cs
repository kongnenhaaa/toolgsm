using System;
using System.Windows;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using gsm.Services;
using gsm.ViewModels;

namespace gsm
{
    public partial class App : Application
    {
        private ServiceProvider? _serviceProvider;
        private MainViewModel? _mainViewModel;
        private RealDeviceSmokeTestRunner? _realDeviceSmokeRunner;
        private CancellationTokenSource? _realDeviceSmokeCts;
        private Task? _realDeviceSmokeTask;
        private ToolGsmApiService? _toolGsmApiService;
        private CancellationTokenSource? _toolGsmApiCts;
        private Task? _toolGsmApiTask;

        public App()
        {
            RegisterGlobalExceptionHandlers();

            BackendConcurrency.ConfigureThreadPool();

            // CustomBlazorWebView resolves wwwroot from AppContext.BaseDirectory.
            // Do not make the publish folder the process current directory:
            // WebView2 child processes inherit it and can keep that folder locked
            // briefly after the main window has closed.

            // TỰ TẠO FILE + FOLDER TRƯỚC MỌI THỨ
            gsm.Services.AppBootstrap.EnsureAll();

            var serviceCollection = new ServiceCollection();

            serviceCollection.AddWpfBlazorWebView();
            serviceCollection.AddMudServices(config =>
            {
                config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight;
                config.SnackbarConfiguration.PreventDuplicates = false;
                config.SnackbarConfiguration.NewestOnTop = true;
                config.SnackbarConfiguration.ShowCloseIcon = true;
                config.SnackbarConfiguration.VisibleStateDuration = 4000;
                config.SnackbarConfiguration.HideTransitionDuration = 300;
                config.SnackbarConfiguration.ShowTransitionDuration = 300;
            });

            // ===== ĐĂNG KÝ BACKEND CŨ =====
            serviceCollection.AddSingleton<IGsmModemService, GsmModemService>();
            serviceCollection.AddSingleton<IPortSessionRegistry, PortSessionRegistry>();
            serviceCollection.AddSingleton<IGsmOperationDelay, GsmOperationDelay>();
            serviceCollection.AddSingleton<IGsmSmsService, GsmSmsService>();
            serviceCollection.AddSingleton<IGsmUssdService, GsmUssdService>();
            serviceCollection.AddSingleton<IGsmCallService, GsmCallService>();
            serviceCollection.AddSingleton<IGsmBackgroundSupervisor, GsmBackgroundSupervisor>();
            serviceCollection.AddSingleton<MainViewModel>();
            serviceCollection.AddSingleton<RealDeviceSmokeTestRunner>();
            serviceCollection.AddSingleton<ToolGsmApiService>();
            serviceCollection.AddSingleton<IFileDialogService, FileDialogService>();
            serviceCollection.AddSingleton<IAudioService, AudioService>();
            serviceCollection.AddSingleton<INotifyService, NotifyService>();
            serviceCollection.AddSingleton<IFirebaseOtpService, FirebaseOtpService>();

            _serviceProvider = serviceCollection.BuildServiceProvider();
            Resources.Add("services", _serviceProvider);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Resolve từ chính container mà Blazor sử dụng. Container sẽ gọi
            // MainViewModel.Dispose() khi ứng dụng thoát.
            _mainViewModel = _serviceProvider?.GetRequiredService<MainViewModel>()
                ?? throw new InvalidOperationException("Application services are not available.");

            // Inert unless an explicit one-shot request argument is present.
            // The runner reuses MainViewModel's guarded modem pipelines and
            // never opens a second serial connection.
            _realDeviceSmokeRunner = _serviceProvider
                .GetRequiredService<RealDeviceSmokeTestRunner>();
            _realDeviceSmokeCts = new CancellationTokenSource();
            _realDeviceSmokeTask = _realDeviceSmokeRunner
                .RunIfRequestedAsync(e.Args, _realDeviceSmokeCts.Token);
            _ = ObserveRealDeviceSmokeTaskAsync(_realDeviceSmokeTask);

            _toolGsmApiService = _serviceProvider
                .GetRequiredService<ToolGsmApiService>();
            _toolGsmApiCts = new CancellationTokenSource();
            _toolGsmApiTask = _toolGsmApiService
                .RunAsync(_toolGsmApiCts.Token);
            _ = ObserveToolGsmApiTaskAsync(_toolGsmApiTask);
        }

        private async Task ObserveRealDeviceSmokeTaskAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // App shutdown owns cancellation. The runner checkpoints when
                // the process remains alive long enough to finish its handler.
            }
            catch (Exception ex)
            {
                LogCrash(ex, "Real_Device_Smoke_Runner");
            }
        }

        private async Task ObserveToolGsmApiTaskAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _mainViewModel?.AddLog(
                    $"[LOCAL_API_FAILED] {ex.Message}", "ERROR");
                LogCrash(ex, "ToolGSM_Local_API");
            }
        }

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }

        private void UnregisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        }

        private void OnDispatcherUnhandledException(
            object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
        {
            LogCrash(args.Exception, "UI_Thread");
            args.Handled = true; // Ngăn app văng
        }

        private void OnUnobservedTaskException(
            object? sender,
            System.Threading.Tasks.UnobservedTaskExceptionEventArgs args)
        {
            LogCrash(args.Exception, "Task_Background");
            args.SetObserved();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
        {
            LogCrash(args.ExceptionObject as Exception, "AppDomain_Unhandled");
        }

        private void LogCrash(Exception? ex, string source)
        {
            if (ex == null) return;
            try
            {
                string logFile = System.IO.Path.Combine(System.AppContext.BaseDirectory, "crash.log");
                var fi = new System.IO.FileInfo(logFile);
                if (fi.Exists && fi.Length > 1024 * 1024) // 1MB
                {
                    fi.Delete();
                }
                
                string content = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\r\n{ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}\r\n\r\n";
                if (ex.InnerException != null)
                {
                    content += $"Inner Exception:\r\n{ex.InnerException.GetType().Name}: {ex.InnerException.Message}\r\n{ex.InnerException.StackTrace}\r\n\r\n";
                }
                System.IO.File.AppendAllText(logFile, content);
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                try { _toolGsmApiCts?.Cancel(); } catch { }
                try { _realDeviceSmokeCts?.Cancel(); } catch { }

                WaitForTaskBounded(
                    _toolGsmApiTask,
                    TimeSpan.FromSeconds(5));

                // Give the runner's cancellation path time to issue ATH, confirm
                // an empty CLCC snapshot and persist Ambiguous/Cancelled before
                // the DI container closes every serial handle.
                WaitForTaskBounded(
                    _realDeviceSmokeTask,
                    TimeSpan.FromSeconds(9));

                string emergencyCallPort =
                    _realDeviceSmokeRunner?.PotentialActiveCallPort
                    ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(emergencyCallPort)
                    && _mainViewModel?.ModemService is { } modem)
                {
                    using var emergencyCts = new CancellationTokenSource(
                        TimeSpan.FromSeconds(6));
                    Task emergencyHangup = Task.Run(
                        () => EmergencyHangUpBeforeDisconnectAsync(
                            modem,
                            emergencyCallPort,
                            emergencyCts.Token),
                        emergencyCts.Token);
                    WaitForTaskBounded(
                        emergencyHangup,
                        TimeSpan.FromSeconds(6));
                }

                _realDeviceSmokeCts?.Dispose();
                _realDeviceSmokeCts = null;
                _realDeviceSmokeTask = null;
                _realDeviceSmokeRunner = null;

                _toolGsmApiCts?.Dispose();
                _toolGsmApiCts = null;
                _toolGsmApiTask = null;
                _toolGsmApiService = null;

                // Không gọi MainViewModel.Dispose() trực tiếp: ServiceProvider sở hữu
                // singleton này và là đường cleanup duy nhất của App.
                TryDisposeServiceProvider("Shutdown_Dispose");
            }
            finally
            {
                _mainViewModel = null;
                UnregisterGlobalExceptionHandlers();
                base.OnExit(e);
            }
        }

        internal static bool WaitForTaskBounded(Task? task, TimeSpan timeout)
        {
            if (task == null) return true;
            try
            {
                return task.Wait(timeout);
            }
            catch (AggregateException)
            {
                // The observer owns crash logging. A completed faulted task no
                // longer needs serial resources, so shutdown can continue.
                return true;
            }
        }

        private static async Task EmergencyHangUpBeforeDisconnectAsync(
            IGsmModemService modem,
            string portName,
            CancellationToken cancellationToken)
        {
            try
            {
                await modem.SendCommandAsync(
                    portName,
                    "ATH",
                    timeoutMs: 3000,
                    silent: true,
                    ct: cancellationToken).ConfigureAwait(false);

                // The result is intentionally not treated as an acceptance-test
                // marker. This is a shutdown safety fallback whose only job is
                // to make a final bounded attempt before COM is closed.
                await modem.SendCommandAsync(
                    portName,
                    "AT+CLCC",
                    timeoutMs: 2000,
                    silent: true,
                    ct: cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Provider disposal is still required even if USB vanished.
            }
        }

        private void TryDisposeServiceProvider(string errorSource)
        {
            try
            {
                DisposeServiceProviderOnce(ref _serviceProvider);
            }
            catch (Exception ex)
            {
                LogCrash(ex, errorSource);
            }
        }

        internal static void DisposeServiceProviderOnce(ref ServiceProvider? serviceProvider) =>
            System.Threading.Interlocked.Exchange(ref serviceProvider, null)?.Dispose();
    }
}
