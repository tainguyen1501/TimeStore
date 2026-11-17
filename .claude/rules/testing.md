---
paths:
  - "backend/Tests/**/*.cs"
---

# Testing rules

- Run: `cd backend && dotnet test --solution TimeStore.slnx` (xunit v3 on Microsoft.Testing.Platform, enabled in
  `global.json`). Test projects are executables (`OutputType Exe`) with a global `using Xunit`.
- **Projects**
  - `TimeStore.UnitTests`: services with fakes (`Fakes/`), domain rules, protocol parsers/writers, and
    `Conventions/` (dependency rules, forbidden packages, runtime async). Add a rule there for every new project.
  - `TimeStore.Metadata.ContractTests`: `MetadataStoreContract` + one subclass per provider creating a fresh store.
  - `TimeStore.Server.Tests`: conventions over the S3 port (`S3ErrorMapTests`) and end-to-end tests: `TimeStoreServer`
    starts the real server as a child process (free ports, temp data dir, its own root key, Production) and tests drive
    it with the AWS SDK for .NET over HTTP. Set `TIMESTORE_SERVER_BINARY` to run the same tests against a Native AOT
    binary. Use a new bucket name per test (`e2e-<guid>`); assert SDK errors with `ThrowsAnyAsync<AmazonS3Exception>`
    (the SDK throws typed subclasses).
  - `TimeStore.Compat.Tests`: real clients against the running server, **opt-in** (`TIMESTORE_COMPAT=1`; needs Docker
    and `uv`): AWS CLI and rclone in pinned images, s3cmd through `uvx` (versions in `CompatClients`, probed versions
    in `docs/research/s3-compatibility.md`). Without the variable every test calls `Assert.SkipUnless` first and no
    server starts. Shares `TimeStoreServer` (linked file), so `TIMESTORE_SERVER_BINARY` works here too. Clients run
    without a shell; secrets go by environment variable (`docker run -e NAME`) or s3cmd's config file in the
    scratch directory, never on a command line or in an assertion message. Compare downloaded files byte for byte.
    `Ceph/CephS3Tests` runs the pinned ceph s3-tests subset (`CephS3TestsRunner`: commit, excluded markers) as a
    ratchet: every test in `Ceph/expected-pass.txt` must pass; add the tests a run reports as newly passing (set
    `TIMESTORE_COMPAT_REPORT_DIR` to keep the passed/failed lists), never remove one without a recorded reason.
    `SigV4/SigV4TestSuiteTests` runs the applicable cases of the official SigV4 suite (aws-c-auth, pinned commit)
    through `SigV4Canonical`/`SigV4Signer`; a new rule in either must keep it green.
- **Isolation**: every test creates its own store/bucket/temp directory and removes it; never rely on data from
  another test. Never disable parallelization for an assembly.
- Pass `TestContext.Current.CancellationToken` to every async call (xunit analyzer xUnit1051).
- Compare error codes with catalog constants (`BucketErrors.Codes.NotFound`), never string literals.
- Test names: `Action_Condition_ExpectedResult`. Cover the success path and every error a service can return.
- Control time with a fake `TimeProvider`; never `Task.Delay` for timing assertions.
- Tests never call real AWS or the network. Only the opt-in compatibility tests download (pinned images and
  packages, suites fetched at test time and never committed), and they still talk only to the local server.
- **SigV4 references**: the published AWS documentation examples (`SigV4DocExamplesTests`) and the real AWS SDK for
  .NET (`AWSSDK.S3`, test-only) whose signed requests are captured in memory by a fake `HttpMessageHandler`
  (`SigV4AwsSdkTests`). Pin SDK behaviour we depend on (payload modes, trailers) with assertions there.
