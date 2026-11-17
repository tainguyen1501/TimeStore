using TimeStore.Compat.Tests.Infrastructure;

namespace TimeStore.Compat.Tests.Ceph;

/// <summary>
/// The ceph s3-tests subset as a ratchet: every test in <c>expected-pass.txt</c> must pass. A listed test that fails
/// is a regression and fails this test; a test that newly passes is reported so it can be added to the list (the list
/// only grows). The list is ours; the suite itself is downloaded at test time.
/// </summary>
public sealed class CephS3Tests(CompatServer fixture, ITestOutputHelper output) : IClassFixture<CompatServer>
{
    private const string ExpectedFile = "Ceph/expected-pass.txt";
    private const string ReportDirectoryVariable = "TIMESTORE_COMPAT_REPORT_DIR";

    [Fact]
    public async Task EveryExpectedTest_StillPasses()
    {
        Assert.SkipUnless(CompatClients.Enabled, CompatClients.SkipReason);
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, ExpectedFile))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        var results = await CephS3TestsRunner.RunAsync(fixture, TestContext.Current.CancellationToken);

        var byId = results.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var regressions = expected
            .Select(id => (Id: id, Result: byId.GetValueOrDefault(id)))
            .Where(e => e.Result is not { Passed: true })
            .Select(e => $"  {e.Id}: {e.Result?.Failure ?? "not run (renamed, removed or now excluded?)"}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var newlyPassing = results.Where(r => r.Passed && !expected.Contains(r.Id)).Select(r => r.Id).Order(StringComparer.Ordinal).ToArray();

        // TIMESTORE_COMPAT_REPORT_DIR keeps the passing list (to refresh the ratchet) after the scratch directory is gone.
        if (Environment.GetEnvironmentVariable(ReportDirectoryVariable) is { Length: > 0 } reportDirectory)
        {
            Directory.CreateDirectory(reportDirectory);
            File.WriteAllLines(Path.Combine(reportDirectory, "ceph-passed.txt"), results.Where(r => r.Passed).Select(r => r.Id).Order(StringComparer.Ordinal));
            File.WriteAllLines(Path.Combine(reportDirectory, "ceph-failed.txt"), results.Where(r => !r.Passed).Select(r => $"{r.Id}: {r.Failure}").Order(StringComparer.Ordinal));
        }

        output.WriteLine($"s3-tests {CephS3TestsRunner.Commit[..7]}: {results.Count(r => r.Passed)} passed, {results.Count(r => !r.Passed)} failed, {expected.Count} expected.");
        if (newlyPassing.Length > 0)
            output.WriteLine($"Newly passing (add to {ExpectedFile}):\n{string.Join('\n', newlyPassing)}");
        Assert.True(regressions.Length == 0, $"{regressions.Length} expected s3-tests no longer pass:\n{string.Join('\n', regressions)}");
    }
}
