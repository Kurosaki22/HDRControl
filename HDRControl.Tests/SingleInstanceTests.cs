using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace HDRControl.Tests;

// Launches the real executable. xUnit runs the tests of one class sequentially, so they cannot collide.
public sealed class SingleInstanceTests : IDisposable
{
    private const string MutexName = "HDRControl.SingleInstance";
    private const string MainWindowClass = "WinUIDesktopWin32WindowClass";
    private const string DialogClass = "#32770";
    private const uint WmClose = 0x0010;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly List<Process> _started = [];

    public SingleInstanceTests()
    {
        if (Mutex.TryOpenExisting(MutexName, out var running))
        {
            running.Dispose();
            throw new InvalidOperationException("Quit HDR Control before running these tests.");
        }
    }

    public void Dispose()
    {
        foreach (var process in _started)
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }

            process.Dispose();
        }
    }

    [Fact]
    public void FirstInstance_OpensMainWindowAndHoldsLock()
    {
        var first = Launch();

        WaitForWindow(first, MainWindowClass);

        Assert.True(Mutex.TryOpenExisting(MutexName, out var held));
        held.Dispose();
    }

    [Fact]
    public void SecondInstance_ShowsNoticeAndExits_FirstKeepsRunning()
    {
        var first = Launch();
        WaitForWindow(first, MainWindowClass);

        var second = Launch();
        var notice = WaitForWindow(second, DialogClass);

        Assert.Contains("already running", DialogText(notice));
        Assert.Empty(FindWindows(second, MainWindowClass));

        Assert.True(PostMessageW(notice, WmClose, IntPtr.Zero, IntPtr.Zero));
        Assert.True(second.WaitForExit(Timeout), "The second instance did not exit.");
        Assert.Equal(0, second.ExitCode);
        Assert.False(first.HasExited);
        Assert.NotEmpty(FindWindows(first, MainWindowClass));
    }

    [Fact]
    public void AfterFirstInstanceEnds_AppCanStartAgain()
    {
        var first = Launch();
        WaitForWindow(first, MainWindowClass);
        first.Kill();
        first.WaitForExit();

        var next = Launch();

        WaitForWindow(next, MainWindowClass);
    }

    private Process Launch()
    {
        var path = typeof(SingleInstanceTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "AppExecutable")
            .Value!;
        var process = Process.Start(Path.GetFullPath(path));
        _started.Add(process);
        return process;
    }

    private static IntPtr WaitForWindow(Process process, string className)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Timeout)
        {
            var window = FindWindows(process, className).FirstOrDefault();
            if (window != IntPtr.Zero)
            {
                return window;
            }

            Assert.False(process.HasExited, $"The app exited with code {(process.HasExited ? process.ExitCode : 0)} before showing {className}.");
            Thread.Sleep(100);
        }

        // A startup error box would otherwise only surface as a bare timeout.
        var dialogs = FindWindows(process, DialogClass).Select(DialogText);
        throw new TimeoutException($"No {className} window appeared. Open dialogs: [{string.Join(" | ", dialogs)}]");
    }

    private static List<IntPtr> FindWindows(Process process, string className)
    {
        var windows = new List<IntPtr>();
        var enumerated = EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == process.Id && IsWindowVisible(window))
            {
                var name = new StringBuilder(256);
                if (GetClassNameW(window, name, name.Capacity) > 0 && name.ToString() == className)
                {
                    windows.Add(window);
                }
            }

            return true;
        }, IntPtr.Zero);
        if (!enumerated)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return windows;
    }

    private static string DialogText(IntPtr dialog)
    {
        // The icon of a message box is an empty Static control in front of the text.
        var text = new StringBuilder();
        var label = IntPtr.Zero;
        while ((label = FindWindowExW(dialog, label, "Static", null)) != IntPtr.Zero)
        {
            var part = new StringBuilder(4096);
            GetWindowTextW(label, part, part.Capacity);
            text.Append(part);
        }

        return text.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
