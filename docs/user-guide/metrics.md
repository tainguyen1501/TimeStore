# Overview and metrics

**Dashboard** shows a card per bucket (objects, size and its share of all stored bytes), **S3 Traffic** (requests by
type since the server started) and **Node Status** (stored bytes, disk, request rate, latency, errors, unfinished
uploads), refreshed every 10 seconds. The right panel's ring shows the storage used per bucket.

**Metrics** starts with four cards, each with its last 15 minutes as a line:

| Card | Meaning |
|---|---|
| Stored | Bytes in all current objects, with the object and bucket counts |
| Disk | Used and free space of the volume holding the data directory |
| S3 requests | Requests per second on the S3 port in the last 10 seconds, and their errors |
| Latency (p99) | 99% of the S3 requests in the last 10 seconds finished within this time (p50: half of them) |

Below them come upload and download rates, client errors (4xx: denied, missing or invalid requests)
and server errors (5xx: should stay at zero), the buckets by size, and totals since the server started: requests by
kind (reads, writes, lists, deletes), bytes, latency and incomplete multipart uploads.

Request numbers live in the server's memory: a restart starts them at zero and the lines fill again over 15 minutes.
Storage numbers are always exact (they are kept with every write).
