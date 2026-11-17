# .NET 11 / C# 15: what TimeStore uses

Researched 2026-11-16. Release metadata: SDK 11.0.100, released 2026-11-10, **STS**, end of support 2028-11-14.
How we apply it: `.claude/rules/csharp.md`, `aot.md`, `performance.md`.

## C# 15

| Feature | Status | Use in TimeStore |
|---|---|---|
| Closed hierarchies (`closed record`) | shipped | S3 operations, outcomes: exhaustive switches without `default` |
| Union types (`union X(A, B)`) | shipped | Only with class cases off the hot path: the default union stores `object` (struct cases box), default value has no case |
| Collection expression arguments (`[with(capacity: n), ..]`) | shipped | Pre-sized collections |
| Extension indexers (extension blocks from C# 14) | shipped | Header/query helpers |
| Labeled `break`/`continue` | shipped | Nested parse loops |
| Non-virtual static interface members on older runtimes | shipped | — |
| Memory safety rules (`unsafe(expr)`, `safe`) | **preview** | Not used |

## Runtime

- **Runtime Async**: async handled by the runtime (fewer allocations, shorter stack traces), supports NativeAOT and
  ReadyToRun. The docs both say "add `<Features>runtime-async=on</Features>` to enable" and "set
  `<UseRuntimeAsync>false</UseRuntimeAsync>` to opt out"; no `EnablePreviewFeatures` needed on net11.0. **Verified on SDK 11.0.100 (2026-11-16): it is off by default; the `<Features>` property turns it on.** We set it
  in `Directory.Build.props` and guard it with `RuntimeAsyncTests`. Only code we compile benefits; packages built for older
  TFMs keep state machines.
- NativeAOT: interface dispatch through a shared helper (smaller call sites, faster interface-heavy code: our
  port/service design).
- JIT: more bounds-check elimination (`values[^1]`, `i + c < len`), switch folding, devirtualization.
- Minimum x64 hardware is now `x86-64-v2`.

## Libraries

- `Utf8.IndexOfInvalidSubsequence`, `Utf16.IsValid` → key validation.
- Base64 `EncodeToUtf8`/`DecodeFromUtf8` → `Content-MD5`, `x-amz-checksum-*`.
- `CryptographicOperations.FixedTimeEquals` overloads → signature comparison.
- `ReadOnlySequenceStream`, `ReadOnlyMemoryStream` → wrap pipe data without copies.
- Span-based Deflate/GZip/Zstandard encoders and decoders in `System.IO.Compression` → optional compression later.
- System.Text.Json: union serialization, `InferClosedTypePolymorphism`, `GetTypeInfo<T>()`; source-generation support
  for unions not confirmed yet.

## ASP.NET Core 11

- Kestrel HTTP/1.1 parser no longer throws on malformed requests (docs: 20–40% faster for bad requests).
- `[ShortCircuit]` attribute for endpoints (we use the `.ShortCircuit()` extension for health).
- OpenAPI 3.2 by default (`OpenApiVersion = OpenApi3_1` if openapi-typescript lags).
- HTTP/3 starts processing before the control stream settles.

## SDK

- `UseOptimizedPublish` (opt-in) for Native AOT; MSBuild server on by default.
- Reproducible container digests with `SOURCE_DATE_EPOCH`; `dotnet publish /t:PublishContainer` as a Dockerfile
  alternative.
- File-based apps (`#:include`, `#:package`) for small tools such as load generators.
- `dotnet test --timeout`, `--maximum-failed-tests`.

## Sources

- https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries
- https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/sdk
- https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11
- https://devblogs.microsoft.com/dotnet/csharp-15-union-types/
- https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/
- https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json (release date, support phase)
