using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CorpVPN.Client.Models;

namespace CorpVPN.Client.Services;

internal sealed class XrayService
{
    private readonly string _baseDir;
    private Process? _xrayProcess;

    public XrayService(string baseDir)
    {
        _baseDir = baseDir;
    }

    public string XrayPath => Path.Combine(_baseDir, "core", "xray.exe");
    public string RuntimeDir => Path.Combine(_baseDir, "runtime");
    public string ActiveConfigPath => Path.Combine(RuntimeDir, "active-config.json");

    public bool IsRunning => _xrayProcess is { HasExited: false };

    public async Task<(bool Ok, string Message)> StartAsync(VlessNode node, bool tunEnabled, CancellationToken cancellationToken)
    {
        if (!File.Exists(XrayPath))
        {
            return (false, $"xray.exe not found at {XrayPath}");
        }

        Directory.CreateDirectory(RuntimeDir);
        var config = BuildConfig(node, tunEnabled);
        await File.WriteAllTextAsync(ActiveConfigPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = XrayPath,
            Arguments = $"run -c \"{ActiveConfigPath}\"",
            WorkingDirectory = Path.GetDirectoryName(XrayPath)!,
            UseShellExecute = false,
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            CreateNoWindow = true
        };

        _xrayProcess = Process.Start(psi);
        await Task.Delay(800, cancellationToken).ConfigureAwait(false);

        if (_xrayProcess is null || _xrayProcess.HasExited)
        {
            return (false, "xray exited right after start");
        }

        return (true, $"Connected: {node.Name} ({node.Host}:{node.Port})");
    }

    public void Stop()
    {
        if (_xrayProcess is null)
        {
            return;
        }

        try
        {
            if (!_xrayProcess.HasExited)
            {
                _xrayProcess.Kill(entireProcessTree: true);
                _xrayProcess.WaitForExit(2000);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _xrayProcess.Dispose();
            _xrayProcess = null;
        }
    }

    public async Task<double> QueryKbpsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(XrayPath) || !IsRunning)
        {
            return 0d;
        }

        var psi = new ProcessStartInfo
        {
            FileName = XrayPath,
            Arguments = "api statsquery --server 127.0.0.1:10085",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(XrayPath)!
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return 0d;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        long up = 0;
        long down = 0;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains("outbound>>>proxy>>>traffic>>>uplink", StringComparison.OrdinalIgnoreCase))
            {
                up = ParseTrailingInt(line);
            }
            else if (line.Contains("outbound>>>proxy>>>traffic>>>downlink", StringComparison.OrdinalIgnoreCase))
            {
                down = ParseTrailingInt(line);
            }
        }

        return (up + down) * 8d / 1000d;
    }

    private static long ParseTrailingInt(string line)
    {
        var parts = line.Split([':', '='], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return 0;
        }

        return long.TryParse(parts[^1], out var value) ? value : 0;
    }

    private static JsonObject BuildConfig(VlessNode node, bool tunEnabled)
    {
        var inboundArray = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "socks-in",
                ["port"] = 10808,
                ["listen"] = "127.0.0.1",
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["udp"] = true }
            },
            new JsonObject
            {
                ["tag"] = "http-in",
                ["port"] = 10809,
                ["listen"] = "127.0.0.1",
                ["protocol"] = "http",
                ["settings"] = new JsonObject()
            },
            new JsonObject
            {
                ["tag"] = "api",
                ["listen"] = "127.0.0.1",
                ["port"] = 10085,
                ["protocol"] = "dokodemo-door",
                ["settings"] = new JsonObject { ["address"] = "127.0.0.1" }
            }
        };

        if (tunEnabled)
        {
            inboundArray.Add(new JsonObject
            {
                ["tag"] = "tun-in",
                ["protocol"] = "tun",
                ["settings"] = new JsonObject
                {
                    ["name"] = "xray-tun",
                    ["mtu"] = 1500,
                    ["stack"] = "system",
                    ["autoRoute"] = true,
                    ["strictRoute"] = true
                }
            });
        }

        var streamSettings = new JsonObject
        {
            ["network"] = "tcp",
            ["security"] = node.Security,
            ["serverName"] = node.Sni,
            ["fingerprint"] = node.Fingerprint
        };

        if (string.Equals(node.Security, "tls", StringComparison.OrdinalIgnoreCase))
        {
            streamSettings["tlsSettings"] = new JsonObject
            {
                ["serverName"] = node.Sni,
                ["fingerprint"] = node.Fingerprint
            };
        }

        return new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["loglevel"] = "warning",
                ["access"] = Path.Combine(AppContext.BaseDirectory, "runtime", "access.log"),
                ["error"] = Path.Combine(AppContext.BaseDirectory, "runtime", "error.log")
            },
            ["api"] = new JsonObject
            {
                ["tag"] = "api",
                ["services"] = new JsonArray("StatsService")
            },
            ["stats"] = new JsonObject(),
            ["policy"] = new JsonObject
            {
                ["system"] = new JsonObject
                {
                    ["statsOutboundUplink"] = true,
                    ["statsOutboundDownlink"] = true
                }
            },
            ["inbounds"] = inboundArray,
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "proxy",
                    ["protocol"] = "vless",
                    ["settings"] = new JsonObject
                    {
                        ["vnext"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["address"] = node.Host,
                                ["port"] = node.Port,
                                ["users"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["id"] = node.Uuid,
                                        ["encryption"] = "none",
                                        ["flow"] = node.Flow
                                    }
                                }
                            }
                        }
                    },
                    ["streamSettings"] = streamSettings
                },
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }
            },
            ["routing"] = new JsonObject
            {
                ["rules"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "field",
                        ["inboundTag"] = new JsonArray("api"),
                        ["outboundTag"] = "direct"
                    }
                }
            }
        };
    }
}
