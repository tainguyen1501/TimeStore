# Security policy

TimeStore stores other people's data and holds their credentials, so security reports get priority over other work.
Thank you for reporting problems responsibly.

## Supported versions

TimeStore is before 1.0. Security fixes go into the latest release and `main`; older releases are not patched.
Upgrade to the latest release before reporting, if you can.

| Version | Supported |
|---|---|
| Latest release, `main` | ✅ |
| Older releases | ❌ |

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a security problem.**

Report it privately through GitHub: open the repository's **Security** tab and choose **Report a vulnerability**
(https://github.com/tainguyen1501/TimeStore/security/advisories/new). Only the maintainers see the report.

Please include:

- the TimeStore version or commit, and how it runs (binary, container, operating system);
- the affected part (S3 API, admin API or UI, storage) and the configuration that matters;
- steps or a small script to reproduce, and what an attacker gains;
- whether the problem is already known to others or public.

Never include real credentials or other people's data. Test against your own installation only.

## What happens next

- We acknowledge the report within **7 days**.
- We confirm or reject it, and agree on a severity with you, as soon as we can reproduce it.
- We fix confirmed problems in a private fork, release the fix, and then publish a GitHub security advisory (with a
  CVE when it applies). We aim to release a fix within **90 days** of the report; we tell you if it takes longer.
- You are credited in the advisory, unless you prefer not to be.

Please keep the problem private until the advisory is published, or until 90 days have passed, whichever comes first.

## Scope

In scope: anything that lets someone read, change or delete data, or use the server, beyond what their credentials
and bucket policies allow. For example:

- bypassing SigV4 authentication, presigned URL checks or bucket policies;
- signing in to the admin API or UI without valid credentials, or getting more than the key's role allows;
- exposing secrets (secret keys, session tokens, signatures) in responses, logs or stored files;
- reaching files outside the data directory, or one object's data through another;
- a single request that crashes the server or makes it allocate far beyond the request's size.

Out of scope:

- deployments that ignore the documented setup, such as the admin port on a public address without a TLS proxy
  (`AdminAddress`, `Admin:SecureCookies` in `docs/configuration.md`) or the development key pair in production;
- denial of service through request volume alone (put a rate limiter or proxy in front);
- missing hardening headers or best practices without a concrete attack;
- vulnerabilities in dependencies that TimeStore does not use in an exploitable way (report those upstream).

## Safe harbor

We will not take legal action against research done in good faith under this policy: testing on your own
installation, not accessing or changing other people's data, not degrading service for others, and reporting
privately. If in doubt, ask first through a private report.
