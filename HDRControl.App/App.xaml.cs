using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using HDRControl.App.Services;

namespace HDRControl.App;

public partial class App : Application
{
    private const uint MessageBoxError = 0x00000010;
    private MainWindow? _window;
    // Held in a field so the mutex stays alive until the process exits.
    private Mutex? _singleInstanceMutex;

    public App()
    {
        DispatcherUnhandledException += (_, args) => ExceptionLog.Write("Unhandled UI exception", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ExceptionLog.Write("Unhandled exception", args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            ExceptionLog.Write("Unobserved task exception", args.Exception);
    }

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        try
        {
            var isAutoStart = args.Args.Contains(AutoStartService.StartupArgument);
            _singleInstanceMutex = new Mutex(true, "HDRControl.SingleInstance", out var isFirstInstance);
            if (!isFirstInstance)
            {
                if (!isAutoStart)
                {
                    SystemTrayService.RequestShowRunningInstance();
                }

                Shutdown();
                return;
            }

            if (SystemParameters.HighContrast)
            {
                ApplyHighContrastBrushes();
            }

            _window = new MainWindow();
            _window.Launch(isAutoStart);
        }
        catch (Exception error)
        {
            ShowStartupError(error);
            throw;
        }
    }

    private void ApplyHighContrastBrushes()
    {
        var window = SystemColors.WindowBrush;
        var text = SystemColors.WindowTextBrush;
        var highlight = SystemColors.HighlightBrush;
        string[] windowKeys =
        [
            "DashboardBackgroundBrush", "DashboardSidebarBrush", "DashboardCardBrush", "DashboardAccentSoftBrush",
            "DashboardToastBrush", "DashboardHotkeyBrush"
        ];
        string[] textKeys =
        [
            "DashboardCardBorderBrush", "DashboardGlassBorderBrush", "DashboardTextBrush", "DashboardMutedBrush",
            "DashboardHotkeyBorderBrush"
        ];
        foreach (var key in windowKeys) Resources[key] = window;
        foreach (var key in textKeys) Resources[key] = text;
        Resources["DashboardAccentBrush"] = highlight;
        Resources["DashboardNavigationSelectedBrush"] = highlight;
        Resources["DashboardOverlayBrush"] = Brushes.Transparent;
    }

    private static void ShowStartupError(Exception error)
    {
        ExceptionLog.Write("Startup error", error);
        MessageBoxW(IntPtr.Zero, error.ToString(), "HDR Control – Startup error", MessageBoxError);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(
        IntPtr owner,
        string text,
        string caption,
        uint type);
}
