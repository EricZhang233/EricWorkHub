using Microsoft.UI.Xaml;

namespace EricWorkHub
{
    public partial class App : Application
    {
        private Window? _window;

        public App()
        {
            ResourceManagerRequested += (_, args) => args.CustomResourceManager = EmbeddedXamlResources.CreateManager();
            UnhandledException += (_, e) =>
            {
                LogService.Write("App", "UnhandledException", e.Exception, level: LogService.LogLevel.Error);
                LogService.FlushAndStop();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                LogService.Write("App", "DomainUnhandledException", e.ExceptionObject as Exception, level: LogService.LogLevel.Error);
                LogService.FlushAndStop();
            };
            TaskScheduler.UnobservedTaskException += (_, e) => LogService.Write("App", "UnobservedTaskException", e.Exception, level: LogService.LogLevel.Error);
            InitializeComponent();
            LogService.Write("App", $"App constructed version={AppVersion.Version} os={Environment.OSVersion} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            using var operation = LogService.StartOperation("Startup", "OnLaunched");
            ConfigService.Initialize();
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            Program.Instance.StartServer(() => dispatcher.TryEnqueue(() =>
            {
                if (_window is MainWindow main) WindowActivator.Activate(main);
            }));
            _window = new MainWindow();
            _window.Activate();
        }
    }
}
