# S3 servers compared on a 4-vCPU Linux VPS (2026-11-17)

Medians of 3 runs, 20s per workload. Each server: Docker, 2 CPUs, 3g memory, a
fresh volume per run; warp v1.3.1 with 1.5 CPUs. Docker: Ubuntu 24.04.4 LTS, 4 CPUs, 8127700992 bytes on Linux x86_64.

Machine: a KVM VPS with 4 vCPUs (Intel Haswell, no TSX), 7.6 GB RAM, a virtual disk (about 940 synced 4 KiB writes
per second with `dd oflag=dsync`), Ubuntu 24.04.4, kernel 6.8, Docker Engine 29.10. Nothing else ran on it; CPU steal
averaged 0% (peak 3%). Command:
`SERVER_CPUS=2 SERVER_MEMORY=3g WARP_CPUS=1.5 REPS=3 DURATION=20s deploy/bench/compare.sh`.

The load generator is a limit on this machine: warp (1.5 CPUs) ran at its CPU cap in about three quarters of the
`docker stats` samples, so the gaps between the fastest servers are compressed. Treat differences under 5% as ties.

Cells: throughput, latency p50 / p99, peak resident memory of the server.

| Server | Image | Durability |
|---|---|---|
| timestore | `timestore:bench` | fsync on every write (default) |
| minio | `pgsty/minio:RELEASE.2026-08-04T00-00-00Z` | fsync on every write (default) |
| seaweedfs | `chrislusf/seaweedfs:4.54` | fsync enabled for /buckets/ (weed shell fs.configure -fsync) |
| rustfs | `rustfs/rustfs:1.0.6` | default (no fsync setting found; not verified) |
| garage | `dxflrs/garage:v2.5.0` | data_fsync = true, metadata_fsync = true |

| Workload | timestore | minio | seaweedfs | rustfs | garage |
|---|---|---|---|---|---|
| Idle memory | 39.0 MiB | 131 MiB | 148 MiB | 108 MiB | 27.0 MiB |
| PUT 1 KiB x1 | 199 obj/s<br>p50 5.00 ms · p99 8.40 ms<br>59.0 MiB | 208 obj/s<br>p50 4.60 ms · p99 8.90 ms<br>273 MiB | 119 obj/s<br>p50 8.30 ms · p99 12.7 ms<br>376 MiB | 188 obj/s<br>p50 5.30 ms · p99 9.30 ms<br>125 MiB | 254 obj/s<br>p50 3.50 ms · p99 9.70 ms<br>54.0 MiB |
| PUT 1 KiB x16 | 1,161 obj/s<br>p50 12.5 ms · p99 29.7 ms<br>57.0 MiB | 572 obj/s<br>p50 27.7 ms · p99 71.2 ms<br>332 MiB | 466 obj/s<br>p50 33.9 ms · p99 60.2 ms<br>423 MiB | 1,150 obj/s<br>p50 13.6 ms · p99 23.6 ms<br>150 MiB | 338 obj/s<br>p50 42.1 ms · p99 114 ms<br>80.0 MiB |
| PUT 64 KiB x16 | 984 obj/s<br>p50 15.5 ms · p99 30.5 ms<br>54.0 MiB | 410 obj/s<br>p50 38.7 ms · p99 131 ms<br>488 MiB | 299 obj/s<br>p50 52.5 ms · p99 94.3 ms<br>483 MiB | 802 obj/s<br>p50 18.7 ms · p99 37.7 ms<br>172 MiB | 134 obj/s<br>p50 122 ms · p99 286 ms<br>90.0 MiB |
| PUT 16 MiB x8 | 195 MiB/s<br>p50 663 ms · p99 914 ms<br>63.0 MiB | 180 MiB/s<br>p50 723 ms · p99 1,040 ms<br>419 MiB | 86.2 MiB/s<br>p50 1,480 ms · p99 2,360 ms<br>1,378 MiB | 119 MiB/s<br>p50 1,058 ms · p99 1,535 ms<br>352 MiB | 68.3 MiB/s<br>p50 1,870 ms · p99 2,309 ms<br>114 MiB |
| GET 64 KiB x16 | 4,124 obj/s<br>p50 5.90 ms · p99 39.6 ms<br>65.0 MiB | 1,954 obj/s<br>p50 11.2 ms · p99 29.8 ms<br>535 MiB | 1,926 obj/s<br>p50 8.00 ms · p99 40.5 ms<br>1,234 MiB | 4,217 obj/s<br>p50 4.30 ms · p99 38.1 ms<br>494 MiB | 889 obj/s<br>p50 17.4 ms · p99 35.6 ms<br>97.0 MiB |
| STAT 1 KiB x16 | 6,110 obj/s<br>p50 2.40 ms · p99 18.4 ms<br>75.0 MiB | 2,497 obj/s<br>p50 5.60 ms · p99 19.1 ms<br>444 MiB | 3,954 obj/s<br>p50 3.90 ms · p99 13.1 ms<br>538 MiB | 5,165 obj/s<br>p50 3.30 ms · p99 26.2 ms<br>438 MiB | 4,151 obj/s<br>p50 3.80 ms · p99 12.1 ms<br>134 MiB |
| MIXED 64 KiB x16 | 2,808 obj/s<br>85.0 MiB | 1,261 obj/s<br>567 MiB | 1,070 obj/s<br>554 MiB | 1,819 obj/s<br>412 MiB | 454 obj/s<br>143 MiB |
| DELETE (in MIXED) | 281 obj/s<br>p50 9.00 ms · p99 33.9 ms<br>85.0 MiB | 126 obj/s<br>p50 14.6 ms · p99 40.8 ms<br>567 MiB | 107 obj/s<br>p50 10.1 ms · p99 21.8 ms<br>554 MiB | 182 obj/s<br>p50 32.2 ms · p99 56.2 ms<br>412 MiB | 45.2 obj/s<br>p50 14.9 ms · p99 67.6 ms<br>143 MiB |
