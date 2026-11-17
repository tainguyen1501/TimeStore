using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using TimeStore.Contracts.Lifecycle;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.S3;

/// <summary>Step 6c: lifecycle configuration over S3 and the admin API, and a run applied at once.</summary>
public sealed class LifecycleTests(TimeStoreServer server) : IClassFixture<TimeStoreServer>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly AmazonS3Client _s3 = server.Client();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _bucket = $"lc-{Guid.NewGuid():N}";

    public async ValueTask InitializeAsync() => await _s3.PutBucketAsync(_bucket, _ct);

    public ValueTask DisposeAsync()
    {
        _s3.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task PutRulesAsync(params LifecycleRule[] rules) =>
        _s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = _bucket, Configuration = new LifecycleConfiguration { Rules = [.. rules] },
        }, _ct);

    private static LifecycleRule Expire(string id, string prefix, int days) => new()
    {
        Id = id, Status = LifecycleRuleStatus.Enabled, Expiration = new LifecycleRuleExpiration { Days = days },
        Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = prefix } },
    };

    [Fact]
    public async Task Configuration_RoundTrips_AndDeleteRemovesIt()
    {
        // The server answers 404 NoSuchLifecycleConfiguration; SDK v4 reports that as an empty configuration.
        Assert.Empty((await _s3.GetLifecycleConfigurationAsync(_bucket, _ct)).Configuration?.Rules ?? []);

        await PutRulesAsync(Expire("logs", "logs/", 30), new LifecycleRule
        {
            Id = "versions", Status = LifecycleRuleStatus.Disabled,
            NoncurrentVersionExpiration = new LifecycleRuleNoncurrentVersionExpiration { NoncurrentDays = 7, NewerNoncurrentVersions = 2 },
            AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = 3 },
            Filter = new LifecycleFilter(),
        });

        var rules = (await _s3.GetLifecycleConfigurationAsync(_bucket, _ct)).Configuration.Rules;
        Assert.Equal(["logs", "versions"], rules.Select(r => r.Id));
        Assert.Equal(30, rules[0].Expiration.Days);
        Assert.Equal(LifecycleRuleStatus.Disabled, rules[1].Status);
        Assert.Equal((7, 2, 3), (rules[1].NoncurrentVersionExpiration.NoncurrentDays, rules[1].NoncurrentVersionExpiration.NewerNoncurrentVersions,
            rules[1].AbortIncompleteMultipartUpload.DaysAfterInitiation));

        await _s3.DeleteLifecycleConfigurationAsync(_bucket, _ct);
        Assert.Empty((await _s3.GetLifecycleConfigurationAsync(_bucket, _ct)).Configuration?.Rules ?? []);
        await _s3.DeleteLifecycleConfigurationAsync(_bucket, _ct); // nothing to delete: still 204
    }

    [Fact]
    public async Task RuleIds_AreGenerated_LimitedAndUnique()
    {
        await PutRulesAsync(Expire(null!, "", 1));
        Assert.False(string.IsNullOrEmpty((await _s3.GetLifecycleConfigurationAsync(_bucket, _ct)).Configuration.Rules[0].Id));

        var tooLong = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutRulesAsync(Expire(new string('a', 256), "", 1)));
        Assert.Equal("InvalidArgument", tooLong.ErrorCode);
        var twice = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutRulesAsync(Expire("same", "a/", 1), Expire("same", "b/", 2)));
        Assert.Equal("InvalidArgument", twice.ErrorCode);
    }

    [Fact]
    public async Task TagFiltersAndTransitions_AreRefused()
    {
        var tags = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutRulesAsync(new LifecycleRule
        {
            Id = "tags", Status = LifecycleRuleStatus.Enabled, Expiration = new LifecycleRuleExpiration { Days = 1 },
            Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecycleTagPredicate { Tag = new Tag { Key = "k", Value = "v" } } },
        }));
        Assert.Equal("NotImplemented", tags.ErrorCode);

        var transition = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => PutRulesAsync(new LifecycleRule
        {
            Id = "cold", Status = LifecycleRuleStatus.Enabled, Filter = new LifecycleFilter(),
            Transitions = [new LifecycleTransition { Days = 30, StorageClass = S3StorageClass.Glacier }],
        }));
        Assert.Equal("InvalidStorageClass", transition.ErrorCode);
    }

    [Fact]
    public async Task AdminRunNow_AppliesADueDateRule_AndRemovesALoneDeleteMarker()
    {
        using var admin = await server.AdminClientAsync();
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "tmp/old.txt", ContentBody = "x" }, _ct);
        await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "keep.txt", ContentBody = "y" }, _ct);

        var today = DateTime.UtcNow.Date;
        var put = await admin.PutAsJsonAsync($"/api/v1/buckets/{_bucket}/lifecycle", new LifecycleConfigurationDto(
        [
            new LifecycleRuleDto("tmp", true, "tmp/", null, null, null, today, false, null, null, null),
            new LifecycleRuleDto("markers", true, null, null, null, null, null, true, null, null, null),
        ]), Json, _ct);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var run = await (await admin.PostAsync("/api/v1/maintenance/lifecycle", null, _ct)).Content.ReadFromJsonAsync<LifecycleRunResponse>(Json, _ct);
        Assert.True(run!.Expired >= 1);
        var keys = (await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket }, _ct)).S3Objects?.Select(o => o.Key) ?? [];
        Assert.Equal(["keep.txt"], keys);

        // A lone delete marker (its object version removed) goes on the next run.
        await _s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = _bucket, VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        }, _ct);
        var version = (await _s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = "gone.txt", ContentBody = "z" }, _ct)).VersionId;
        await _s3.DeleteObjectAsync(_bucket, "gone.txt", _ct);
        await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = "gone.txt", VersionId = version }, _ct);

        var second = await (await admin.PostAsync("/api/v1/maintenance/lifecycle", null, _ct)).Content.ReadFromJsonAsync<LifecycleRunResponse>(Json, _ct);
        Assert.True(second!.DeleteMarkersRemoved >= 1);
        Assert.Empty((await _s3.ListVersionsAsync(new ListVersionsRequest { BucketName = _bucket, Prefix = "gone.txt" }, _ct)).Versions ?? []);
    }

    [Fact]
    public async Task AdminApi_ValidatesLikeS3()
    {
        using var admin = await server.AdminClientAsync();
        var response = await admin.PutAsJsonAsync($"/api/v1/buckets/{_bucket}/lifecycle", new LifecycleConfigurationDto(
            [new LifecycleRuleDto("bad", true, null, null, null, 0, null, false, null, null, null)]), Json, _ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/buckets/{_bucket}/lifecycle", _ct)).StatusCode);
    }
}
