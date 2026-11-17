---
paths:
  - "backend/Src/**/*.cs"
---

# Performance rules (hot path: PUT, GET, HEAD, LIST, multipart)

## Memory

- **Never buffer a whole body or object.** Stream request bodies to the blob store; stream blobs to the response.
  No `MemoryStream` copies, no `ReadAllBytes`, no `ToArray()` on payloads.
- Buffers come from `ArrayPool<byte>.Shared` (returned in `finally`) or `stackalloc` for small, bounded sizes
  (≤ 512 bytes). Size limits come from constants.
- Parse with `Span<T>`/`ReadOnlySpan<char>`/UTF-8 bytes; do not split strings into arrays on the hot path.
- Results are structs (`Result<T>`); errors are cached `static readonly` when they have no parameters.
- Avoid LINQ, closures and `params` arrays on the hot path; plain loops over arrays or spans.

## I/O

- Async all the way; never `.Result`, `.Wait()` or `GetAwaiter().GetResult()`.
- GET of a whole or ranged blob: open the blob stream first, then copy through one pooled buffer (Kestrel has no
  kernel `sendfile`; `SendFileAsync` is the same copy).
- Writes: temp file → `Flush(flushToDisk: true)` → atomic rename; metadata commit after the blob is durable.
- Hash while streaming (`IncrementalHash`); never re-read a blob to hash it.
- Blobs a commit releases (overwrite, delete, abort, replaced part) go to `IBlobDeletions.DeferAsync`, never
  straight to `IBlobStore.DeleteAsync`: GETs still reading them must complete. Blobs of writes that did not commit
  are deleted at once.

## Metadata (SQLite)

- One writer: every write goes through `SqliteWriter` (single connection, `Channel`, group commit, one `SAVEPOINT`
  per write). Never open a write transaction on a pooled connection; set `command.Transaction` on every command
  inside a write.
- Reads use pooled connections and indexes; every new query is checked against `docs/database.md`.

## Proof

- A change on the hot path comes with a number: BenchmarkDotNet (`backend/Benchmarks`) for parsing/signing/XML,
  `warp` (`deploy/bench/warp.sh`, on macOS and the Linux image) for end-to-end throughput and latency. Record
  before/after in `docs/performance.md` and the change description.
- Runtime settings were chosen from measurements (step 5e): Server GC with DATAS, Runtime Async on, stated in
  `TimeStore.Server.csproj`. Change them only with a new warp table (throughput and peak RSS, macOS and Linux).
