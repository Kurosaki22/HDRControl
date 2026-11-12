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
        // Also covers UI-thread crashes: the app never marks them handled, so they reach the AppDomain.
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

            // High contrast keeps its system colors; the window applies the saved theme only outside it.
            ApplyTheme(dark: false);
            if (SystemParameters.HighContrast)
            {
                ApplyHighContrastBrushes();
            }

            _window = new MainWindow();
            _window.Launch(isAutoStart);

            // Lets the UI tests crash the real executable to check the exception log.
            if (Environment.GetEnvironmentVariable("HDRCONTROL_TEST_CRASH") is { Length: > 0 } crashMessage)
            {
                Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException(crashMessage)));
            }
        }
        catch (Exception error)
        {
            ShowStartupError(error);
            throw;
        }
    }

    public static void ApplyTheme(bool dark)
    {
        var app = Current;
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml")
        };
        foreach (var key in palette.Keys)
        {
            app.Resources[key] = palette[key];
        }
    }

    private void ApplyHighContrastBrushes()
    {
        var window = SystemColors.WindowBrush;
        var text = SystemColors.WindowTextBrush;
        var highlight = SystemColors.HighlightBrush;
        string[] windowKeys =
        [
            "DashboardBackgroundBrush", "DashboardSidebarBrush", "DashboardCardBrush",
            "DashboardToastBrush", "DashboardHotkeyBrush", "DashboardThumbBrush"
        ];
        string[] textKeys =
        [
            "DashboardCardBorderBrush", "DashboardGlassBorderBrush", "DashboardTextBrush", "DashboardMutedBrush",
            "DashboardHotkeyBorderBrush", "DashboardTrackOffBrush", "DashboardBezelBrush"
        ];
        string[] highlightKeys =
        [
            "DashboardAccentBrush", "DashboardNavigationSelectedBrush", "DashboardAccentFillBrush",
            "DashboardTrackOnBrush"
        ];
        foreach (var key in windowKeys) Resources[key] = window;
        foreach (var key in textKeys) Resources[key] = text;
        foreach (var key in highlightKeys) Resources[key] = highlight;
        Resources["DashboardOnAccentBrush"] = SystemColors.HighlightTextBrush;
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
