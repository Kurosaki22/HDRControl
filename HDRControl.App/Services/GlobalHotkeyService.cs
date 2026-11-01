using System.ComponentModel;
using System.Runtime.InteropServices;
using HDRControl.App.Models;

namespace HDRControl.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint SubclassId = 0x48445231;
    private const int FirstHotkeyId = 0x4100;
    private const int AllDisplaysHotkeyId = 0x40FF;
    private readonly IntPtr _windowHandle;
    private readonly SubclassProc _subclassProc;
    private readonly Dictionary<int, string> _registeredHotkeys = [];
    private readonly Dictionary<string, int> _monitorHotkeyIds = new(StringComparer.Ordinal);
    private int _nextHotkeyId = FirstHotkeyId;
    private bool _disposed;

    public GlobalHotkeyService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _subclassProc = WindowProc;
        if (!SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Global keyboard hotkeys could not be initialized.");
        }
    }

    public event Action<string>? HotkeyPressed;

    public IReadOnlyList<string> UpdateRegistrations(
        IReadOnlyList<MonitorViewModel> monitors,
        string? allDisplaysHotkey)
    {
        foreach (var id in _registeredHotkeys.Keys.ToArray())
        {
            UnregisterHotKey(_windowHandle, id);
        }

        _registeredHotkeys.Clear();
        var warnings = new List<string>();
        var hotkeysInUse = new HashSet<(uint Modifiers, uint VirtualKey)>();

        foreach (var monitor in monitors)
        {
            if (!monitor.CanSelect)
            {
                continue;
            }

            var hotkey = monitor.Hotkey;
            if (!TryRegister(
                    hotkey,
                    monitor.Identity,
                    GetMonitorHotkeyId(monitor.Identity),
                    hotkeysInUse,
                    warnings))
            {
                continue;
            }
        }

        TryRegister(
            allDisplaysHotkey,
            AllDisplaysHotkeyTarget,
            AllDisplaysHotkeyId,
            hotkeysInUse,
            warnings);
        return warnings;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var id in _registeredHotkeys.Keys)
        {
            UnregisterHotKey(_windowHandle, id);
        }

        RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
        _registeredHotkeys.Clear();
        _disposed = true;
    }

    public const string AllDisplaysHotkeyTarget = "\0all-displays";

    private bool TryRegister(
        string? hotkey,
        string target,
        int id,
        HashSet<(uint Modifiers, uint VirtualKey)> hotkeysInUse,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return true;
        }

        if (!HotkeyGesture.TryParse(hotkey, out var gesture) || gesture is null)
        {
            warnings.Add($"“{hotkey}” is not a valid global hotkey.");
            return false;
        }

        if (!hotkeysInUse.Add((gesture.Modifiers, gesture.VirtualKey)))
        {
            warnings.Add($"{gesture.DisplayText} is assigned more than once.");
            return false;
        }

        if (!RegisterHotKey(
                _windowHandle,
                id,
                gesture.Modifiers | ModNoRepeat,
                gesture.VirtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            warnings.Add($"{gesture.DisplayText} could not be registered (Windows error {error}).");
            return false;
        }

        _registeredHotkeys.Add(id, target);
        return true;
    }

    private int GetMonitorHotkeyId(string identity)
    {
        if (_monitorHotkeyIds.TryGetValue(identity, out var id))
        {
            return id;
        }

        id = _nextHotkeyId++;
        _monitorHotkeyIds.Add(identity, id);
        return id;
    }

    private IntPtr WindowProc(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        IntPtr referenceData)
    {
        if (message == WmHotkey
            && _registeredHotkeys.TryGetValue(unchecked((int)wParam.ToUInt64()), out var target))
        {
            HotkeyPressed?.Invoke(target);
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        IntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr window,
        SubclassProc callback,
        UIntPtr subclassId,
        IntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr window,
        SubclassProc callback,
        UIntPtr subclassId);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr window,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
