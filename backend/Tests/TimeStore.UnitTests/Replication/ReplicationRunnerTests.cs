using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TimeStore.Application.Abstractions;
using TimeStore.Application.Features.Replication;
using TimeStore.Cryptography.Secrets;
using TimeStore.Domain.Blobs;
using TimeStore.Domain.Buckets;
using TimeStore.Domain.Objects;
using TimeStore.Domain.Replication;
using TimeStore.Infra.Services.Replication;
using TimeStore.Metadata.Sqlite;
using TimeStore.Metadata.Sqlite.Schema;
using TimeStore.UnitTests.Fakes;

namespace TimeStore.UnitTests.Replication;

/// <summary>The replication worker's loop over a real SQLite outbox and a fake destination.</summary>
public sealed class ReplicationRunnerTests : IAsyncLifetime
{
    private const string Md5 = "d41d8cd98f00b204e9800998ecf8427e";
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "timestore-tests", Guid.NewGuid().ToString("N"));
    private readonly ISecretProtector _protector =
        new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(AesGcmSecretProtector.MasterKeyLength));
    private readonly FakeDestination _destination = new();
    private SqliteMetadataStore _store = null!;
    private Bucket _bucket = null!;

    public async ValueTask InitializeAsync()
    {
        var connections = new SqliteConnectionFactory(new SqliteMetadataOptions(Path.Combine(_directory, "metadata.db")));
        await new SqliteMetadataInitializer(connections, TimeProvider.System).InitializeAsync(_ct);
        _store = new SqliteMetadataStore(connections);
        _bucket = new Bucket(Guid.CreateVersion7(), "photos", DateTime.UtcNow);
        Assert.True(await _store.TryCreateBucketAsync(_bucket, _ct));
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task AKeyOverwrittenManyTimes_IsSentOnce_WithItsNewestState()
    {
        await SaveRuleAsync();
        for (var i = 0; i < 100; i++)
            await PutAsync("hot", contentType: $"text/v{i}");

        await RunUntilAsync(() => _destination.Puts.Count > 0 && Backlog().Pending == 0);

        var put = Assert.Single(_destination.Puts);
        Assert.Equal(("hot", "text/v99"), (put.Key, put.ContentType));
    }

    [Fact]
    public async Task ChangesMadeWhileAKeyIsSent_AreSentAfterwards()
    {
        await SaveRuleAsync();
        _destination.Gate = new TaskCompletionSource();
        await PutAsync("a", contentType: "text/first");

        await RunUntilAsync(async () =>
        {
            if (_destination.Started == 1 && _destination.Gate is { Task.IsCompleted: false } gate)
            {
                await PutAsync("a", contentType: "text/second"); // committed during the send
                gate.SetResult();
            }

            return _destination.Puts.Count == 2 && Backlog().Pending == 0;
        });

        Assert.Equal(["text/first", "text/second"], _destination.Puts.Select(static p => p.ContentType));
    }

    [Fact]
    public async Task ASlowObject_DoesNotHoldBackTheOthers()
    {
        await SaveRuleAsync();
        _destination.SlowKey = "big";
        await PutAsync("big");
        for (var i = 0; i < 10; i++)
            await PutAsync($"small-{i}");

        await RunUntilAsync(
            () => Task.FromResult(_destination.Puts.Count(static p => p.Key.StartsWith("small-", StringComparison.Ordinal)) == 10),
            then: async () =>
            {
                Assert.DoesNotContain(_destination.Puts, static p => p.Key == "big"); // still sending
                _destination.ReleaseSlow();
                await WaitUntilAsync(() => Backlog().Pending == 0);
            });
    }

    [Fact]
    public async Task Deletes_AreSentOnlyWhenTheRuleSaysSo()
    {
        await SaveRuleAsync(replicateDeletes: false);
        await PutAsync("kept");
        await _store.DeleteObjectAsync(_bucket.Id, "kept", _ct);
        await RunUntilAsync(() => Backlog().Pending == 0);
        Assert.Empty(_destination.Deletes);
        Assert.Empty(_destination.Puts); // the key was gone when it was sent: nothing to put, no delete

        await SaveRuleAsync(replicateDeletes: true);
        await PutAsync("gone");
        await _store.DeleteObjectAsync(_bucket.Id, "gone", _ct);
        await RunUntilAsync(() => _destination.Deletes.Contains("gone") && Backlog().Pending == 0);
    }

    [Fact]
    public async Task TemporaryFailures_PauseTheRule_NeverFail_AndCatchUpLater()
    {
        await SaveRuleAsync();
        _destination.Next = _ => new ReplicationOutcome(ReplicationFailure.Temporary, "ServiceUnavailable (503)");
        await PutAsync("a");

        await RunUntilAsync(async () => (await _store.FindRuleAsync(_bucket.Id, _ct))!.Failures >= 2, then: async () =>
        {
            Assert.Equal((1L, 0L), (Backlog().Pending, Backlog().Failed));
            _destination.Next = null; // the destination is back
            await WaitUntilAsync(() => Backlog().Pending == 0);
        });

        var rule = (await _store.FindRuleAsync(_bucket.Id, _ct))!;
        Assert.Equal((0, (string?)null), (rule.Failures, rule.LastError));
        Assert.Single(_destination.Puts);
    }

    [Fact]
    public async Task ARejectedDestination_FailsEveryPendingChange_UntilRetried()
    {
        await SaveRuleAsync();
        _destination.Next = _ => new ReplicationOutcome(ReplicationFailure.DestinationRejected, "InvalidAccessKeyId (403)");
        await PutAsync("a");
        await PutAsync("b");

        await RunUntilAsync(() => Backlog().Failed == 2);
        Assert.Equal("InvalidAccessKeyId (403)", Backlog().Failures[0].LastError);

        _destination.Next = null;
        var rule = (await _store.FindRuleAsync(_bucket.Id, _ct))!;
        await _store.RetryAsync(rule.Id, DateTime.UtcNow, _ct);
        await RunUntilAsync(() => Backlog() is { Pending: 0, Failed: 0 } && _destination.Puts.Count == 2);
    }

    [Fact]
    public async Task ARejectedObject_FailsThatKeyOnly_AndAnUnreadableBlob_IsTriedAgain()
    {
        await SaveRuleAsync();
        _destination.Next = key => key switch
        {
            "too-big" => new ReplicationOutcome(ReplicationFailure.ObjectRejected, "EntityTooLarge (400)"),
            "moved" => new ReplicationOutcome(ReplicationFailure.SourceUnavailable, "blob gone"),
            _ => ReplicationOutcome.Success,
        };
        await PutAsync("too-big");
        await PutAsync("moved");
        await PutAsync("fine");

        await RunUntilAsync(() => Backlog() is { Failed: 1, Pending: 1 } && _destination.Puts.Any(static p => p.Key == "fine"));

        Assert.Equal("too-big", Assert.Single(Backlog().Failures).Key);
    }

    [Fact]
    public void Backoff_Doubles_UpToTheCap()
    {
        var options = ReplicationOptions.Default with { MaxBackoff = TimeSpan.FromSeconds(10) };
        Assert.Equal([1, 2, 4, 8, 10, 10], new[] { 1, 2, 3, 4, 5, 40 }.Select(n => options.Backoff(n).TotalSeconds));
    }

    private ReplicationBacklog Backlog() =>
        _store.GetBacklogAsync(_store.FindRuleAsync(_bucket.Id, _ct).AsTask().GetAwaiter().GetResult()!.Id, 10, _ct)
            .AsTask().GetAwaiter().GetResult();

    /// <summary>Waits (the runner started by <see cref="RunUntilAsync(Func{Task{bool}}, Func{Task})"/> keeps running).</summary>
    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10, _ct);
        }
    }

    private async Task RunUntilAsync(Func<bool> condition) => await RunUntilAsync(() => Task.FromResult(condition()));

    /// <summary>Runs the worker until <paramref name="condition"/> holds (then <paramref name="then"/> while it still runs).</summary>
    private async Task RunUntilAsync(Func<Task<bool>> condition, Func<Task>? then = null)
    {
        var options = new ReplicationOptions(
            true, Concurrency: 4, PollInterval: TimeSpan.FromMilliseconds(50), BatchDelay: TimeSpan.Zero,
            MaxBackoff: TimeSpan.FromMilliseconds(200), RequestTimeout: TimeSpan.FromSeconds(10));
        var runner = new ReplicationRunner(_store, _store, new RecordingBlobStore(), _destination, _protector, options, TimeProvider.System);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var running = runner.RunAsync(stop.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!await condition())
            {
                Assert.False(running.IsCompleted, "the runner stopped");
                Assert.True(DateTime.UtcNow < deadline, "timed out");
                await Task.Delay(10, _ct);
            }

            if (then is not null)
                await then();
        }
        finally
        {
            _destination.ReleaseSlow();
            _destination.Gate?.TrySetResult();
            await stop.CancelAsync();
            await running;
        }
    }

    private async Task SaveRuleAsync(bool replicateDeletes = true)
    {
        var existing = await _store.FindRuleAsync(_bucket.Id, _ct);
        var rule = new ReplicationRule(
            existing?.Id ?? Guid.CreateVersion7(), _bucket.Id, "https://replica.example.org", "replica", "us-east-1", "AKID",
            ReplicationSecrets.Protect(_protector, _bucket.Id, "secret"), "", replicateDeletes, DateTime.UtcNow, DateTime.UtcNow);
        Assert.True(await _store.SaveRuleAsync(rule, queueExisting: false, _ct));
    }

    private async Task PutAsync(string key, string contentType = "text/plain") =>
        await _store.CommitObjectAsync(new StoredObject(
            _bucket.Id, key, Guid.CreateVersion7(), new SingleBlob(new BlobLocator(Guid.CreateVersion7(), "node-1", 0, Md5)), Md5,
            contentType, null, null, null, [], [], DateTime.UtcNow), CommitCondition.Always, _ct);

    private sealed class FakeDestination : IReplicationClient
    {
        private readonly TaskCompletionSource _slow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public ConcurrentQueue<(string Key, string ContentType)> Puts { get; } = new();

        public ConcurrentQueue<string> Deletes { get; } = new();

        public Func<string, ReplicationOutcome>? Next { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public string? SlowKey { get; set; }

        public int Started => Volatile.Read(ref _started);

        public void ReleaseSlow() => _slow.TrySetResult();

        public async ValueTask<ReplicationOutcome> PutAsync(
            ReplicationDestination destination, StoredObject source, Func<BlobLocator, CancellationToken, ValueTask<Stream>> openBlob,
            CancellationToken ct)
        {
            Assert.Equal("secret", destination.SecretKey);
            Interlocked.Increment(ref _started);
            if (Gate is { } gate)
                await gate.Task.WaitAsync(ct);
            if (source.Key == SlowKey)
                await _slow.Task.WaitAsync(ct);
            if (Next?.Invoke(source.Key) is { IsSuccess: false } failure)
                return failure;
            Puts.Enqueue((source.Key, source.ContentType));
            return ReplicationOutcome.Success;
        }

        public ValueTask<ReplicationOutcome> DeleteAsync(ReplicationDestination destination, string key, CancellationToken ct)
        {
            if (Next?.Invoke(key) is { IsSuccess: false } failure)
                return ValueTask.FromResult(failure);
            Deletes.Enqueue(key);
            return ValueTask.FromResult(ReplicationOutcome.Success);
        }

        public ValueTask<ReplicationOutcome> TestAsync(ReplicationDestination destination, CancellationToken ct) =>
            ValueTask.FromResult(ReplicationOutcome.Success);
    }
}
