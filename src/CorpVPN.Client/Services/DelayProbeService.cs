using System.Net.Sockets;
using CorpVPN.Client.Models;

namespace CorpVPN.Client.Services;

internal sealed class DelayProbeService
{
    public async Task<NodeDelay?> ProbeAsync(VlessNode node, int timeoutMs, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;

        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(node.Host, node.Port, cts.Token).ConfigureAwait(false);
            var elapsed = (int)Math.Max(1, Environment.TickCount64 - started);
            return new NodeDelay(node, elapsed);
        }
        catch
        {
            return null;
        }
    }

    public async Task<NodeDelay?> PickLeastDelayAsync(IEnumerable<VlessNode> nodes, int timeoutMs, CancellationToken cancellationToken)
    {
        var tasks = nodes.Select(node => ProbeAsync(node, timeoutMs, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).OrderBy(r => r.DelayMs).FirstOrDefault();
    }
}
