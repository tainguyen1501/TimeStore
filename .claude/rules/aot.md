---
paths:
  - "backend/**/*.cs"
  - "backend/**/*.csproj"
  - "backend/**/*.props"
---

# Native AOT rules

The Server ships as a Native AOT binary. Trim/AOT analyzer warnings (IL2xxx, IL3xxx) are errors in production
projects (`IsAotCompatible` + `TreatWarningsAsErrors`). A suppression (`UnconditionalSuppressMessage`,
`DynamicDependency`) needs a comment proving why it is safe, and the reviewer must accept it.

## Forbidden

- Reflection over user types: `Activator.CreateInstance(Type)`, `Type.GetType(string)`, `GetMethods()`/`GetProperties()`
  for dispatch, `Expression.Compile()`, `System.Reflection.Emit`, `dynamic`.
- `XmlSerializer`, `DataContractSerializer`, reflection-based `JsonSerializer` calls (without a `JsonTypeInfo`).
- Packages: EF Core, Dapper (Dapper.AOT is allowed after review), Scrutor and any assembly scanning, AutoMapper,
  MediatR, FluentValidation, FluentResults, Newtonsoft.Json. Guarded by `ProjectReferenceTests`.
- Runtime code generation of any kind; `Assembly.Load*`.

## Required

- **JSON**: source generation. Every admin API type is in `AdminJsonContext`; serialize with a `JsonTypeInfo<T>`.
- **XML**: hand-written `XmlWriter` / `XmlReader` in `TimeStore.Protocol`.
- **Configuration**: `EnableConfigurationBindingGenerator` (on in the Server); options are plain classes.
- **DI**: explicit registrations; no open-generic tricks that need runtime type construction.
- **Minimal APIs**: the Request Delegate Generator runs with `PublishAot`; keep handlers as static methods with
  explicit parameter sources when ambiguous (`[FromServices]`).
- **Logging**: `[LoggerMessage]` source-generated methods on hot paths.
- **Data access**: raw ADO.NET with explicit parameter types (`SqliteParameters.AddId`).

## Native calls

- P/Invoke only through `[LibraryImport]` (source-generated, AOT-safe), never `[DllImport]` with runtime
  marshalling. Current use: `Blob.FileSystem/DirectorySync` (libc `open`/`fsync`/`close`), resolved per platform
  (macOS libSystem, glibc `libc.so.6`, musl). Verified on macOS (JIT and AOT); Linux is verified when the container
  image lands (phase 0 deploy / roadmap).

## Known incompatible packages (verified)

- `Microsoft.Extensions.Caching.Hybrid` 11.0.0: IL2026 in `DefaultJsonSerializerFactory` (static analysis of the
  library, not fixable by registering serializers). Use `ICacheService` → `MemoryCacheService` instead.

## Verify

- On macOS, `dotnet publish -r osx-arm64` prints native toolchain noise (`dsymutil` "skipping debug map object",
  `xcodebuild requires Xcode`) with only the Command Line Tools installed: harmless. Only IL2xxx/IL3xxx warnings count.
- Every new package: check it targets the current TFM, has no AOT warnings on publish, and add it to
  `Directory.Packages.props` with a comment if it needs care.
- Before calling a change done when it touches packages, the Server, or reflection-adjacent code:
  `dotnet publish backend/Src/TimeStore.Server -c Release -r <rid>` must finish **without warnings**, and the
  published binary must start and answer `/health/live`.
