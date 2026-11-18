using System.Threading.Channels;
using TimeStore.Application.Features.Backups;

namespace TimeStore.Infra.Services.Backups;

/// <summary>
/// <see cref="IBackupQueue"/> in memory: an unbounded channel of run ids. It only wakes the worker; the
/// <c>backup_runs</c> row is the truth, so a lost wake-up after a crash is recovered from the table at startup.
/// </summary>
public sealed class BackupQueue : IBackupQueue
{
    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Enqueue(Guid runId) => _channel.Writer.TryWrite(runId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
