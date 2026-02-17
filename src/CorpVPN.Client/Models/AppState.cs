namespace CorpVPN.Client.Models;

public sealed record AppState(bool TunEnabled, bool AutoStartConfigured)
{
    public static AppState Default => new(false, false);
}
