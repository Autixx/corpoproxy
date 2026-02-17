using System.Diagnostics;

namespace CorpVPN.Client.Services;

internal static class AutoStartService
{
    public static bool EnsureTask()
    {
        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = $"/Create /TN CorpVPN-Autostart /SC ONLOGON /TR \"\"{exePath}\"\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
            return process is not null && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
