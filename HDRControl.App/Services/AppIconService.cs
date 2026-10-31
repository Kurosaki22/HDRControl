using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HDRControl.App.Services;

public sealed class AppIconService : IDisposable
{
    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const uint ImageIcon = 1;
    private const int SmCxIcon = 11;
    private const int SmCyIcon = 12;
    private const int SmCxSmallIcon = 49;
    private const int SmCySmallIcon = 50;
    private readonly IntPtr _windowHandle;
    private readonly IntPtr _largeIcon;
    private bool _disposed;

    public AppIconService(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        var executableModule = GetModuleHandleW(null);
        _largeIcon = LoadImageW(
            executableModule,
            new IntPtr(32512),
            ImageIcon,
            GetSystemMetrics(SmCxIcon),
            GetSystemMetrics(SmCyIcon),
            0);
        SmallIcon = LoadImageW(
            executableModule,
            new IntPtr(32512),
            ImageIcon,
            GetSystemMetrics(SmCxSmallIcon),
            GetSystemMetrics(SmCySmallIcon),
            0);

        if (_largeIcon == IntPtr.Zero || SmallIcon == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (_largeIcon != IntPtr.Zero)
            {
                DestroyIcon(_largeIcon);
            }

            if (SmallIcon != IntPtr.Zero)
            {
                DestroyIcon(SmallIcon);
            }

            throw new Win32Exception(error, "The HDR Control application icon could not be loaded.");
        }

        SendMessageW(_windowHandle, WmSetIcon, new IntPtr(IconBig), _largeIcon);
        SendMessageW(_windowHandle, WmSetIcon, new IntPtr(IconSmall), SmallIcon);
    }

    public IntPtr SmallIcon { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SendMessageW(_windowHandle, WmSetIcon, new IntPtr(IconBig), IntPtr.Zero);
        SendMessageW(_windowHandle, WmSetIcon, new IntPtr(IconSmall), IntPtr.Zero);
        DestroyIcon(_largeIcon);
        DestroyIcon(SmallIcon);
        _disposed = true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadImageW(
        IntPtr instance,
        IntPtr name,
        uint type,
        int width,
        int height,
        uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
