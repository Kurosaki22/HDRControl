using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace HDRControl.App;

public partial class App : Application
{
    private const uint MessageBoxError = 0x00000010;
    private Window? _window;

    public App()
    {
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
            AppContext.BaseDirectory);

        try
        {
            InitializeComponent();
        }
        catch (Exception error)
        {
            ShowStartupError(error);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception error)
        {
            ShowStartupError(error);
            throw;
        }
    }

    private static void ShowStartupError(Exception error) =>
        MessageBoxW(IntPtr.Zero, error.ToString(), "HDR Control – Startup error", MessageBoxError);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(
        IntPtr owner,
        string text,
        string caption,
        uint type);
}
