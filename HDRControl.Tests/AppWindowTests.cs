using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace HDRControl.Tests;

// Launches the real executable. xUnit runs the tests of one class sequentially, so they cannot collide.
public sealed class AppWindowTests : IDisposable
{
    private const string MutexName = "HDRControl.SingleInstance";
    private const string MainWindowClass = "WinUIDesktopWin32WindowClass";
    private const string DialogClass = "#32770";
    private const int SwHide = 0;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly List<Process> _started = [];

    public AppWindowTests()
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
    public void SecondInstance_ExitsSilently_FirstKeepsRunning()
    {
        var first = Launch();
        WaitForWindow(first, MainWindowClass);

        var second = Launch();

        Assert.True(second.WaitForExit(Timeout), "The second instance did not exit.");
        Assert.Equal(0, second.ExitCode);
        Assert.False(first.HasExited);
        Assert.NotEmpty(FindWindows(first, MainWindowClass));
    }

    [Fact]
    public void SecondInstance_ShowsHiddenWindowOfFirst()
    {
        var first = Launch();
        var window = WaitForWindow(first, MainWindowClass);
        ShowWindow(window, SwHide);
        Assert.Empty(FindWindows(first, MainWindowClass));

        var second = Launch();

        Assert.True(second.WaitForExit(Timeout), "The second instance did not exit.");
        WaitForWindow(first, MainWindowClass);
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

    [Fact]
    public void DisplayList_With20Displays_ScrollsFromTopToBottom()
    {
        var first = "Dummy Display 1";
        var last = "Dummy Display 20";
        var window = AutomationElement.FromHandle(WaitForWindow(Launch(dummyDisplays: 20), MainWindowClass));
        var firstRow = WaitForText(window, first);
        var lastRow = WaitForText(window, last);
        var list = FindScrollableParent(firstRow);
        var scroll = (ScrollPattern)list.GetCurrentPattern(ScrollPattern.Pattern);

        Assert.True(scroll.Current.VerticallyScrollable, "The display list cannot scroll.");
        Assert.Equal(0, scroll.Current.VerticalScrollPercent, 0.5);
        Assert.True(IsInside(list, firstRow), $"{first} should be visible at the top.");
        Assert.False(IsInside(list, lastRow), $"{last} should be below the visible area at the top.");

        scroll.SetScrollPercent(ScrollPattern.NoScroll, 100);
        WaitUntil(() => IsInside(list, lastRow), $"{last} did not scroll fully into view.");
        Assert.Equal(100, scroll.Current.VerticalScrollPercent, 0.5);
        Assert.False(IsInside(list, firstRow), $"{first} should be above the visible area at the bottom.");

        scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        WaitUntil(() => IsInside(list, firstRow), $"{first} did not scroll back into view.");
        Assert.Equal(0, scroll.Current.VerticalScrollPercent, 0.5);
    }

    private Process Launch(int? dummyDisplays = null)
    {
        var path = typeof(AppWindowTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "AppExecutable")
            .Value!;
        var startInfo = new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = false };
        if (dummyDisplays is not null)
        {
            startInfo.Environment["HDRCONTROL_DUMMY_DISPLAYS"] = dummyDisplays.ToString();
        }

        var process = Process.Start(startInfo)!;
        _started.Add(process);
        return process;
    }

    private static AutomationElement WaitForText(AutomationElement window, string text)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
            new PropertyCondition(AutomationElement.NameProperty, text));
        AutomationElement? element = null;
        WaitUntil(
            () => (element = window.FindFirst(TreeScope.Descendants, condition)) is not null,
            $"\"{text}\" did not appear.");
        return element!;
    }

    private static AutomationElement FindScrollableParent(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var parent = walker.GetParent(element); parent is not null; parent = walker.GetParent(parent))
        {
            if (parent.TryGetCurrentPattern(ScrollPattern.Pattern, out _))
            {
                return parent;
            }
        }

        throw new InvalidOperationException("The display list has no scrollable parent.");
    }

    private static bool IsInside(AutomationElement container, AutomationElement element) =>
        container.Current.BoundingRectangle.Contains(element.Current.BoundingRectangle);

    private static void WaitUntil(Func<bool> condition, string failure)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed >= Timeout)
            {
                throw new TimeoutException(failure);
            }

            Thread.Sleep(100);
        }
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
