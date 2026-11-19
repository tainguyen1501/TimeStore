using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TimeStore.Application.Features.Backups;
using TimeStore.Contracts.Backups;
using TimeStore.Contracts.Common;
using TimeStore.Server.Tests.Infrastructure;

namespace TimeStore.Server.Tests.Admin;

/// <summary>
/// The backup directory with no configuration: <c>&lt;DataPath&gt;/backups</c> on the data volume, readable by the
/// server's user only, keeping the newest <c>Backup:Keep</c> archives; an archive is deleted through the API once taken.
/// </summary>
public sealed class BackupDirectoryTests(BackupDirectoryTests.DefaultDirectoryServer server)
    : IClassFixture<BackupDirectoryTests.DefaultDirectoryServer>
{
    private const string Backups = "/api/v1/backups";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private string Directory => Path.Combine(server.DataPath, "backups");

    [Fact]
    public async Task Backups_GoToTheDataDirectory_KeepTheNewest_AndAreDeletedOnceTaken()
    {
        using var backup = server.BackupClient();

        var first = await BackUpAsync(backup);
        Assert.True(File.Exists(Path.Combine(Directory, first.ArchiveName!)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(Directory, first.ArchiveName!)));
        }

        await Task.Delay(TimeSpan.FromSeconds(1.1), _ct); // archive names carry the second
        var second = await BackUpAsync(backup);

        // Keep = 1: the new archive replaced the first one.
        Assert.Equal([second.ArchiveName!], System.IO.Directory.GetFiles(Directory).Select(Path.GetFileName));
        await AssertErrorAsync(await backup.GetAsync($"{Backups}/{first.Id}/archive", _ct), HttpStatusCode.NotFound, BackupErrors.Codes.ArchiveNotFound);

        using (var download = await backup.GetAsync($"{Backups}/{second.Id}/archive", _ct))
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await backup.DeleteAsync($"{Backups}/{second.Id}/archive", _ct)).StatusCode);

        Assert.Empty(System.IO.Directory.GetFiles(Directory));
        await AssertErrorAsync(await backup.DeleteAsync($"{Backups}/{second.Id}/archive", _ct), HttpStatusCode.NotFound, BackupErrors.Codes.ArchiveNotFound);
        Assert.Equal(BackupRunStatuses.Succeeded, (await backup.GetFromJsonAsync<BackupRunResponse>($"{Backups}/{second.Id}", Json, _ct))!.Status);
    }

    [Fact]
    public async Task DeletingAnArchive_NeedsASignedBackupOrAdminKey()
    {
        using var anonymous = server.AnonymousAdminClient();

        var response = await anonymous.DeleteAsync($"{Backups}/{Guid.CreateVersion7()}/archive", _ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<BackupRunResponse> BackUpAsync(HttpClient backup)
    {
        var started = await backup.PostAsJsonAsync(Backups, new StartBackupRequest(), Json, _ct);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = (await started.Content.ReadFromJsonAsync<BackupRunResponse>(Json, _ct))!.Id;
        BackupRunResponse? run = null;
        await TimeStoreServer.WaitUntilAsync(() =>
        {
            run = backup.GetFromJsonAsync<BackupRunResponse>($"{Backups}/{id}", Json, _ct).GetAwaiter().GetResult();
            return run!.Status is not (BackupRunStatuses.Queued or BackupRunStatuses.Running);
        }, _ct);
        Assert.Equal(BackupRunStatuses.Succeeded, run!.Status);
        return run;
    }

    private async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, _ct))!.Code);
    }

    /// <summary>No Backup:Path: the default directory inside the data directory (free-space margin off for tests).</summary>
    public sealed class DefaultDirectoryServer : TimeStoreServer
    {
        protected override void Configure(IDictionary<string, string?> environment) =>
            environment["TimeStore__Backup__MinFreeBytes"] = "0";
    }
}
