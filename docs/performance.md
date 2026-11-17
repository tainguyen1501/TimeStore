# Performance baselines

How to measure, and the numbers each change is compared with. Rules: `.claude/rules/performance.md`; analysis and
plan: `docs/research/performance.md`.

## How to measure

```bash
# End to end (warp, pinned image; needs Docker). A fresh server and data directory per run.
deploy/bench/warp.sh host <publish dir>/TimeStore.Server       # Native AOT binary on this machine
docker build -f deploy/Dockerfile -t timestore:bench .          # Linux Native AOT image
deploy/bench/warp.sh docker timestore:bench                     # the same workloads against the container

# Against other S3 servers (MinIO, SeaweedFS, RustFS, Garage): same limits, same workloads, medians of REPS runs.
REPS=3 DURATION=20s OUT=docs/benchmarks/<date>-compare.md deploy/bench/compare.sh
SERVERS="timestore garage" REPS=1 DURATION=5s deploy/bench/compare.sh   # a quick subset

# Micro benchmarks of the request hot path (BenchmarkDotNet, in process).
cd backend && dotnet run -c Release --project Benchmarks/TimeStore.Benchmarks -- --filter '*'
```

`DURATION=30s deploy/bench/warp.sh …` lengthens each workload; `SERVER_ENV="DOTNET_gcServer=0" deploy/bench/warp.sh …`
passes runtime settings to the server. The last column is the server's peak resident memory. Compare runs on the
same machine; absolute numbers depend on the disk and on how the OS implements fsync (macOS uses `F_FULLFSYNC`).
`compare.sh` runs every server in Docker with `SERVER_CPUS` (4) and `SERVER_MEMORY` (4g), warp with `WARP_CPUS` (3),
one server at a time on a fresh volume, and sets each server's durability as close to "fsync before the answer" as
it allows (the report lists it per server). On macOS Docker runs in a VM: rank the servers, do not compare the numbers
with Linux runs. This file keeps summaries; when a decision rests on many runs, the unedited runs go to
`docs/benchmarks/<date>-<step>.md`.

## Against other S3 servers: 2026-11-17

`deploy/bench/compare.sh`, medians of 3 runs × 20 s, each server in Docker Desktop (macOS, Apple M4) with 4 CPUs and
4 GB; full report with latencies and memory: `docs/benchmarks/2026-11-17-compare.md`. Rank of TimeStore among
TimeStore, MinIO (community build), SeaweedFS 4.54, RustFS 1.0.6, Garage 2.5.0:

| Workload | TimeStore | Best of the others | Rank |
|---|---|---|---|
| PUT 1 KiB, 1 client | 894 obj/s | Garage 1,218 | 3 |
| PUT 1 KiB, 16 clients | 4,993 obj/s | RustFS 3,605 | 1 |
| PUT 64 KiB, 16 clients | 3,500 obj/s | MinIO 1,912 | 1 |
| PUT 16 MiB, 8 clients | 810 MiB/s | MinIO 706 | 1 |
| GET 64 KiB, 16 clients | 30,213 obj/s | RustFS 38,539 | 2 |
| STAT, 16 clients | 47,116 obj/s | RustFS 49,843 | 2 |
| MIXED 64 KiB, 16 clients | 13,534 obj/s | MinIO 7,152 | 1 |
| DELETE (in MIXED) | 1,353 obj/s | MinIO 715 | 1 |
| Memory idle / under load | 71 / 88–150 MiB | Garage 24 idle; under load all others 124–1,420 | 2 / 1 |

Caveats: Docker on macOS runs in a VM (rank, do not compare with Linux numbers); RustFS's durability is not verified
(no documented fsync setting); single-client and large-object runs vary between runs (the 5-second trial run had
MinIO at 1,715 obj/s for PUT 1 KiB × 1). Where TimeStore trails: single-client small PUTs (each waits for its own
fsync; group commit only helps concurrent writers) and GET/STAT against RustFS.

## Against other S3 servers on Linux: 2026-11-17 (4-vCPU VPS)

The same harness on a small KVM VPS (4 vCPUs Intel Haswell, 7.6 GB RAM, virtual disk, Ubuntu 24.04, Docker Engine):
each server 2 CPUs and 3 GB, warp 1.5 CPUs, medians of 3 runs × 20 s; full report:
`docs/benchmarks/2026-11-17-compare-linux-4vcpu.md`.

| Workload | TimeStore | Best of the others | Rank |
|---|---|---|---|
| PUT 1 KiB, 1 client | 199 obj/s | Garage 254 | 3 |
| PUT 1 KiB, 16 clients | 1,161 obj/s | RustFS 1,150 | 1 (tie) |
| PUT 64 KiB, 16 clients | 984 obj/s | RustFS 802 | 1 |
| PUT 16 MiB, 8 clients | 195 MiB/s | MinIO 180 | 1 |
| GET 64 KiB, 16 clients | 4,124 obj/s | RustFS 4,217 | 2 (tie) |
| STAT, 16 clients | 6,110 obj/s | RustFS 5,165 | 1 |
| MIXED 64 KiB, 16 clients | 2,808 obj/s | RustFS 1,819 | 1 |
| DELETE (in MIXED) | 281 obj/s | RustFS 182 | 1 |
| Memory idle / peak under load | 39 / 85 MiB | Garage 27 idle / 143 peak; the others 352–1,378 peak | 2 / 1 |

Caveats: warp ran at its 1.5-CPU cap in about three quarters of the samples, so the fastest servers look closer than
they are (differences under 5% are ties); the slow virtual disk (about 1 ms per fsync) bounds every PUT. Same
pattern as on macOS: TimeStore trails only on single-client small PUTs.

## Baseline: 2026-11-16 (before group commit)

Apple M4 (10 cores), macOS, internal SSD; Docker Desktop for the Linux runs. warp 1.3.1, 15 s per workload.

**macOS, Native AOT binary**

| Workload | Throughput | p50 | p90 | p99 | max |
|---|---|---|---|---|---|
| PUT 1 KiB x1 | 153 obj/s | 6.3 ms | 8.6 ms | 10.8 ms | 19.2 ms |
| PUT 1 KiB x16 | 226 obj/s | 43.2 ms | 108.5 ms | 720.1 ms | 2321.4 ms |
| PUT 64 KiB x16 | 219 obj/s (13.7 MiB/s) | 55.1 ms | 103.4 ms | 483.3 ms | 1585.4 ms |
| PUT 16 MiB x8 | 40 obj/s (647 MiB/s) | 204.8 ms | 296.6 ms | 356.3 ms | 407.4 ms |
| GET 64 KiB x16 | 12 105 obj/s (757 MiB/s) | 1.2 ms | 2.0 ms | 3.3 ms | 206.2 ms |

**Linux container (Docker Desktop VM), Native AOT image**

| Workload | Throughput | p50 | p90 | p99 | max |
|---|---|---|---|---|---|
| PUT 1 KiB x1 | 825 obj/s | 1.2 ms | 1.4 ms | 2.4 ms | 10.8 ms |
| PUT 1 KiB x16 | 1 267 obj/s | 9.8 ms | 19.0 ms | 50.6 ms | 854.7 ms |
| PUT 64 KiB x16 | 1 011 obj/s (63 MiB/s) | 13.6 ms | 30.3 ms | 79.3 ms | 451.8 ms |
| PUT 16 MiB x8 | 41 obj/s (653 MiB/s) | 171.5 ms | 252.5 ms | 478.2 ms | 798.7 ms |
| GET 64 KiB x16 | 21 982 obj/s (1 374 MiB/s) | 0.6 ms | 1.2 ms | 2.9 ms | 128.5 ms |

Reading: small PUTs scale ~1.5× from 1 to 16 clients on both systems, with long tails: the serialized SQLite commit
(see the research note). Reads and large writes are healthy.

**Micro benchmarks** (BenchmarkDotNet 0.15.8, in process, .NET 11, same machine)

| Benchmark | Mean | Allocated |
|---|---|---|
| SigV4 header verify (GET, AWS example) | 2.27 µs | 4 512 B |
| Canonical query (presigned GET) | 0.84 µs | 3 376 B |
| aws-chunked 1 MiB in 64 KiB chunks + CRC32 trailer | 75.4 µs (≈ 13 GB/s) | 712 B |
| ListObjectsV2 XML, 1000 keys | 553 µs | 683 016 B |

Reading: signature work is microseconds against milliseconds of fsync; not worth optimizing now. Listing XML
allocates about 680 bytes per key: a candidate once listings matter.

## Step 5b: single writer + group commit (2026-11-16)

Same machine and workloads. Durability unchanged (`synchronous=FULL`, a PUT is acknowledged after its round commits).

**Linux container, Native AOT image**

| Workload | Throughput | p50 | p90 | p99 | max |
|---|---|---|---|---|---|
| PUT 1 KiB x1 | 805 obj/s | 1.1 ms | 1.6 ms | 4.3 ms | 26.8 ms |
| PUT 1 KiB x16 | **4 151 obj/s** (was 1 267) | 3.3 ms | 5.9 ms | **12.3 ms** (was 50.6) | **50.6 ms** (was 854.7) |
| PUT 64 KiB x16 | **3 281 obj/s** (was 1 011; 205 MiB/s) | 4.4 ms | 8.2 ms | **15.8 ms** (was 79.3) | 45.9 ms |
| PUT 16 MiB x8 | 39 obj/s (626 MiB/s) | 211.6 ms | 321.9 ms | 450.1 ms | 487.9 ms |
| GET 64 KiB x16 | 21 346 obj/s (1 334 MiB/s) | 0.6 ms | 1.3 ms | 3.5 ms | 81.5 ms |

**macOS, Native AOT binary**

| Workload | Throughput | p50 | p90 | p99 | max |
|---|---|---|---|---|---|
| PUT 1 KiB x1 | 165 obj/s | 5.8 ms | 7.8 ms | 10.2 ms | 54.2 ms |
| PUT 1 KiB x16 | 265 obj/s (was 226) | 58.9 ms | 80.7 ms | **103.6 ms** (was 720.1) | **129.4 ms** (was 2321.4) |
| PUT 64 KiB x16 | 264 obj/s (was 219; 16.5 MiB/s) | 59.2 ms | 78.9 ms | **100.4 ms** (was 483.3) | 131.7 ms |
| PUT 16 MiB x8 | 41 obj/s (653 MiB/s) | 200.8 ms | 270.7 ms | 721.4 ms | 1126.0 ms |
| GET 64 KiB x16 | 11 563 obj/s (723 MiB/s) | 1.2 ms | 2.0 ms | 3.8 ms | 206.5 ms |

Reading: on Linux small PUTs now scale 5x from 1 to 16 clients (was 1.5x) and the second-long tails are gone. On
macOS the tails are gone too, but throughput barely moves: what is left is the blob's own durability, two
`F_FULLFSYNC` per PUT (file and directory), which flush the whole drive cache and queue at the device. That cost is
specific to macOS (Linux `fsync` is per file); it is not a deployment target, so it stays as is.

## Step 5c: delayed blob deletion (2026-11-16)

Linux container, clean Docker disk (see the incident in the research note):

| Workload | Throughput | p50 | p90 | p99 | max |
|---|---|---|---|---|---|
| PUT 1 KiB x1 | 784 obj/s | 1.3 ms | 1.5 ms | 2.2 ms | 49.1 ms |
| PUT 1 KiB x16 | 4 868 obj/s | 2.7 ms | 5.4 ms | 9.3 ms | 72.5 ms |
| PUT 64 KiB x16 | 3 547 obj/s (222 MiB/s) | 3.7 ms | 7.3 ms | 12.2 ms | 61.1 ms |
| PUT 16 MiB x8 | 64 obj/s (1 027 MiB/s) | 128.3 ms | 160.1 ms | 305.6 ms | 446.0 ms |
| GET 64 KiB x16 | 22 873 obj/s (1 430 MiB/s) | 0.6 ms | 1.1 ms | 2.7 ms | 49.6 ms |

Reading: no regression (the PUT path with new keys releases nothing). Runs vary by roughly ±20% on this machine;
compare trends, not single numbers. Benchmark runs must not fill the disk: `warp.sh` now removes the container's
data volume.

## Step 7e: metrics on the hot path (2026-11-16)

Every S3 request now passes the metrics middleware (a timestamp, a few interlocked adds), and every object write
fires a `bucket_usage` trigger. macOS AOT binary, warp 10 s per workload, before (7d) and after (7e), alternating:

| Workload | before | after | before | after |
|---|---|---|---|---|
| PUT 1 KiB x1 | 175 obj/s | 173 obj/s | 175 obj/s | 174 obj/s |
| PUT 1 KiB x16 | 267 obj/s | 313 obj/s | 275 obj/s | 265 obj/s |
| PUT 64 KiB x16 | 260 obj/s | 256 obj/s | 260 obj/s | 254 obj/s |
| GET 64 KiB x16 | 14 202 obj/s | 14 149 obj/s | 14 426 obj/s | 14 189 obj/s |

Reading: within run-to-run noise (GET −0.4 to −1.6%, the CPU-bound case); no measurable cost.

## Step 5e: Runtime Async and GC mode (2026-11-16)

Four configurations of the same Native AOT build. GC settings are environment variables
(`SERVER_ENV=… deploy/bench/warp.sh …`); Runtime Async off is a separate build (`-p:Features=runtime-async=off`).
"Peak RSS" is the server process, sampled once a second (not `docker stats`, which counts the page cache of the
written blobs). Every run, with p50/p90/p99/max per round: `docs/benchmarks/2026-11-16-step-5e.md`.

| | Configuration |
|---|---|
| A | Defaults: Server GC with DATAS, Runtime Async on |
| B | Server GC without DATAS (`DOTNET_GCDynamicAdaptationMode=0`) |
| C | Workstation GC (`DOTNET_gcServer=0`) |
| D | Runtime Async off (otherwise A) |

**Linux container (4 vCPU), median of 5 rounds** (rounds interleaved, half in reverse order; peak RSS from the
last round)

| Workload | A | B | C | D |
|---|---|---|---|---|
| PUT 1 KiB x16 | 4 657 obj/s | 5 187 obj/s | 4 313 obj/s | 4 762 obj/s |
| PUT 64 KiB x16 | 3 429 obj/s | 3 441 obj/s | 3 281 obj/s | 3 450 obj/s |
| PUT 16 MiB x8 | 47 obj/s | 40 obj/s | 36 obj/s | 38 obj/s |
| GET 64 KiB x16 | 23 512 obj/s | 23 154 obj/s | 22 016 obj/s | 23 176 obj/s |
| Peak RSS | 47–63 MiB | 50–109 MiB | 38–59 MiB | 48–65 MiB |

**macOS (10 cores)**: one round of each configuration, then A and B twice more, alternating

| Workload | A | B | C | D |
|---|---|---|---|---|
| PUT 1 KiB x16 | 269–283 obj/s | 268–329 obj/s | 271 obj/s | 273 obj/s |
| GET 64 KiB x16 | 13 189 obj/s | 12 967 obj/s | 11 330 obj/s | 12 055 obj/s |
| Peak RSS | 47–55 MiB | 134–177 MiB | 42–64 MiB | 42–63 MiB |

Reading: PUT throughput is set by fsync and varies ±20% or more between rounds of the same configuration. Three
rounds (C twice, D once) dropped to ~230 obj/s even for a single client, which no GC setting explains (the same
configurations ran at 715–920 obj/s in other rounds): the VM disk. No configuration is reliably faster on PUT. GET, the CPU-bound workload, is the stable signal: Workstation GC is ~5% slower in every Linux round and on
macOS. Turning DATAS off buys nothing and costs 2–3x the memory (one heap per core: 10 on macOS). Runtime Async on or
off is within noise. **Kept: Server GC with DATAS, Runtime Async on**, now stated in `TimeStore.Server.csproj`
(verified: a build with `GarbageCollectionAdaptationMode=0` shows the B memory profile, 127–173 MiB).

## Step 5d: SigV4 and aws-chunked allocations (2026-11-16)

Micro benchmarks again, with signed chunks added (`STREAMING-AWS4-HMAC-SHA256-PAYLOAD`, what SDKs send over plain
HTTP; 1 MiB in 64 KiB chunks, 17 chunk signatures). Apple M4, .NET 11, in process.

| Benchmark | Mean | Allocated |
|---|---|---|
| SigV4 header verify (GET, AWS example) | 2.25 µs | 4 512 B |
| Canonical query (presigned GET) | 0.83 µs | 3 376 B |
| aws-chunked 1 MiB + CRC32 trailer | 75.8 µs (≈ 13.8 GB/s) | 712 B |
| aws-chunked 1 MiB, signed chunks | 414 µs (≈ 2.5 GB/s) | 20 994 B |
| ListObjectsV2 XML, 1000 keys | 547 µs | 683 020 B |

Reading: no change made. The signature of a request is 2.25 µs; at the measured rate (~5 000 PUT/s on 4 vCPU, fsync-bound) the
whole CPU budget is ~800 µs per PUT, so signing is under 0.3% of it, and its 4.5 KB is ~22 MB/s of short-lived
garbage. Signed chunks cost
1.2 KB of strings per 64 KiB chunk; their time is the SHA-256 of the payload, which the protocol requires. Step 5e
shows the GC is not a limit: halving or multiplying its resources leaves throughput unchanged. Rewriting this code
for zero allocations would not be measurable; the benchmarks stay as the regression baseline. The listing XML
(≈ 680 B per key) remains the largest allocator if listings ever become a hot path.
