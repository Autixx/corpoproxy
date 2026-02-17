using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace CorpVPN.Client.Services;

internal static class SystemProxyService
{
    private const string RegistryPath = @"Software\\Microsoft\\Windows\\CurrentVersion\\Internet Settings";

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(nint hInternet, int dwOption, nint lpBuffer, int dwBufferLength);

    public static void SetEnabled(bool enabled, int localPort = 10809)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open Internet Settings registry key.");

        if (enabled)
        {
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"127.0.0.1:{localPort}", RegistryValueKind.String);
            key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        }
        else
        {
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        }

        InternetSetOption(0, 39, 0, 0);
        InternetSetOption(0, 37, 0, 0);
    }
}
