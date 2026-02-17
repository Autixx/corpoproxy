using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace CorpVPN.Client.Services;

internal static class EmbeddedSubscriptionStore
{
    private const string ResourceName = "CorpVPN.Client.Assets.subscriptions.enc";

    public static IReadOnlyList<string> LoadSubscriptionUrls()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Encrypted subscriptions resource not found. Rebuild project.");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var all = ms.ToArray();
        if (all.Length <= 16)
        {
            return [];
        }

        var iv = all.AsSpan(0, 16).ToArray();
        var cipher = all.AsSpan(16).ToArray();

        var key = SHA256.HashData(Encoding.UTF8.GetBytes(BuildSecrets.SubscriptionsKey));
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);

        var text = Encoding.UTF8.GetString(plain);
        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("#", StringComparison.Ordinal) && Uri.IsWellFormedUriString(l, UriKind.Absolute))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
