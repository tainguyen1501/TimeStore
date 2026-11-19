using System.Net;
using TimeStore.Blob.FileSystem;
using TimeStore.Cryptography.Checksums;
using TimeStore.Cryptography.Secrets;
using TimeStore.Domain.Stores;
using TimeStore.Infra;
using TimeStore.Metadata.Sqlite;
using TimeStore.Application.Abstractions;
using TimeStore.Infra.Credentials;
using TimeStore.Protocol.Auth;
using TimeStore.Protocol.Routing;
using TimeStore.Server;
using TimeStore.Server.Admin;
using TimeStore.Server.S3;
using TimeStore.Server.Workers;
using TimeStore.Application.Features.Maintenance;
using TimeStore.Application.Features.Uploads;
using TimeStore.Application.Features.Auth;
using TimeStore.Application.Features.Metrics;
using TimeStore.Server.Metrics;
using TimeStore.Application.Features.Backups;
using TimeStore.Infra.Backups;

var builder = WebApplication.CreateSlimBuilder(args);

var options = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
// Offline restore: needs only the data path and master key settings, not a complete server configuration.
if (RestoreCommand.Matches(args))
    return await RestoreCommand.RunAsync(args, options);

// Problems the operator fixes (configuration, data directory): one line and exit code 1, not a crash with a stack trace.
const int StartupRefused = 1;
if (options.Validate() is { } problem)
{
    await Console.Error.WriteLineAsync($"TimeStore cannot start: {problem}");
    return StartupRefused;
}

byte[] masterKey;
try
{
    masterKey = string.IsNullOrWhiteSpace(options.MasterKey)
        ? MasterKeyFile.Load(options.MasterKeyFile!)
        : MasterKeyFile.Parse(options.MasterKey, $"{ServerOptions.Section}:{nameof(ServerOptions.MasterKey)}");
}
catch (InvalidOperationException failure)
{
    await Console.Error.WriteLineAsync($"TimeStore cannot start: {failure.Message}");
    return StartupRefused;
}

var protector = new AesGcmSecretProtector(masterKey);
System.Security.Cryptography.CryptographicOperations.ZeroMemory(masterKey); // lives on only inside the protector

// A restore was cut half way (a pod killed during the chart's restore init container): never serve partial data.
if (File.Exists(Path.Combine(options.DataPath, BackupRestorer.InProgressMarker)))
{
    await Console.Error.WriteLineAsync(
        $"TimeStore cannot start: the data directory {Path.GetFullPath(options.DataPath)} holds an unfinished restore "
        + $"({BackupRestorer.InProgressMarker}). Run the restore again (it starts over), or empty the directory.");
    return StartupRefused;
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    // User metadata may hold non-ASCII text (clients send UTF-8, which Kestrel already reads). Responses default to
    // ASCII and would fail (500) on such a value; see S3MetadataHeaders for how it is written instead.
    kestrel.ResponseHeaderEncodingSelector = S3MetadataHeaders.Encoding;
    kestrel.ListenAnyIP(options.S3Port);
    kestrel.Listen(IPAddress.Parse(options.AdminAddress), options.AdminPort);
});

if (options.Admin.OpenApi)
    builder.Services.AddAdminOpenApi();
builder.Services.AddAdminUi();

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.TypeInfoResolverChain.Insert(0, AdminJsonContext.Default);
    // Numbers are numbers (the Web default also reads them from strings, and OpenAPI then says "integer or string").
    json.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict;
});

// Malformed or missing bodies throw BadHttpRequestException, answered as COMMON_INVALID_REQUEST (ExceptionHandling).
builder.Services.Configure<RouteHandlerOptions>(routing => routing.ThrowOnBadRequest = true);

// Composition root: the only place that knows which providers run.
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(new S3Router(options.S3.Domains));
builder.Services.AddSingleton(new RootCredential(new AccessCredential(options.S3.RootAccessKey, options.S3.RootSecretKey)));
builder.Services.AddSingleton<ISecretProtector>(protector);
builder.Services.AddSingleton<ISecretKeyResolver, CredentialSecretResolver>();
builder.Services.AddSingleton<SigningKeyCache>();
builder.Services.AddSingleton(new SigV4Options(options.S3.Region, TimeSpan.FromSeconds(options.S3.MaxClockSkewSeconds)));
builder.Services.AddSingleton<SigV4Verifier>();
builder.Services.AddSingleton<IChecksumFactory, ChecksumFactory>();
builder.Services.AddSingleton<S3BucketHandlers>();
builder.Services.AddSingleton<S3ObjectHandlers>();
builder.Services.AddSingleton<S3UploadHandlers>();
builder.Services.AddSingleton<S3Dispatcher>();
builder.Services.AddSingleton<S3Authorization>();
builder.Services.AddSingleton(new BlobSweepOptions(options.BlobSweep.GracePeriod));
builder.Services.AddSingleton(new UploadExpiryOptions(options.Multipart.AbandonAfter));
builder.Services.AddSingleton(options.Admin);
builder.Services.AddSingleton(new AdminSessionOptions(options.Admin.SessionLifetime));
builder.Services.AddSingleton(new BlobDeletionOptions(options.Blobs.DeleteDelay));
builder.Services.AddSingleton(new StorageMetricsOptions(options.DataPath));
builder.Services.AddSingleton(new BackupOptions(
    options.NodeId, options.DataPath, Path.Combine(options.DataPath, FileSystemBlobStore.TempDirectory),
    options.Backup.Compression, options.Backup.CompressionLevel, options.Backup.MinFreeBytes));
if (options.Backup.HasDirectory)
    builder.Services.AddDirectoryBackupTarget(new DirectoryBackupOptions(options.Backup.Path!));
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddSingleton<S3RequestMetrics>();
builder.Services.AddSingleton<MetricsSampler>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MetricsSampler>());
if (options.Lifecycle.Enabled)
    builder.Services.AddHostedService<LifecycleWorker>();
if (options.BlobSweep.Enabled || options.Multipart.AbandonAfter is not null)
    builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddTimeStoreServices();
builder.Services.AddSqliteMetadataStore(new SqliteMetadataOptions(Path.Combine(options.DataPath, ServerOptions.MetadataFileName)));
builder.Services.AddFileSystemBlobStore(new FileSystemBlobOptions(options.DataPath, options.NodeId));

var app = builder.Build();

if (!IPAddress.IsLoopback(IPAddress.Parse(options.AdminAddress)) && !options.Admin.SecureCookies)
{
    app.Logger.LogWarning(
        "The admin API listens on {Address} over plain HTTP: sign-in secrets and session cookies travel in clear text. "
        + "Put it behind a TLS proxy and set TimeStore:Admin:SecureCookies=true.", options.AdminAddress);
}

foreach (var initializer in app.Services.GetServices<IStorageInitializer>())
    await initializer.InitializeAsync(app.Lifetime.ApplicationStopping);

var (unreadable, accessKeys) = await MasterKeyCheck.CountUnreadableAsync(
    app.Services.GetRequiredService<IMetadataStore>(), protector, app.Lifetime.ApplicationStopping);
if (unreadable > 0)
{
    await Console.Error.WriteLineAsync(
        $"TimeStore cannot start: the master key does not decrypt {unreadable} of {accessKeys} stored access keys, so it "
        + "is not the key they were encrypted with. Configure the original key (a new key cannot read them).");
    return StartupRefused;
}

app.UseTimeStoreExceptionHandler(options.S3Port);
app.UseS3Api(options); // S3 port: own middleware branch + dispatcher, never reaches endpoint routing
app.UseAdminAuthentication(); // admin port only: the S3 branch above answers every S3 request
app.UseRouting();
app.MapHealthEndpoints();
app.MapAdminApi();
app.MapAdminObjectApi();
app.MapAdminMetricsApi();
app.MapAdminBackupApi();
if (options.Admin.OpenApi)
    app.MapOpenApi(); // /openapi/v1.json, Development only by default (AdminSettings.OpenApi)
app.UseAdminUi(options.Admin); // everything else on the admin port: the React UI

await app.RunAsync();
return 0;
