#!/usr/bin/env bash
# End-to-end baseline with warp (MinIO's S3 benchmark, AGPL: executed as a load generator, never copied).
#
#   deploy/bench/warp.sh host <path to the TimeStore.Server binary>   # the binary on this machine
#   deploy/bench/warp.sh docker [image]                               # the Linux image (deploy/Dockerfile)
#
#   SERVER_ENV="DOTNET_gcServer=0 DOTNET_GCDynamicAdaptationMode=0" deploy/bench/warp.sh …   # runtime knobs for the server
#
# Each run starts a server on a fresh data directory, runs the fixed workloads below and prints a Markdown table
# (record it in docs/performance.md). Needs Docker. Durations are short on purpose: compare runs, not absolutes.
set -euo pipefail

WARP_IMAGE="minio/warp:v1.3.1"   # newest published image (2026-11-16)
DURATION="${DURATION:-15s}"
ACCESS_KEY="warp-bench"
SECRET_KEY="warp-bench-secret-key"
TARGET="${1:?usage: warp.sh host <binary> | docker [image]}"
read -r -a server_env <<<"${SERVER_ENV:-}"   # KEY=VALUE pairs passed to the server process

# name | warp operation | object size | clients
WORKLOADS=(
  "PUT 1 KiB x1|put|1KiB|1"
  "PUT 1 KiB x16|put|1KiB|16"
  "PUT 64 KiB x16|put|64KiB|16"
  "PUT 16 MiB x8|put|16MiB|8"
  "GET 64 KiB x16|get|64KiB|16"
)

work="$(mktemp -d)"
server_pid=""
sampler_pid=""
cleanup() {
  [[ -n "$sampler_pid" ]] && kill "$sampler_pid" 2>/dev/null || true
  [[ -n "$server_pid" ]] && kill "$server_pid" 2>/dev/null || true
  docker rm -f -v timestore-bench >/dev/null 2>&1 || true # -v: the image declares VOLUME /data
  docker network rm timestore-bench >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

network_args=()
case "$TARGET" in
  host)
    binary="${2:?usage: warp.sh host <path to TimeStore.Server>}"
    port=$((20000 + RANDOM % 20000))
    env ${server_env[@]+"${server_env[@]}"} TimeStore__S3Port=$port TimeStore__AdminPort=$((port + 1)) TimeStore__DataPath="$work/data" \
      TimeStore__S3__RootAccessKey=$ACCESS_KEY TimeStore__S3__RootSecretKey=$SECRET_KEY \
      TimeStore__BlobSweep__Enabled=false TimeStore__Multipart__AbandonAfterDays=0 Logging__LogLevel__Default=Warning \
      "$binary" >"$work/server.log" 2>&1 &
    server_pid=$!
    endpoint="host.docker.internal:$port"
    network_args=(--add-host=host.docker.internal:host-gateway)
    health="http://127.0.0.1:$((port + 1))/health/live"
    ;;
  docker)
    image="${2:-timestore:bench}"
    docker network create timestore-bench >/dev/null
    docker_env=()
    for pair in ${server_env[@]+"${server_env[@]}"}; do docker_env+=(-e "$pair"); done
    docker run -d --name timestore-bench --network timestore-bench -p 127.0.0.1::9001 \
      -e TimeStore__S3__RootAccessKey=$ACCESS_KEY -e TimeStore__S3__RootSecretKey=$SECRET_KEY \
      -e TimeStore__BlobSweep__Enabled=false -e TimeStore__Multipart__AbandonAfterDays=0 \
      -e Logging__LogLevel__Default=Warning ${docker_env[@]+"${docker_env[@]}"} "$image" >/dev/null
    endpoint="timestore-bench:9000"
    network_args=(--network timestore-bench)
    health="http://$(docker port timestore-bench 9001 | head -1)/health/live"
    ;;
  *) echo "unknown target: $TARGET" >&2; exit 2 ;;
esac

for _ in $(seq 1 50); do curl -fs "$health" >/dev/null 2>&1 && break; sleep 0.2; done
curl -fs "$health" >/dev/null || { echo "server did not start"; cat "$work/server.log" 2>/dev/null; exit 1; }

# Resident set size of the server process in MiB. Not `docker stats`: a container's memory usage includes the page
# cache of the blobs it wrote, which says nothing about the server.
memory_mib() {
  case "$TARGET" in
    host) ps -o pid=,rss= -p "$server_pid" ;;
    docker) docker top timestore-bench -o pid,rss | tail -n +2 ;; # docker top needs the pid column
  esac | awk '{ printf "%d", $2 / 1024 }'
}

# Samples memory once a second while a workload runs and keeps the highest value in $work/peak.
start_sampler() {
  echo 0 >"$work/peak"
  ( while true; do
      m=$(memory_mib 2>/dev/null || echo 0)
      [[ "${m:-0}" -gt "$(cat "$work/peak")" ]] && echo "$m" >"$work/peak"
      sleep 1
    done ) &
  sampler_pid=$!
}

stop_sampler() { kill "$sampler_pid" 2>/dev/null || true; wait "$sampler_pid" 2>/dev/null || true; sampler_pid=""; }

echo "Idle memory: $(memory_mib) MiB${SERVER_ENV:+ (SERVER_ENV=$SERVER_ENV)}"
echo
echo "| Workload | Throughput | p50 | p90 | p99 | max | peak memory |"
echo "|---|---|---|---|---|---|---|"
for workload in "${WORKLOADS[@]}"; do
  IFS='|' read -r name op size clients <<<"$workload"
  start_sampler
  out=$(docker run --rm "${network_args[@]}" -e WARP_ACCESS_KEY=$ACCESS_KEY -e WARP_SECRET_KEY=$SECRET_KEY "$WARP_IMAGE" \
    "$op" --host "$endpoint" --obj.size "$size" --concurrent "$clients" --duration "$DURATION" \
    --bucket "bench-$op-$(echo "$size$clients" | tr -dc 'a-z0-9' | tr 'A-Z' 'a-z')" --region us-east-1 --noclear 2>&1)
  stop_sampler
  average=$(grep -m1 "Average:" <<<"$out" | sed -E 's/.*Average: ([^,]+), ([0-9.]+) obj\/s.*/\2 obj\/s (\1)/')
  reqs=$(grep -m1 "Reqs:" <<<"$out")
  pick() { sed -E "s/.*$1: ([0-9.]+ms).*/\1/" <<<"$reqs"; }
  echo "| $name | $average | $(pick 50%) | $(pick 90%) | $(pick 99%) | $(pick Slowest) | $(cat "$work/peak") MiB |"
done
