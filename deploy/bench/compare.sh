#!/usr/bin/env bash
# Head-to-head S3 benchmark: TimeStore against other self-hosted S3 servers, all in Docker, same limits, same warp
# workloads (docs/research/performance.md, "Comparing with other servers").
#
#   deploy/bench/compare.sh                         # every server, REPS runs each, Markdown to stdout
#   SERVERS="timestore minio" REPS=1 DURATION=10s deploy/bench/compare.sh
#   OUT=docs/benchmarks/2026-11-17-compare.md deploy/bench/compare.sh
#
# Needs Docker (give it at least 8 CPUs / 12 GB) and python3 (medians). Build the TimeStore image first:
#   docker build -f deploy/Dockerfile -t timestore:bench .
#
# Fairness: one server at a time; each server container gets SERVER_CPUS / SERVER_MEMORY and a fresh volume per run;
# warp runs in its own container (WARP_CPUS). Durability is set to "every write is on disk before it is
# acknowledged" wherever the server allows it (see durability_of below). Third-party servers are run as black boxes:
# their code is never read or copied. On macOS Docker runs in a VM: compare servers with each other, not with
# numbers taken on Linux.
set -euo pipefail

SERVERS="${SERVERS:-timestore minio seaweedfs rustfs garage}"
REPS="${REPS:-3}"
DURATION="${DURATION:-20s}"
SERVER_CPUS="${SERVER_CPUS:-4}"
SERVER_MEMORY="${SERVER_MEMORY:-4g}"
WARP_CPUS="${WARP_CPUS:-3}"
OUT="${OUT:-}"

# Pinned images (2026-11-17). MinIO: the official images and downloads are gone since the community edition was
# archived; pgsty/minio is the community build of the same source (AGPL).
TIMESTORE_IMAGE="${TIMESTORE_IMAGE:-timestore:bench}"
MINIO_IMAGE="pgsty/minio:RELEASE.2026-08-04T00-00-00Z"
SEAWEEDFS_IMAGE="chrislusf/seaweedfs:4.54"
RUSTFS_IMAGE="rustfs/rustfs:1.0.6"
GARAGE_IMAGE="dxflrs/garage:v2.5.0"
WARP_IMAGE="minio/warp:v1.3.1"

# Garage key ids are "GK" + 24 hex characters, secrets 64 hex characters; the other servers take any value.
ACCESS_KEY="GK0123456789abcdef01234567"
SECRET_KEY="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
REGION="us-east-1"

NETWORK="s3bench"
SERVER="s3bench-server"
VOLUME="s3bench-data"

# name | warp operation | object size | clients | extra warp flags
# Deletes are measured inside MIXED (45% GET, 15% PUT, 10% DELETE, 30% STAT): warp's own DELETE benchmark deletes a
# fixed set of prepared objects, which fast servers finish in under a second ("too few samples").
WORKLOADS=(
  "PUT 1 KiB x1|put|1KiB|1|"
  "PUT 1 KiB x16|put|1KiB|16|"
  "PUT 64 KiB x16|put|64KiB|16|"
  "PUT 16 MiB x8|put|16MiB|8|"
  "GET 64 KiB x16|get|64KiB|16|"
  "STAT 1 KiB x16|stat|1KiB|16|"
  "MIXED 64 KiB x16|mixed|64KiB|16|"
)

# Lookups as functions: macOS ships bash 3.2 (no associative arrays).
durability_of() {
  case "$1" in
    timestore|minio) echo "fsync on every write (default)" ;;
    seaweedfs) echo "fsync enabled for /buckets/ (weed shell fs.configure -fsync)" ;;
    rustfs) echo "default (no fsync setting found; not verified)" ;;
    garage) echo "data_fsync = true, metadata_fsync = true" ;;
  esac
}
image_of() {
  case "$1" in
    timestore) echo "$TIMESTORE_IMAGE" ;; minio) echo "$MINIO_IMAGE" ;; seaweedfs) echo "$SEAWEEDFS_IMAGE" ;;
    rustfs) echo "$RUSTFS_IMAGE" ;; garage) echo "$GARAGE_IMAGE" ;;
  esac
}

work="$(mktemp -d)"
results="$work/results.csv"
: >"$results"
sampler_pid=""

remove_server() {
  docker rm -f -v "$SERVER" >/dev/null 2>&1 || true
  docker volume rm -f "$VOLUME" >/dev/null 2>&1 || true
}
cleanup() {
  [[ -n "$sampler_pid" ]] && kill "$sampler_pid" 2>/dev/null || true
  remove_server
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

docker network create "$NETWORK" >/dev/null 2>&1 || true

# Starts one server on a fresh volume; sets PORT (its S3 port inside the network, also published on loopback for the
# readiness check).
start_server() {
  remove_server
  case "$1" in timestore|minio|rustfs) PORT=9000 ;; seaweedfs) PORT=8333 ;; garage) PORT=3900 ;; esac
  limits=(--network "$NETWORK" --cpus "$SERVER_CPUS" --memory "$SERVER_MEMORY" -v "$VOLUME:/data" -p "127.0.0.1::$PORT")
  case "$1" in
    timestore)
      docker run -d --name "$SERVER" "${limits[@]}" \
        -e TimeStore__S3__RootAccessKey=$ACCESS_KEY -e TimeStore__S3__RootSecretKey=$SECRET_KEY \
        -e TimeStore__S3__Region=$REGION -e TimeStore__DataPath=/data \
        -e TimeStore__BlobSweep__Enabled=false -e TimeStore__Multipart__AbandonAfterDays=0 \
        -e Logging__LogLevel__Default=Warning "$TIMESTORE_IMAGE" >/dev/null
      ;;
    minio)
      docker run -d --name "$SERVER" "${limits[@]}" \
        -e MINIO_ROOT_USER=$ACCESS_KEY -e MINIO_ROOT_PASSWORD=$SECRET_KEY -e MINIO_REGION=$REGION \
        "$MINIO_IMAGE" server /data >/dev/null
      ;;
    seaweedfs)
      cat >"$work/s3.json" <<EOF
{ "identities": [ { "name": "bench", "credentials": [ { "accessKey": "$ACCESS_KEY", "secretKey": "$SECRET_KEY" } ],
  "actions": [ "Admin", "Read", "Write", "List", "Tagging" ] } ] }
EOF
      docker run -d --name "$SERVER" "${limits[@]}" -v "$work/s3.json:/etc/s3.json:ro" \
        "$SEAWEEDFS_IMAGE" server -dir=/data -s3 -s3.port=$PORT -s3.config=/etc/s3.json \
        -master.volumeSizeLimitMB=256 -volume.max=500 >/dev/null
      # Every bucket gets 7 volumes; the default cap (8 volumes of 30 GB) and the automatic one (free disk / volume
      # size) are reached after a few buckets, then uploads fail. Volumes are not preallocated: a high cap costs nothing.
      ;;
    rustfs)
      docker run -d --name "$SERVER" "${limits[@]}" \
        -e RUSTFS_ACCESS_KEY=$ACCESS_KEY -e RUSTFS_SECRET_KEY=$SECRET_KEY -e RUSTFS_VOLUMES=/data \
        "$RUSTFS_IMAGE" /data >/dev/null
      ;;
    garage)
      cat >"$work/garage.toml" <<EOF
metadata_dir = "/data/meta"
data_dir = "/data/data"
db_engine = "lmdb"
metadata_fsync = true
data_fsync = true
replication_factor = 1
rpc_bind_addr = "[::]:3901"
rpc_public_addr = "127.0.0.1:3901"
rpc_secret = "$(printf '%064d' 7)"
[s3_api]
s3_region = "$REGION"
api_bind_addr = "[::]:3900"
root_domain = ".s3.garage"
EOF
      docker run -d --name "$SERVER" "${limits[@]}" -v "$work/garage.toml:/etc/garage.toml:ro" \
        "$GARAGE_IMAGE" >/dev/null
      ;;
    *) echo "unknown server: $1" >&2; exit 2 ;;
  esac
  wait_for_s3
  setup_server "$1"
}

# Any HTTP answer on the S3 port means the server is up.
wait_for_s3() {
  local published
  for _ in $(seq 1 120); do
    published=$(docker port "$SERVER" "$PORT" 2>/dev/null | head -1)
    if [[ -n "$published" ]] && [[ "$(curl -s -o /dev/null -w '%{http_code}' "http://$published/")" != "000" ]]; then
      return 0
    fi
    sleep 1
  done
  echo "server did not start:" >&2
  docker logs "$SERVER" 2>&1 | tail -30 >&2
  exit 1
}

setup_server() {
  case "$1" in
    seaweedfs)
      echo "fs.configure -locationPrefix=/buckets/ -fsync -apply" | docker exec -i "$SERVER" weed shell >/dev/null
      ;;
    garage)
      node=$(docker exec "$SERVER" /garage node id -q 2>/dev/null | cut -d@ -f1)
      docker exec "$SERVER" /garage layout assign -z dc1 -c 10G "$node" >/dev/null 2>&1
      docker exec "$SERVER" /garage layout apply --version 1 >/dev/null 2>&1
      docker exec "$SERVER" /garage key import --yes -n bench "$ACCESS_KEY" "$SECRET_KEY" >/dev/null 2>&1
      docker exec "$SERVER" /garage key allow --create-bucket "$ACCESS_KEY" >/dev/null 2>&1
      ;;
  esac
}

# Resident memory of the server's processes in MiB (not `docker stats`: that includes the page cache of the data it
# wrote, which says nothing about the server).
memory_mib() {
  docker top "$SERVER" -o pid,rss 2>/dev/null | tail -n +2 | awk '{ sum += $2 } END { printf "%d", sum / 1024 }'
}

start_sampler() {
  echo 0 >"$work/peak"
  ( while true; do
      m=$(memory_mib || echo 0)
      [[ "${m:-0}" -gt "$(cat "$work/peak")" ]] && echo "$m" >"$work/peak"
      sleep 1
    done ) &
  sampler_pid=$!
}
stop_sampler() { kill "$sampler_pid" 2>/dev/null || true; wait "$sampler_pid" 2>/dev/null || true; sampler_pid=""; }

# The lines of one "Report: <op>" section of warp's output (mixed runs print one per operation and a total).
section() { awk -v op="$1" '/^Report: /{ on = ($2 == op ".") } on'; }

# Appends one CSV row from a warp report: "Average: 0.25 MiB/s, 256.12 obj/s" and
# "Reqs: Avg: …, 50%: 1.2ms, 90%: …, 99%: …, Fastest: …, Slowest: …" (the total of a mixed run has no Reqs line).
record() {
  local name="$1" rep="$2" label="$3" out="$4" avg reqs objs mibs
  avg=$(grep -m1 "Average:" <<<"$out" || true)
  reqs=$(grep -m1 "Reqs:" <<<"$out" || true)
  objs=$(sed -nE 's/.* ([0-9.]+) obj\/s.*/\1/p' <<<"$avg"); mibs=$(sed -nE 's/.*Average: ([0-9.]+) MiB\/s.*/\1/p' <<<"$avg")
  pick() { sed -nE "s/.*$1: ([0-9.]+)(µs|ms|s).*/\1 \2/p" <<<"$reqs" | awk '{ v=$1; if ($2=="µs") v/=1000; if ($2=="s") v*=1000; printf "%.2f", v }'; }
  if [[ -z "$objs" ]]; then
    echo "  $name run $rep, $label: no result" >&2
    grep -iE "error|fail|skipping" <<<"$out" | head -3 >&2 || true
  fi
  echo "$name,$rep,$label,${objs:-0},${mibs:-0},$(pick 50%),$(pick 90%),$(pick 99%),$(pick Slowest),$(cat "$work/peak")" >>"$results"
  echo "  $name run $rep, $label: ${objs:-0} obj/s" >&2
}

run_workloads() {
  local name="$1" rep="$2"
  echo "$name,$rep,idle,0,0,0,0,0,0,$(memory_mib)" >>"$results"
  for workload in "${WORKLOADS[@]}"; do
    IFS='|' read -r label op size clients extra <<<"$workload"
    read -r -a extra_args <<<"${extra:-}"
    bucket="bench-$op-$(echo "$size$clients" | tr 'A-Z' 'a-z' | tr -dc 'a-z0-9')"
    start_sampler
    out=$(docker run --rm --network "$NETWORK" --cpus "$WARP_CPUS" \
      -e WARP_ACCESS_KEY=$ACCESS_KEY -e WARP_SECRET_KEY=$SECRET_KEY "$WARP_IMAGE" \
      "$op" --host "$SERVER:$PORT" --obj.size "$size" --concurrent "$clients" --duration "$DURATION" \
      --bucket "$bucket" --region "$REGION" --noclear ${extra_args[@]+"${extra_args[@]}"} 2>&1) || true
    stop_sampler
    if [[ "$op" == mixed ]]; then
      record "$name" "$rep" "$label" "$(section Total <<<"$out")"
      record "$name" "$rep" "DELETE (in MIXED)" "$(section DELETE <<<"$out")"
    else
      record "$name" "$rep" "$label" "$out"
    fi
  done
}

for name in $SERVERS; do
  for rep in $(seq 1 "$REPS"); do
    echo "$name, run $rep of $REPS" >&2
    start_server "$name"
    run_workloads "$name" "$rep"
    remove_server
  done
done

# Medians per server and workload, as Markdown.
report=$(python3 - "$results" "$SERVERS" <<'PY'
import csv, statistics, sys
rows = list(csv.reader(open(sys.argv[1])))
servers = sys.argv[2].split()
by = {}
for r in rows:
    by.setdefault((r[0], r[2]), []).append(r)
def med(vals):
    vals = [float(v) for v in vals if v not in ("", None)]
    return statistics.median(vals) if vals else None
def fmt(v, unit=""):
    return "—" if v is None else (f"{v:,.0f}{unit}" if v >= 100 else f"{v:,.1f}{unit}" if v >= 10 else f"{v:,.2f}{unit}")
workloads = [w for w in dict.fromkeys(r[2] for r in rows) if w != "idle"]
print("| Workload | " + " | ".join(servers) + " |")
print("|---|" + "---|" * len(servers))
print("| Idle memory | " + " | ".join(fmt(med([r[9] for r in by.get((s, "idle"), [])]), " MiB") for s in servers) + " |")
for w in workloads:
    cells = []
    for s in servers:
        rs = by.get((s, w), [])
        obj, mib, p50, p99, peak = (med([r[i] for r in rs]) for i in (3, 4, 5, 7, 9))
        if not obj:
            cells.append("failed")
            continue
        rate = f"{fmt(mib)} MiB/s" if w.startswith("PUT 16") else f"{fmt(obj)} obj/s"
        latency = f"p50 {fmt(p50)} ms · p99 {fmt(p99)} ms<br>" if p50 is not None else ""
        cells.append(f"{rate}<br>{latency}{fmt(peak)} MiB")
    print(f"| {w} | " + " | ".join(cells) + " |")
PY
)

header="# S3 servers compared ($(date +%Y-%m-%d))

Medians of $REPS runs, $DURATION per workload. Each server: Docker, ${SERVER_CPUS} CPUs, ${SERVER_MEMORY} memory, a
fresh volume per run; warp ${WARP_IMAGE##*:} with ${WARP_CPUS} CPUs. Docker: $(docker info --format '{{.OperatingSystem}}, {{.NCPU}} CPUs, {{.MemTotal}} bytes') on $(uname -sm).
Cells: throughput, latency p50 / p99, peak resident memory of the server.

| Server | Image | Durability |
|---|---|---|"
for name in $SERVERS; do header+=$'\n'"| $name | \`$(image_of "$name")\` | $(durability_of "$name") |"; done

{ echo "$header"; echo; echo "$report"; } | tee ${OUT:+"$OUT"}
