"""Cost of a metadata-only backup on a busy server (stage B5b): a check, not a reference. Numbers depend on the machine;
compare the windows of one run (before / during the backup), not runs on different machines.

    uv run --with boto3 --with requests python deploy/bench/metadata_backup.py --binary <TimeStore.Server> \
        --objects 1000000 [--files] [--clients 16] [--duration 60]

Starts a server on a fresh data directory, creates a bucket, stops it and adds --objects synthetic object rows straight
into metadata.db (with --files, a 16-byte blob file for each, so a restore can check them). Then it starts the server
again with Blobs:RetainDeletedDays=1, runs PUT 4 KiB + GET of the same key from --clients threads for --duration
seconds, starts a metadata-only backup a third of the way in, and prints the latency of the requests before and while
the backup runs, the backup's timings, the database and archive sizes and how far the WAL grew. With --files it also
stops the server and times `restore --metadata` (it checks every referenced blob). Nothing is copied from other projects.
"""
import argparse, hashlib, os, signal, socket, sqlite3, statistics, subprocess, sys, tempfile, threading, time, uuid

import boto3, requests
from botocore.auth import S3SigV4Auth
from botocore.awsrequest import AWSRequest
from botocore.config import Config
from botocore.credentials import Credentials

KEY, SECRET, REGION, BUCKET = "bench", "bench-secret-key", "us-east-1", "bench"


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Server:
    def __init__(self, binary, data, retain_days):
        self.binary, self.data, self.retain_days = binary, data, retain_days
        self.s3_port, self.admin_port = free_port(), free_port()
        self.master_key = __import__("base64").b64encode(os.urandom(32)).decode()
        self.process = None

    def start(self):
        env = dict(os.environ, TimeStore__DataPath=self.data, TimeStore__S3Port=str(self.s3_port),
                   TimeStore__AdminPort=str(self.admin_port), TimeStore__MasterKey=self.master_key,
                   TimeStore__S3__RootAccessKey=KEY, TimeStore__S3__RootSecretKey=SECRET,
                   TimeStore__Blobs__RetainDeletedDays=str(self.retain_days), TimeStore__Backup__MinFreeBytes="0",
                   TimeStore__Backup__Keep="10", Logging__LogLevel__Default="Warning")
        self.process = subprocess.Popen([self.binary], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        for _ in range(300):
            try:
                if requests.get(f"http://127.0.0.1:{self.admin_port}/health/live", timeout=1).ok:
                    return
            except requests.RequestException:
                pass
            time.sleep(0.1)
        raise RuntimeError("server did not start")

    def stop(self):
        self.process.send_signal(signal.SIGTERM)
        self.process.wait(timeout=60)

    def s3(self):
        return boto3.client("s3", endpoint_url=f"http://127.0.0.1:{self.s3_port}", aws_access_key_id=KEY,
                            aws_secret_access_key=SECRET, region_name=REGION,
                            config=Config(s3={"addressing_style": "path"}, retries={"max_attempts": 1},
                                          max_pool_connections=4))

    def signed(self, method, path, body=b""):
        url = f"http://127.0.0.1:{self.admin_port}{path}"
        request = AWSRequest(method=method, url=url, data=body,
                             headers={"Content-Type": "application/json"} if body else {})
        S3SigV4Auth(Credentials(KEY, SECRET), "s3", REGION).add_auth(request)
        prepared = request.prepare()
        return requests.request(method, prepared.url, headers=dict(prepared.headers), data=body, timeout=600)


def synthesize(data, objects, files):
    """Adds object rows to the bench bucket (and, with files, a 16-byte blob file for each)."""
    db = sqlite3.connect(os.path.join(data, "metadata.db"))
    bucket_id = db.execute("SELECT id FROM buckets WHERE name = ?", (BUCKET,)).fetchone()[0]
    now = int(time.time() * 1000)
    body = b"0123456789abcdef"
    etag = hashlib.md5(body).hexdigest()
    rows = []
    insert = ("INSERT INTO objects (bucket_id, key, version_id, blob_id, node_id, size, etag, content_type, created_at) "
              "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)")
    for i in range(objects):
        blob = uuid.uuid4().bytes
        if files:
            name = blob.hex()
            directory = os.path.join(data, "blobs", name[-2:])
            os.makedirs(directory, exist_ok=True)
            with open(os.path.join(directory, name), "wb") as f:
                f.write(body)
        rows.append((bucket_id, f"data/{i:08d}.bin", uuid.uuid4().bytes, blob, "node-1", len(body) if files else 1024,
                     etag, "application/octet-stream", now))
        if len(rows) == 100_000:
            db.executemany(insert, rows)
            rows.clear()
    db.executemany(insert, rows)
    db.commit()
    db.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    db.close()


def load(server, stop, samples):
    s3 = server.s3()
    payload = os.urandom(4096)
    while not stop.is_set():
        key = f"load/{uuid.uuid4().hex}"
        for op in ("PUT", "GET"):
            started = time.monotonic()
            try:
                if op == "PUT":
                    s3.put_object(Bucket=BUCKET, Key=key, Body=payload)
                else:
                    s3.get_object(Bucket=BUCKET, Key=key)["Body"].read()
                samples.append((started, (time.monotonic() - started) * 1000, op, True))
            except Exception:
                samples.append((started, (time.monotonic() - started) * 1000, op, False))


def percentile(values, p):
    if not values:
        return float("nan")
    values = sorted(values)
    return values[min(len(values) - 1, int(round(p / 100 * (len(values) - 1))))]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", required=True)
    parser.add_argument("--objects", type=int, default=1_000_000)
    parser.add_argument("--files", action="store_true")
    parser.add_argument("--clients", type=int, default=16)
    parser.add_argument("--duration", type=float, default=60)
    parser.add_argument("--keep", action="store_true", help="keep the work directory (deleted by default)")
    args = parser.parse_args()

    work = tempfile.mkdtemp(prefix="timestore-bench-")
    data = os.path.join(work, "data")
    server = Server(args.binary, data, retain_days=1)
    server.start()
    server.s3().create_bucket(Bucket=BUCKET)
    server.stop()
    t = time.monotonic()
    synthesize(data, args.objects, args.files)
    print(f"synthesized {args.objects} objects{' with blob files' if args.files else ''} in {time.monotonic() - t:.1f} s")
    database = os.path.join(data, "metadata.db")
    db_bytes = os.path.getsize(database)

    server.start()
    stop, samples, wal = threading.Event(), [], []
    threads = [threading.Thread(target=load, args=(server, stop, samples), daemon=True) for _ in range(args.clients)]
    begin = time.monotonic()
    for thread in threads:
        thread.start()

    def watch_wal():
        while not stop.is_set():
            path = database + "-wal"
            wal.append(os.path.getsize(path) if os.path.exists(path) else 0)
            time.sleep(0.1)

    threading.Thread(target=watch_wal, daemon=True).start()
    time.sleep(args.duration / 3)
    backup_start = time.monotonic()
    run = server.signed("POST", "/api/v1/backups", b'{"content":"metadata"}').json()
    while run["status"] in ("queued", "running"):
        time.sleep(0.05)
        run = server.signed("GET", f"/api/v1/backups/{run['id']}").json()
    backup_end = time.monotonic()
    time.sleep(max(0, begin + args.duration - time.monotonic()))
    stop.set()
    for thread in threads:
        thread.join()
    server.stop()

    def window(lo, hi, op=None):
        return [s for s in samples if lo <= s[0] < hi and (op is None or s[2] == op)]

    print(f"\nbackup: {run['status']}, {run.get('archiveName')}, archive {run.get('archiveSize')} bytes, "
          f"database {db_bytes} bytes ({db_bytes / args.objects:.0f} bytes per object)")
    parse = lambda iso: __import__("datetime").datetime.fromisoformat(iso.replace("Z", "+00:00")).timestamp()
    snapshot_s = parse(run["snapshotAt"]) - parse(run["startedAt"])
    total_s = parse(run["finishedAt"]) - parse(run["startedAt"])
    print(f"snapshot (VACUUM INTO) {snapshot_s:.2f} s, whole run {total_s:.2f} s, seen by the client "
          f"{backup_end - backup_start:.2f} s; WAL peak {max(wal) / 1e6:.1f} MB")
    print("\n| Window | Requests/s | PUT p50 | PUT p99 | GET p50 | GET p99 | Max | Errors |")
    print("|---|---|---|---|---|---|---|---|")
    for name, lo, hi in (("Before the backup", begin + 2, backup_start),
                         ("During the backup", backup_start, backup_end),
                         ("After the backup", backup_end, begin + args.duration)):
        rows = window(lo, hi)
        puts = [s[1] for s in rows if s[2] == "PUT" and s[3]]
        gets = [s[1] for s in rows if s[2] == "GET" and s[3]]
        errors = sum(1 for s in rows if not s[3])
        rate = len(rows) / max(hi - lo, 1e-9)
        print(f"| {name} ({hi - lo:.1f} s) | {rate:.0f} | {percentile(puts, 50):.1f} ms | {percentile(puts, 99):.1f} ms | "
              f"{percentile(gets, 50):.1f} ms | {percentile(gets, 99):.1f} ms | "
              f"{max([s[1] for s in rows], default=float('nan')):.0f} ms | {errors} |")

    if args.files:
        archive = os.path.join(data, "backups", run["archiveName"])
        t = time.monotonic()
        result = subprocess.run([args.binary, "restore", "--metadata", "--archive", archive, "--data-path", data],
                                capture_output=True, text=True)
        print(f"\nrestore --metadata: exit {result.returncode} in {time.monotonic() - t:.2f} s")
        print(result.stdout.strip() or result.stderr.strip())
    if args.keep:
        print(f"\nwork directory: {work}")
    else:
        __import__("shutil").rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
