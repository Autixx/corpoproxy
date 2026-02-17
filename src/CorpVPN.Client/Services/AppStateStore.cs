using System.Text.Json;
using CorpVPN.Client.Models;

namespace CorpVPN.Client.Services;

internal static class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string StatePath { get; } = Path.Combine(AppContext.BaseDirectory, "config", "state.json");

    public static AppState Load()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return AppState.Default;
            }

            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<AppState>(json) ?? AppState.Default;
        }
        catch
        {
            return AppState.Default;
        }
    }

    public static void Save(AppState state)
    {
        var dir = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(StatePath, json);
    }
}
