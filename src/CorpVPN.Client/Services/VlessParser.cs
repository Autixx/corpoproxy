using System.Net;
using System.Text;
using CorpVPN.Client.Models;

namespace CorpVPN.Client.Services;

internal static class VlessParser
{
    public static VlessNode Parse(string uri)
    {
        var parsed = new Uri(uri);
        if (!string.Equals(parsed.Scheme, "vless", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Only vless:// is supported.");
        }

        var user = WebUtility.UrlDecode(parsed.UserInfo);
        var uuid = user.Split(':')[0];
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(parsed.Host))
        {
            throw new FormatException("VLESS uri missing uuid/host.");
        }

        var query = ParseQuery(parsed.Query);
        var name = WebUtility.UrlDecode(parsed.Fragment.TrimStart('#'));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = parsed.Host;
        }

        return new VlessNode(
            Name: name,
            Host: parsed.Host,
            Port: parsed.Port > 0 ? parsed.Port : 443,
            Uuid: uuid,
            Security: Get(query, "security", "tls"),
            Sni: Get(query, "sni", parsed.Host),
            Fingerprint: Get(query, "fp", "chrome"),
            Flow: Get(query, "flow", string.Empty),
            Network: Get(query, "type", "tcp"),
            OriginalUri: uri
        );
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return dict;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = WebUtility.UrlDecode(parts[0]);
            var value = parts.Length == 2 ? WebUtility.UrlDecode(parts[1]) : string.Empty;
            if (!string.IsNullOrWhiteSpace(key))
            {
                dict[key] = value;
            }
        }

        return dict;
    }

    private static string Get(IReadOnlyDictionary<string, string> map, string key, string fallback)
        => map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}
