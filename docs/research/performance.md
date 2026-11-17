# Research: performance (phase 5)

Rule: measure before changing anything on the hot path (`.claude/rules/performance.md`). This note records the
first baseline, where the time goes, and the plan.

## 1. Baseline (2026-11-16)

Native AOT binary on the development machine (Apple M4, 10 cores, macOS; data on the internal SSD), warp 1.3.1 (the
newest published image; `minio/warp:v1.3.1`, executed as a load generator only) in Docker on the same machine,
default configuration (`synchronous=FULL`, file fsync + directory fsync per blob).

| Workload | Throughput | Latency |
|---|---|---|
| GET 64 KiB, 16 clients | 12 500 obj/s, 780 MiB/s | p50 1.2 ms, p99 7.9 ms |
| PUT 16 MiB, 8 clients | 56 obj/s, 900 MiB/s | p50 194 ms, p99 353 ms |
| PUT 1 KiB, 1 client | 149 obj/s | p50 6.4 ms, p99 11 ms |
| PUT 1 KiB, 16 clients | 214 obj/s | p50 55 ms, **p99 1.2 s**, max 2.8 s |
| PUT 64 KiB, 16 clients | 218 obj/s, 14 MiB/s | p50 56 ms, **p99 700 ms** |

Reads and large writes are healthy. **Small writes do not scale**: 16 clients get 1.4× one client, with second-long
tail latencies.

## 2. Where the time goes (small PUT)

Every PUT does, in order: write the blob to a temp file, `fsync` it (`F_FULLFSYNC` on macOS), rename, `fsync` the
shard directory, then one SQLite transaction (`BEGIN IMMEDIATE` … `COMMIT`, WAL with `synchronous=FULL`: one more
fsync of the WAL).

- The blob fsyncs are per request and run in parallel across requests.
- The **SQLite commit is serialized**: one writer at a time, and the writer holds the lock through the WAL fsync.
  Concurrent requests do not queue in order: they hit `SQLITE_BUSY` and the busy handler sleeps with growing
  intervals until the lock is free, which is where the second-long tails come from.
- So the throughput of small PUTs is roughly one WAL fsync per object, plus wasted sleeps.

**Single writer + group commit** fixes both: one task owns the write connection and drains a queue; each round
commits every queued write in one transaction (one fsync for the batch), each write in its own `SAVEPOINT` so one
failing condition (`If-None-Match`, missing bucket) does not undo the others. Callers await their own result.
Durability stays `synchronous=FULL`: a PUT is acknowledged only after the commit that contains it.

## 3. Other items already on the roadmap

- **Per-connection pragmas** run on every pooled open today (`foreign_keys`, `synchronous`, `busy_timeout`): one
  extra statement per query. Apply them once per physical connection (the `sqlite3` handle is stable across pool
  reuse) or through the connection string (`Foreign Keys=True`, `Default Timeout`).
- **Delayed deletion of replaced blobs**: today a GET of a multipart object can fail mid-stream when an overwrite
  frees its parts between two parts (step 3d). Deleting replaced blobs after a short grace (seconds), with the orphan
  sweep as the backstop after a crash, removes that failure and takes the deletes off the request path.
- **SigV4 / aws-chunked hot path** (review of phase 1): `Split`/LINQ in `SigV4Verifier`, allocations in canonical
  query and header handling, strings to sign per chunk. Measure with BenchmarkDotNet first; GET numbers above suggest
  it is not the bottleneck today.
- **Runtime Async on/off, GC mode** (Server GC vs DATAS): measure, do not assume. Done in step 5e.

## 4. Proposed steps

| Step | Content | Proof |
|---|---|---|
| 5a ✅ | Benchmark harness: an opt-in warp runner (pinned image, fixed workloads, results as a table) and a BenchmarkDotNet project for parsing/signing/XML; record the baseline in `docs/performance.md` | Baseline table committed |
| 5b ✅ | SQLite single writer + group commit (queue, savepoints, durability unchanged); pragmas once per physical connection | warp PUT 1 KiB ×16: throughput up, p99 down; contract tests unchanged |
| 5c ✅ | Delayed deletion of replaced and deleted blobs (grace, sweep as backstop) | The step 3d mid-stream failure no longer happens (test); PUT/DELETE latency |
| 5d ✅ | SigV4/aws-chunked allocations, if the benchmarks show they matter | Closed without a change: numbers in `docs/performance.md` |
| 5e ✅ | Runtime Async and GC mode measurements; defaults chosen from numbers | warp table (throughput + peak RSS) |

## 5. Decisions (2026-11-16)

1. **Group commit with `synchronous=FULL` kept**: a PUT is acknowledged only after the batch holding it is synced; no
   relaxed-durability mode.
2. **Measure on macOS and on Linux**: the development machine, plus the Linux AOT binary in a container (closer to a
   deployment; Linux fsync differs from macOS `F_FULLFSYNC`).
3. **Replaced and deleted blobs are deleted after 60 seconds** (configurable), from an in-memory queue; the orphan
   sweep removes what a crash leaves behind.
4. **Server GC with DATAS and Runtime Async on** (step 5e): the defaults won on numbers and are now stated in
   `TimeStore.Server.csproj`.
5. **No allocation rewrite of SigV4/aws-chunked** (step 5d): not measurable end to end; the benchmarks stay as the
   regression baseline.

## 6. Notes from step 5a

- `deploy/bench/warp.sh` (host binary or Docker image) runs five fixed workloads and prints a Markdown table;
  `deploy/Dockerfile` builds the Linux Native AOT image (symbols and development settings stripped: the development
  settings hold the development key pair). `backend/Benchmarks/TimeStore.Benchmarks` holds the micro benchmarks.
- Linux is 5x faster than macOS for a single small PUT (fsync cost), but scales the same way (≈1.5x at 16 clients)
  with the same long tails: group commit is the next step on both.
- The micro benchmarks confirm the request path is not the bottleneck (signature 2 µs, chunk decoding 13 GB/s);
  step 5d moves to the end of the queue.

## 7. Notes from step 5b

- `SqliteWriter` owns the only write connection (not pooled; pragmas applied once when it opens) and a
  `Channel` of writes. A round takes up to 256 queued writes, runs each in its own `SAVEPOINT` inside one
  `BEGIN IMMEDIATE` transaction, commits once, then completes each caller: a failing write is rolled back to its
  savepoint and reported after the commit; a failed commit fails the whole round and the writer reopens its
  connection.
- Writes ignore the caller's cancellation once queued: a caller must never conclude that a write did not happen
  when it did (it would delete a blob that a committed row points to).
- All 17 write methods of `SqliteMetadataStore` go through the writer; reads keep pooled connections and no longer
  run any per-open statement (`foreign_keys`/`synchronous` only matter for writes). The schema migrator runs
  without pragmas: SQLite's default `synchronous` is FULL, and foreign keys off is what SQLite recommends while
  rebuilding tables.
- Deterministic tests hold a round open with a gate: 100 writes queued meanwhile commit in exactly one round; a
  write that throws after inserting leaves no row while its neighbours commit; a constraint violation fails only
  its write; disposing drains the queue.
- Result: see `docs/performance.md`. Linux small PUTs 3.3x, p99 4x lower, max 17x lower; macOS tails gone,
  throughput bound by `F_FULLFSYNC` of the blob files.

## 8. Notes from step 5c

- `IBlobDeletions` (Application) / `DeferredBlobDeletions` (Infra): every blob a commit releases (overwrite,
  delete, multi-delete, copy over, replaced part, completion's unlisted parts, abort, bucket deletion, expired
  uploads) goes into an in-memory FIFO and is deleted after `TimeStore:Blobs:DeleteDelaySeconds` (60); a loop drains
  due entries every second. Blobs of writes that never committed are still deleted at once (nobody can read them).
- The orphan sweep skips pending blobs: a released blob is old (written long ago) and unreferenced, so without that
  check a sweep could delete it before its grace and break exactly the read the grace protects.
- Shutdown deletes what is still pending (no request reads anymore); after a crash the sweep finds the blobs.
- A delay of 0 keeps the old behaviour; unit tests of services use it so blob counts stay exact. End-to-end servers
  use 1 s; tests that check freed files watch the files they created themselves (other tests' deferred deletions
  would skew a global count).
- Proof: `OverwriteWhileReadingTests` reads 1 MiB of a three-part object, overwrites or deletes it, then reads the
  rest: identical bytes with the grace; with the grace set to 0 both cases fail with "The response ended
  prematurely", the step 3d limit.
- No throughput change (warp writes unique keys; nothing is released on that path).
- **Benchmark incident**: the image declares `VOLUME /data`, and `warp.sh` removed its container without `-v`, so
  every run left an anonymous volume with its data (about 10 GB). Four runs filled the Docker VM disk, which showed
  as falling throughput. Fixed in the script (`docker rm -f -v`); the leaked volumes were removed; numbers recorded
  before the disk filled are unaffected.

## 9. Notes from step 5e

Raw runs (every round, every percentile): `docs/benchmarks/2026-11-16-step-5e.md`.

- Configurations: A defaults (Server GC + DATAS, Runtime Async on), B DATAS off, C Workstation GC, D Runtime Async
  off. GC knobs are environment variables the Native AOT runtime reads (checked first: Workstation GC starts four
  fewer threads on the 4-vCPU VM); D is a separate build with `-p:Features=runtime-async=off` (a global property
  replaces the value in `Directory.Build.props`).
- `warp.sh` gained `SERVER_ENV` and a peak-RSS column. The first version read `docker stats`, whose number includes
  the page cache of the blobs the container wrote (it grew from 18 to 66 MiB "idle" across fresh containers); it now
  reads the process RSS (`ps` / `docker top -o pid,rss`).
- Linux: 5 rounds per configuration, interleaved, two in reverse order. PUT numbers swing ±20% and more within one
  configuration (fsync on the VM disk); GET 64 KiB ×16 is CPU-bound and stable to a few percent, so it is the
  signal: C is ~5% slower in every round, A/B/D are equal.
- macOS: one round each, then A/B twice more alternately. A first-round +20% for B on small PUTs did not reproduce.
  B's cost is memory: 134–177 MiB against ~50 MiB, one heap per core (10 cores); on Linux (4 vCPU) the gap is smaller.
- Runtime Async on/off: no measurable difference in throughput or memory. Kept on: it is the .NET 11 direction,
  costs nothing, and `RuntimeAsyncTests` already guards it.
- Decision: keep Server GC with DATAS. Workstation GC saves ~10% memory but loses ~5% on reads; DATAS off loses
  memory for nothing. Both are SDK defaults today; `TimeStore.Server.csproj` now states them
  (`ServerGarbageCollector`, `GarbageCollectionAdaptationMode=1`) so a default change cannot switch them silently.
  Verified in the AOT binary: building with `GarbageCollectionAdaptationMode=0` reproduces B's memory profile.

## 10. Notes from step 5d

- Added a benchmark for signed chunks (the per-chunk string to sign and HMAC chain the roadmap item named); the
  existing ones were rerun.
- Signed chunks: 414 µs per MiB (≈ 2.5 GB/s), 21 KB allocated (≈ 1.2 KB per 64 KiB chunk). The time is the SHA-256
  of the payload, required by the protocol; the strings are a small fraction.
- Header verification: 2.25 µs and 4.5 KB per request, under 0.3% of the CPU budget per PUT at the measured rate.
- Step 5e is the end-to-end evidence: changing how much the GC can do (Workstation vs Server, DATAS on/off) leaves
  PUT and GET throughput unchanged, so removing a few KB of garbage per request cannot be measured.
- Closed without a code change. Reopen if a profile of a CPU-bound workload (GET of small objects, many clients on
  more cores) shows SigV4 or the GC in its top frames; the listing XML (≈ 680 B per key) is the first candidate then.

## 11. Comparing with other servers

Asked for on 2026-11-17: how TimeStore stands against other self-hosted S3 servers. Public comparisons exist
(nightly warp runs, community gists, vendor posts) but use different hardware and settings and disagree; only a run
on one machine with the same limits says anything about TimeStore.

| Topic | Decision |
|---|---|
| Harness | `deploy/bench/compare.sh`: warp (the same tool as `warp.sh`) against each server in Docker, one at a time, fresh volume per run, `--cpus`/`--memory` limits for the server and for warp, medians of `REPS` runs, a Markdown report |
| Servers | MinIO (`pgsty/minio`, the community build: the official images and downloads are gone since the community edition was archived; quay.io refuses pulls, dl.min.io answers 410), SeaweedFS, RustFS, Garage. Ceph RGW is left out (several daemons; not a single-node comparison) |
| Durability | As close to "on disk before the answer" as each allows: Garage `data_fsync`/`metadata_fsync`, SeaweedFS `fs.configure -fsync` on `/buckets/`, MinIO and TimeStore by default; RustFS has no documented setting (reported as "not verified") |
| Workloads | `warp.sh`'s PUT and GET plus STAT and MIXED; deletes are read from MIXED, because warp's DELETE benchmark empties its prepared objects in under a second on fast servers ("too few samples") |
| Memory | Resident memory of the server's processes (`docker top`), not `docker stats` (that includes the page cache) |
| Third-party code | Servers run as black boxes; their code is not read |

Notes: SeaweedFS's `weed server` stops accepting uploads after a few buckets (7 volumes per bucket against a default
cap of 8, or free disk / volume size with `-volume.max=0`); the harness gives it 256 MB volumes and a cap of 500
(volumes are not preallocated). Garage needs a layout and an imported key (`GK` + 24 hex, 64-hex secret) before the
first request. Results: `docs/benchmarks/2026-11-17-compare.md` (macOS, Docker Desktop) and
`docs/benchmarks/2026-11-17-compare-linux-4vcpu.md` (4-vCPU Linux VPS; warp is CPU-bound there).

