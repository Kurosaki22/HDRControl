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
    // WPF names its window classes "HwndWrapper[<process>;;<guid>]".
    private const string MainWindowClass = "HwndWrapper[HDRControl;";
    private const string DialogClass = "#32770";
    private const int SwHide = 0;
    private const byte VkControl = 0x11;
    private const byte VkMenu = 0x12;
    private const uint KeyEventKeyUp = 0x0002;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly List<Process> _started = [];

    // Every test starts from empty settings, never the user's; launches within one test share them.
    private readonly string _settingsDirectory =
        Path.Combine(Path.GetTempPath(), $"HDRControl.Tests.{Guid.NewGuid():N}");

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

        if (Directory.Exists(_settingsDirectory))
        {
            Directory.Delete(_settingsDirectory, recursive: true);
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

    [Fact]
    public void Hotkey_AlreadyUsed_KeepsPreviousHotkey()
    {
        var window = AutomationElement.FromHandle(WaitForWindow(Launch(dummyDisplays: 3), MainWindowClass));
        WaitForText(window, "Dummy Display 3");

        PressCtrlAlt(window, row: 1, '2');

        WaitForText(window, "Hotkey already in use");
        Assert.Equal("Ctrl+Alt+1", HotkeyBoxes(window)[1].Value());
    }

    [Fact]
    public void Hotkey_Free_IsAssigned()
    {
        var window = AutomationElement.FromHandle(WaitForWindow(Launch(dummyDisplays: 3), MainWindowClass));
        WaitForText(window, "Dummy Display 3");

        PressCtrlAlt(window, row: 1, '7');

        WaitUntil(() => HotkeyBoxes(window)[1].Value() == "Ctrl+Alt+7", "The free hotkey was not assigned.");
    }

    [Fact]
    public void ResetHotkeys_RestoresDefaults()
    {
        var window = AutomationElement.FromHandle(WaitForWindow(Launch(dummyDisplays: 3), MainWindowClass));
        WaitForText(window, "Dummy Display 3");
        PressCtrlAlt(window, row: 1, '7');
        WaitUntil(() => HotkeyBoxes(window)[1].Value() == "Ctrl+Alt+7", "The free hotkey was not assigned.");

        InvokeButton(window, "Settings");
        InvokeButton(window, "Reset all hotkeys");
        InvokeButton(window, "Reset hotkeys");
        WaitForText(window, "Hotkeys reset");
        InvokeButton(window, "Displays");

        WaitUntil(() => HotkeyBoxes(window).Count == 4, "The hotkey fields did not come back.");
        Assert.Equal(
            ["Ctrl+Alt+0", "Ctrl+Alt+1", "Ctrl+Alt+2", "Ctrl+Alt+3"],
            HotkeyBoxes(window).Select(box => box.Value()));
    }

    private static List<AutomationElement> HotkeyBoxes(AutomationElement window) =>
        window.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit))
            .Cast<AutomationElement>()
            .ToList();

    // Keystrokes go to whichever window is in front, so they must never be sent before the app is.
    private static void BringToForeground(IntPtr window)
    {
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        AttachThreadInput(currentThread, foregroundThread, true);
        SetForegroundWindow(window);
        AttachThreadInput(currentThread, foregroundThread, false);
        WaitUntil(() => GetForegroundWindow() == window, "The app window did not come to the foreground.");
    }

    private static void PressCtrlAlt(AutomationElement window, int row, char digit)
    {
        BringToForeground(new IntPtr(window.Current.NativeWindowHandle));
        HotkeyBoxes(window)[row].SetFocus();
        WaitUntil(() => HotkeyBoxes(window)[row].Current.HasKeyboardFocus, "The hotkey field did not get focus.");
        byte[] keys = [VkControl, VkMenu, (byte)digit];
        try
        {
            foreach (var key in keys)
            {
                keybd_event(key, 0, 0, UIntPtr.Zero);
            }
        }
        finally
        {
            foreach (var key in keys.Reverse())
            {
                keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
            }
        }
    }

    private static void InvokeButton(AutomationElement window, string name) =>
        ((InvokePattern)WaitForButton(window, name).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static AutomationElement WaitForButton(AutomationElement window, string name)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name));
        AutomationElement? button = null;
        WaitUntil(
            () => (button = window.FindFirst(TreeScope.Descendants, condition)) is not null,
            $"The button \"{name}\" did not appear.");
        return button!;
    }

    [Fact]
    public void ReconnectedDisplay_GetsItsSavedHotkeyBack()
    {
        var window = LaunchWithDisplays(3);
        PressCtrlAlt(window, row: 3, '7');
        WaitUntil(() => HotkeyBoxes(window)[3].Value() == "Ctrl+Alt+7", "The hotkey was not assigned.");
        StopAll();

        window = LaunchWithDisplays(2);
        Assert.Equal(3, HotkeyBoxes(window).Count);
        StopAll();

        window = LaunchWithDisplays(3);
        Assert.Equal("Ctrl+Alt+7", HotkeyBoxes(window)[3].Value());
    }

    [Fact]
    public void ReconnectedDisplay_WhoseHotkeyWasTaken_GetsDefaultHotkey()
    {
        var window = LaunchWithDisplays(3);
        PressCtrlAlt(window, row: 3, '7');
        WaitUntil(() => HotkeyBoxes(window)[3].Value() == "Ctrl+Alt+7", "The hotkey was not assigned.");
        StopAll();

        // While display 3 is disconnected, its hotkey is free to be taken by display 2.
        window = LaunchWithDisplays(2);
        PressCtrlAlt(window, row: 2, '7');
        WaitUntil(() => HotkeyBoxes(window)[2].Value() == "Ctrl+Alt+7", "The hotkey was not assigned.");
        StopAll();

        window = LaunchWithDisplays(3);
        Assert.Equal("Ctrl+Alt+7", HotkeyBoxes(window)[2].Value());
        Assert.Equal("Ctrl+Alt+3", HotkeyBoxes(window)[3].Value());
    }

    // Starting with fewer dummy displays disconnects the last ones; starting with more reconnects them.
    private AutomationElement LaunchWithDisplays(int displays)
    {
        var window = AutomationElement.FromHandle(WaitForWindow(Launch(displays), MainWindowClass));
        WaitForText(window, $"Dummy Display {displays}");
        return window;
    }

    private void StopAll()
    {
        foreach (var process in _started.Where(process => !process.HasExited))
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    [Fact]
    public void ThemeToggle_SwitchesThemeAndKeepsItAfterRestart()
    {
        var window = LaunchWithDisplays(1);
        InvokeButton(window, "Settings");
        InvokeButton(window, "Switch to dark mode");
        InvokeButton(window, "Switch to light mode");
        InvokeButton(window, "Switch to dark mode");
        // Invoke returns before the click runs; wait for its result so the choice is saved before the stop.
        WaitForButton(window, "Switch to light mode");
        StopAll();

        window = LaunchWithDisplays(1);
        InvokeButton(window, "Settings");
        WaitForButton(window, "Switch to light mode");
    }

    [Fact]
    public void Crash_IsWrittenToExceptionLog()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"HDRControl.Tests.{Guid.NewGuid():N}");
        var message = $"Test crash {Guid.NewGuid():N}";
        try
        {
            var app = Launch(dummyDisplays: 1, new Dictionary<string, string>
            {
                ["HDRCONTROL_LOG_DIRECTORY"] = logDirectory,
                ["HDRCONTROL_TEST_CRASH"] = message
            });

            Assert.True(app.WaitForExit(Timeout), "The app did not crash.");
            Assert.NotEqual(0, app.ExitCode);
            var log = File.ReadAllText(Path.Combine(logDirectory, "ExceptionLog.txt"));
            Assert.Contains("] Unhandled exception", log);
            // The crash must be logged exactly once.
            Assert.Single(log.Split($"System.InvalidOperationException: {message}").Skip(1));
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Crash_RotatesLargeExceptionLog()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"HDRControl.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "ExceptionLog.txt");
        var oldLog = new string('x', 1100 * 1024);
        File.WriteAllText(logPath, oldLog);
        var message = $"Test crash {Guid.NewGuid():N}";
        try
        {
            var app = Launch(dummyDisplays: 1, new Dictionary<string, string>
            {
                ["HDRCONTROL_LOG_DIRECTORY"] = logDirectory,
                ["HDRCONTROL_TEST_CRASH"] = message
            });

            Assert.True(app.WaitForExit(Timeout), "The app did not crash.");
            Assert.Equal(oldLog, File.ReadAllText(Path.Combine(logDirectory, "ExceptionLog.old.txt")));
            var log = File.ReadAllText(logPath);
            Assert.Contains(message, log);
            Assert.DoesNotContain("xxx", log);
        }
        finally
        {
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    [Fact]
    public void LegacyDisplayKeys_AreMovedToDevicePath()
    {
        Directory.CreateDirectory(_settingsDirectory);
        var settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        // One display saved under two adapter LUIDs, as older versions did after a restart.
        File.WriteAllText(settingsPath, """
            { "MonitorHotkeys": { "00000000:0000ABCD:D0000002": "Ctrl+F3", "00000000:0000BEEF:D0000002": "Ctrl+F3" } }
            """);

        var window = LaunchWithDisplays(2);

        Assert.Equal("Ctrl+F3", HotkeyBoxes(window)[2].Value());
        WaitUntil(
            () => File.ReadAllText(settingsPath) is var json && json.Contains("DUMMY#2") && !json.Contains("D0000002"),
            "The old display entries were not replaced.");
    }

    [Fact]
    public void BrokenSettingsFile_IsBackedUpAndAppStartsWithDefaults()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"HDRControl.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_settingsDirectory);
        var settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        File.WriteAllText(settingsPath, "{ broken");
        try
        {
            var window = AutomationElement.FromHandle(WaitForWindow(
                Launch(dummyDisplays: 1, new Dictionary<string, string> { ["HDRCONTROL_LOG_DIRECTORY"] = logDirectory }),
                MainWindowClass));

            WaitForText(window, "Settings were reset");
            Assert.Equal("{ broken", File.ReadAllText($"{settingsPath}.bak"));
            Assert.Equal("Ctrl+Alt+1", HotkeyBoxes(window)[1].Value());
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    private Process Launch(int? dummyDisplays = null, Dictionary<string, string>? environment = null)
    {
        var path = typeof(AppWindowTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "AppExecutable")
            .Value!;
        var startInfo = new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = false };
        startInfo.Environment["HDRCONTROL_SETTINGS_DIRECTORY"] = _settingsDirectory;
        if (dummyDisplays is not null)
        {
            startInfo.Environment["HDRCONTROL_DUMMY_DISPLAYS"] = dummyDisplays.ToString();
        }

        foreach (var (name, value) in environment ?? [])
        {
            startInfo.Environment[name] = value;
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
                if (GetClassNameW(window, name, name.Capacity) > 0 && name.ToString().StartsWith(className, StringComparison.Ordinal))
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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachFrom, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}

internal static class AutomationElementExtensions
{
    public static string Value(this AutomationElement element) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
}
