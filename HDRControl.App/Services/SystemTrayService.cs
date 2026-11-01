using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HDRControl.App.Services;

public sealed class SystemTrayService : IDisposable
{
    private const uint SubclassId = 0x48445232;
    private const uint TrayCallbackMessage = 0x8000 + 0x51;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmRightButtonUp = 0x0205;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint MenuString = 0;
    private const uint TrackPopupMenuReturnCommand = 0x0100;
    private const uint MenuOpen = 1;
    private const uint MenuQuit = 2;
    private const uint AllowAnyProcess = 0xFFFFFFFF;
    private static readonly IntPtr BroadcastWindow = new(0xFFFF);
    private static readonly uint ShowWindowMessage = RegisterWindowMessageW("HDRControl.ShowWindow");
    private readonly IntPtr _windowHandle;
    private readonly IntPtr _applicationIcon;
    private readonly SubclassProc _subclassProc;
    private bool _iconVisible;
    private bool _disposed;

    public SystemTrayService(IntPtr windowHandle, IntPtr applicationIcon)
    {
        _windowHandle = windowHandle;
        _applicationIcon = applicationIcon;
        _subclassProc = WindowProc;
        if (!SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The system tray integration could not be initialized.");
        }
    }

    public event Action? OpenRequested;
    public event Action? QuitRequested;

    public static void RequestShowRunningInstance()
    {
        // Only the process the user just launched may hand over the right to take the foreground.
        AllowSetForegroundWindow(AllowAnyProcess);
        if (!PostMessageW(BroadcastWindow, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The running HDR Control window could not be opened.");
        }
    }

    public void SetVisible(bool visible)
    {
        if (_disposed || _iconVisible == visible)
        {
            return;
        }

        var data = CreateIconData();
        var success = visible
            ? Shell_NotifyIconW(NimAdd, ref data)
            : Shell_NotifyIconW(NimDelete, ref data);
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The system tray icon could not be updated.");
        }

        _iconVisible = visible;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_iconVisible)
        {
            var data = CreateIconData();
            if (!Shell_NotifyIconW(NimDelete, ref data))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The system tray icon could not be removed.");
            }

            _iconVisible = false;
        }

        RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
        _disposed = true;
    }

    private IntPtr WindowProc(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        IntPtr referenceData)
    {
        if (message == ShowWindowMessage)
        {
            OpenRequested?.Invoke();
        }
        else if (message == TrayCallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64());
            if (notification == WmLeftButtonUp)
            {
                OpenRequested?.Invoke();
            }
            else if (notification == WmRightButtonUp)
            {
                ShowContextMenu();
            }
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The system tray menu could not be created.");
        }

        try
        {
            if (!AppendMenuW(menu, MenuString, MenuOpen, "Open HDR Control")
                || !AppendMenuW(menu, 0x0800, 0, null)
                || !AppendMenuW(menu, MenuString, MenuQuit, "Quit"))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The system tray menu could not be populated.");
            }

            if (!GetCursorPos(out var cursor))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The mouse position could not be read.");
            }

            SetForegroundWindow(_windowHandle);
            var command = TrackPopupMenu(
                menu,
                TrackPopupMenuReturnCommand,
                cursor.X,
                cursor.Y,
                0,
                _windowHandle,
                IntPtr.Zero);
            PostMessageW(_windowHandle, 0, IntPtr.Zero, IntPtr.Zero);

            if (command == MenuOpen)
            {
                OpenRequested?.Invoke();
            }
            else if (command == MenuQuit)
            {
                QuitRequested?.Invoke();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NOTIFYICONDATA CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        Window = _windowHandle,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = TrayCallbackMessage,
        Icon = _applicationIcon,
        Tip = "HDR Control",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr window,
        IntPtr rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
