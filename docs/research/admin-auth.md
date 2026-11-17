# Research: admin sign-in (end of phase 2)

The admin API (`/api/v1` on the admin port: buckets, access keys, maintenance) has no authentication today. It is
safe only because it binds to loopback by default; anyone who sets `TimeStore:AdminAddress` to a network address
hands out access-key creation to that network. Phase 7 (React UI) needs a sign-in anyway.

Sources: OWASP guidance (Password Storage,
Session Management, Authentication, CSRF cheat sheets), NIST SP 800-63B for password rules, and what Native AOT
allows in ASP.NET Core.

## 2. Guidance

- **Password storage (OWASP)**: Argon2id preferred; where unavailable, **PBKDF2-HMAC-SHA256 with 600 000
  iterations**, a per-user random salt, stored with its parameters so they can be raised later (rehash on login).
- **Password rules (NIST 800-63B)**: length matters, composition rules do not. At least 8 characters (we can ask
  more, e.g. 12), allow at least 64, no forced periodic change, refuse obviously bad ones (equal to the username).
  An upper bound (e.g. 256) keeps the hash cost of a request bounded.
- **Sessions (OWASP)**: a random identifier of at least 128 bits from a CSPRNG, never derived from user data;
  regenerated at login; idle and absolute timeouts; server-side invalidation on logout and on password change;
  cookie `HttpOnly`, `Secure` (when HTTPS), `SameSite`, narrow path.
- **Brute force**: slow down repeated failures (per account and per client) rather than lock the account
  (a lockout lets anyone lock the admin out); same answer and similar timing for unknown users and wrong passwords.
- **CSRF**: with a cookie session, unsafe requests need a second signal. For a same-origin JSON API, OWASP accepts
  `SameSite=Strict` plus checking `Origin` (or `Sec-Fetch-Site`) on state-changing requests; JSON bodies already
  force a CORS preflight on cross-site calls.

## 3. What Native AOT allows

- ASP.NET Core's AOT support covers minimal APIs, JWT bearer authentication, authorization policies, rate limiting
  and static files; **cookie authentication and Identity are not on the supported list** (Identity also needs EF
  Core, which TimeStore does not use).
- Everything needed for our own scheme is AOT-safe BCL: `Rfc2898DeriveBytes.Pbkdf2`, `RandomNumberGenerator`,
  `SHA256`, `CryptographicOperations.FixedTimeEquals`, `System.Threading.RateLimiting`.

## 4. Proposed design

- **Accounts in the metadata database** (`admin_users`: id, username, password hash, role, created/disabled), managed
  through the admin API; not command-line flags (no secret in `ps`, more than two users, password change).
- **Password hash** in `TimeStore.Cryptography`: PBKDF2-HMAC-SHA256, 600 000 iterations, 16-byte salt, 32-byte
  hash, versioned format (`v1$<iterations>$<salt>$<hash>`), constant-time check, rehash when the stored iterations
  are below the current setting.
- **Server-side sessions** (`admin_sessions`): a 256-bit random token in the cookie, only its SHA-256 stored, with
  user, created, last seen and expiry. Lookups are by hash, so a database leak exposes no usable token. Logout,
  password change and user disabling delete the user's sessions. Works for multi-node later (shared database).
  Our own small middleware, no Data Protection and no cookie-authentication handler.
- **Cookie** `ts_session`: `HttpOnly`, `SameSite=Strict`, `Path=/`, `Secure` when the request is HTTPS (or forced by
  configuration behind a TLS proxy).
- **CSRF**: `SameSite=Strict`, and state-changing requests must carry an `Origin` matching the host (or
  `Sec-Fetch-Site: same-origin`); requests without either are refused. Bodies are JSON only.
- **Brute force**: per-username and per-IP failure counters with growing delays (e.g. after 5 failures in
  15 minutes), and a small concurrency limit on password hashing so login floods cannot exhaust the CPU. Unknown
  users still pay the hash cost.
- **Endpoints**: `POST /api/v1/auth/login`, `POST /api/v1/auth/logout`, `GET /api/v1/auth/me`, setup (below),
  `/api/v1/users` (list, create, disable, delete, reset password) and `POST /api/v1/auth/password` (own password).
  Health endpoints stay anonymous; every other admin endpoint requires a session.
- **S3 access keys stay separate** (data plane). Accounts manage them; IAM-style users with S3 permissions are a
  later phase.

Open decisions: how the first admin is created, which roles exist, session lifetimes, and whether the admin port may
leave loopback without HTTPS.

## 5. Decisions (2026-11-16)

1. **Sign in with an access key**, as S3 consoles do: access key id + secret key. The root key pair from
   configuration (`S3:RootAccessKey` / `S3:RootSecretKey`, already required) is always an admin, so a fresh server
   can be administered at once: no setup page, no setup token. This replaces the separate `admin_users` table of
   section 4: one kind of identity, no password hashing (secrets are already encrypted with the master key and
   compared in constant time).
2. **Roles per access key**: `none` (S3 only, the default for every key), `viewer` (admin API read-only), `admin`
   (everything). Keys in the database get a role column; the root key is always `admin`.
3. **Sessions last 24 hours** (absolute), configurable; server-side, revocable.
4. **The admin port may bind to a network address without HTTPS**; the server then logs a warning that secrets
   travel in clear text and recommends a TLS proxy (with `Admin:SecureCookies=true`).

## 6. Plan

- **Schema v5**: `access_keys.role` (`none` default); `admin_sessions (token_hash PK, access_key_id, created_at,
  expires_at)` with an index on `access_key_id`.
- **Sessions**: a 256-bit random token in cookie `ts_session` (`HttpOnly`, `SameSite=Strict`, `Path=/`, `Secure` on
  HTTPS or when configured); the database keeps only its SHA-256. Each request resolves the session and then the
  key's current role (cached), so a role change, a disabled key or a deleted key takes effect at once; those changes
  also delete the key's sessions. Expired sessions are purged by the maintenance worker.
- **Login** (`POST /api/v1/auth/login`): the same answer and work for unknown keys, wrong secrets, disabled keys and
  keys with role `none` (`AUTH_INVALID_CREDENTIALS`, 401). Failures are counted per access key id and per client
  address; after 5 in 15 minutes, further attempts wait (429 with `Retry-After`, doubling).
- **CSRF**: state-changing requests (`POST`, `PUT`, `PATCH`, `DELETE`) need `Origin` equal to the request's scheme
  and host, or `Sec-Fetch-Site: same-origin`; otherwise 403.
- **Authorization**: every `/api/v1` endpoint except `auth/login` requires a session (401 otherwise); `viewer` may
  only read (`GET`), `admin` may do everything (403 otherwise). Health endpoints stay anonymous.
- **Endpoints**: `auth/login`, `auth/logout`, `auth/me`; access keys gain a role on create and
  `POST /access-keys/{id}/role`.
- **Configuration** `TimeStore:Admin`: `SessionHours` (24), `SecureCookies` (false; true behind a TLS proxy).

## 7. Notes from the implementation

- Done as planned: schema v5, `IAuthService` (login, authenticate, logout, purge), `LoginThrottle`,
  `AdminAuthentication` middleware on the admin port, `auth/login|logout|me`, roles on access keys, purge of expired
  sessions in the maintenance worker (when that worker runs; an expired session is also removed the next time it is
  presented).
- Secrets are compared as SHA-256 digests with `FixedTimeEquals` (fixed length, no early exit); an unknown key is
  compared against a random digest, so every failure does the same work.
- `Retry-After` comes from the throttle itself (`IAuthService.RetryAfter`), not from parsing a message.
- The client address is the TCP peer: behind a proxy every caller shares one address for throttling (forwarded
  headers are not trusted by default).
- New error kinds in Primitives: `Unauthorized` (401) and `TooManyRequests` (429).
- The React shell got its sign-in page in step 7a (`docs/research/admin-ui.md`). The Vite proxy keeps
  requests same-origin (`Sec-Fetch-Site: same-origin`), so the CSRF check passes in development.
