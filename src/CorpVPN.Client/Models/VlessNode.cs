namespace CorpVPN.Client.Models;

public sealed record VlessNode(
    string Name,
    string Host,
    int Port,
    string Uuid,
    string Security,
    string Sni,
    string Fingerprint,
    string Flow,
    string Network,
    string OriginalUri
);
