---
paths:
  - "backend/**/*.cs"
---

# C# and .NET 11 usage

Target `net11.0`, C# 15 (default). **No preview language features** (memory-safety rules stay off).

Research and sources: `docs/research/dotnet-11.md`.

## Build settings

```xml
<!-- backend/Directory.Build.props: every project -->
<TargetFramework>net11.0</TargetFramework>
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
<Features>$(Features);runtime-async=on</Features>   <!-- required: off by default (verified on SDK 11.0.100) -->
<!-- backend/Src and backend/Core Directory.Build.props: production code -->
<IsAotCompatible>true</IsAotCompatible>
<!-- TimeStore.Server.csproj only -->
<PublishAot>true</PublishAot>
<UseOptimizedPublish>true</UseOptimizedPublish>     <!-- opt-in in .NET 11 -->
```

## Closed hierarchies: closed sets of cases (preferred)

Use for S3 operations, request outcomes, job kinds. Switch without `default`; adding a case breaks the build.

```csharp
public closed record S3Operation;
public sealed record GetObject(string Bucket, string Key) : S3Operation;
public sealed record PutObject(string Bucket, string Key) : S3Operation;

return op switch
{
    GetObject g => await objects.GetAsync(g, ct),
    PutObject p => await objects.PutAsync(p, ct),
};
```

## Unions: only with class cases, never on the hot path with struct cases

The generated union stores the value as `object` (struct cases box). Its default value has no case, so handle `null`.

```csharp
public sealed record ObjectFound(ObjectVersion Version);
public sealed record NotModified(string ETag);
public union GetObjectOutcome(ObjectFound, NotModified, AppError);

var response = outcome switch
{
    ObjectFound f => ...,
    NotModified n => ...,
    AppError e => ...,
    null => throw new UnreachableException(),
};
```

Hot-path success/failure stays on the hand-written `Result` / `Result<T>` **struct** in `Core/*.Primitives`.

## Small language features to use

```csharp
List<Part> parts = [with(capacity: count), .. uploaded];      // collection expression arguments

public static class S3HeaderExtensions                        // extension members (C# 14), indexers (C# 15)
{
    extension(IHeaderDictionary headers)
    {
        public string? AmzDate => headers[S3Headers.AmzDate];
    }
}

outer:                                                        // labeled break/continue instead of flags/goto
for (var i = 0; i < chunks.Length; i++)
{
    foreach (var b in chunks[i]) { if (b == '\n') continue outer; }
}

public int BufferSize { get; set => field = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)); }
upload?.LastPartAt = now;                                     // null-conditional assignment
```

## .NET 11 APIs to prefer

- Key validation: `Utf8.IndexOfInvalidSubsequence` / `Utf16.IsValid`.
- `Content-MD5`, `x-amz-checksum-*`: Base64 `EncodeToUtf8` / `DecodeFromUtf8` (no intermediate strings).
- Signature compare: `CryptographicOperations.FixedTimeEquals` only.
- Wrap pipe data: `ReadOnlySequenceStream` / `ReadOnlyMemoryStream`, never copy into `MemoryStream`.
- Compression (later): span-based `ZstandardEncoder`/`Decoder` in `System.IO.Compression`.
- Health endpoints: `.ShortCircuit()` (or `[ShortCircuit]` in .NET 11).
- OpenAPI defaults to 3.2: set `OpenApiVersion = OpenApi3_1` unless openapi-typescript supports 3.2.

## In this repository

- Runtime Async is **off by default** on SDK 11.0.100 (verified 2026-11-16); the `<Features>` property is required and
  `RuntimeAsyncTests` fails if it is lost.
- Packages that are part of the shared framework on net11.0 (e.g. `Microsoft.Extensions.DependencyInjection.Abstractions`)
  must not be referenced: NuGet raises NU1510 (an error here).
- Extension blocks are used for mappings (`BucketMappings`), `Clock.UtcNowMs` and `SqliteParameters.AddId`: follow
  that style instead of classic `this` extension methods.

## Open points

- Does System.Text.Json source generation support C# unions and `InferClosedTypePolymorphism`? Check before using
  a union or closed hierarchy in an admin API contract.
