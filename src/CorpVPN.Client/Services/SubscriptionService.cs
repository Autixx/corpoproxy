using System.Text;
using CorpVPN.Client.Models;

namespace CorpVPN.Client.Services;

internal sealed class SubscriptionService
{
    private readonly HttpClient _httpClient;

    public SubscriptionService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<VlessNode>> LoadNodesAsync(CancellationToken cancellationToken)
    {
        var sources = EmbeddedSubscriptionStore.LoadSubscriptionUrls();
        var all = new List<VlessNode>();

        foreach (var source in sources)
        {
            try
            {
                using var response = await _httpClient.GetAsync(source, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                foreach (var line in DecodeSubscriptionPayload(content))
                {
                    if (!line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var node = VlessParser.Parse(line);
                        if (string.Equals(node.Network, "tcp", StringComparison.OrdinalIgnoreCase))
                        {
                            all.Add(node);
                        }
                    }
                    catch
                    {
                        // Ignore malformed entries.
                    }
                }
            }
            catch
            {
                // Ignore bad subscription endpoint and continue with others.
            }
        }

        return all
            .GroupBy(n => n.OriginalUri, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToArray();
    }

    private static IEnumerable<string> DecodeSubscriptionPayload(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        if (trimmed.Contains("vless://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        }

        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            var decoded = Encoding.UTF8.GetString(bytes);
            return decoded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return [];
        }
    }
}
