using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Serilog;
using Microsoft.Win32;

namespace Jammer_desktop;

// Changes LAN proxy flags through WinINet, retaining the server and PAC addresses.
internal sealed class WindowsProxyScope : IDisposable
{
    [StructLayout(LayoutKind.Explicit)]
    private struct Value { [FieldOffset(0)] public int Flags; [FieldOffset(0)] public IntPtr Pointer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Option { public int Kind; public Value Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Options { public int Size; public IntPtr Connection; public int Count, Error; public IntPtr Items; }
    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InternetQueryOptionW(IntPtr handle, int option, ref Options options, ref int length);
    [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InternetSetOptionW(IntPtr handle, int option, IntPtr buffer, int length);

    private readonly int _previous;
    private readonly object? _previousEnabled;
    private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private bool _disposed;

    public WindowsProxyScope()
    {
        _previous = Flags(null);
        using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey)) _previousEnabled = key?.GetValue("ProxyEnable");
        try { Flags(1); Enabled(0); Notify(); } // DIRECT plus compatibility flag read by other proxy clients.
        catch { Flags(_previous); Enabled(_previousEnabled); Notify(); throw; }
        Log.Information("Windows system proxy disabled for VPN session; previous flags={Flags}. Restart apps that retain existing proxy connections if necessary.", _previous);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        if (Flags(null) != 1 || Convert.ToInt32(key?.GetValue("ProxyEnable", 0)) != 0)
        {
            Log.Information("Windows proxy changed externally; preserving the newer settings");
            return;
        }
        Flags(_previous);
        Enabled(_previousEnabled);
        Notify();
        Log.Information("Windows system proxy settings restored after VPN session");
    }

    private static void Enabled(object? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        if (value is null) key.DeleteValue("ProxyEnable", false);
        else key.SetValue("ProxyEnable", value, RegistryValueKind.DWord);
    }

    private static int Flags(int? value)
    {
        var item = new Option { Kind = 1, Value = new Value { Flags = value ?? 0 } };
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<Option>());
        var listBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<Options>());
        try
        {
            Marshal.StructureToPtr(item, buffer, false);
            var list = new Options { Size = Marshal.SizeOf<Options>(), Count = 1, Items = buffer };
            int length = list.Size;
            if (value.HasValue)
            {
                Marshal.StructureToPtr(list, listBuffer, false);
                if (!InternetSetOptionW(IntPtr.Zero, 75, listBuffer, length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            else if (!InternetQueryOptionW(IntPtr.Zero, 75, ref list, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<Option>(buffer).Value.Flags;
        }
        finally { Marshal.FreeHGlobal(listBuffer); Marshal.FreeHGlobal(buffer); }
    }

    private static void Notify()
    {
        foreach (int option in new[] { 39, 37, 95 })
            if (!InternetSetOptionW(IntPtr.Zero, option, IntPtr.Zero, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
