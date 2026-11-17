# Contributing to TimeStore

Thanks for your interest in TimeStore. Bug reports, compatibility reports from real S3 clients, documentation fixes
and code are all welcome.

## Before you start

- **Bugs and compatibility problems:** open an issue with the client and its version, the request (or the command
  you ran), what you expected and what TimeStore answered. Server logs help; remove secrets first.
- **Security problems:** do not open a public issue; follow `SECURITY.md`.
- **New features or larger changes:** open an issue first so we can agree on the design before you write code. Every
  feature starts as a short design note in `docs/research/` and a step in `docs/roadmap.md`.
- **Small fixes** (typos, docs, an obvious bug): a pull request is fine without an issue.

## Contributor License Agreement

TimeStore is licensed under the AGPL-3.0, and the maintainers also offer it under a commercial license. To keep both
possible, every contributor signs the [Contributor License Agreement](CLA.md) once before their first pull request
is merged. You keep the copyright of your work; the agreement gives the project the right to distribute it under the
AGPL-3.0 and under other licenses. A bot asks you to sign in the pull request; it takes one comment.

## Development setup

Requirements: the .NET SDK pinned in `backend/global.json`, Node.js 22.13 or later with pnpm (version in
`frontend/package.json`), and Docker for the container build and the compatibility tests.

```bash
# Backend: S3 on :9000, admin API + UI on :9001, data in ./data, development key pair
cd backend
dotnet build TimeStore.slnx
dotnet run --project Src/TimeStore.Server

# Admin UI with hot reload (proxies the admin API on :9001)
cd frontend
pnpm install
pnpm dev
```

## Checks before a pull request

```bash
cd backend
dotnet build TimeStore.slnx          # no warnings: warnings are errors
dotnet test --solution TimeStore.slnx

cd frontend
pnpm lint && pnpm typecheck && pnpm build
```

Also run these when your change touches them:

- **S3 behaviour:** the compatibility tests with real clients
  (`TIMESTORE_COMPAT=1 dotnet test --project Tests/TimeStore.Compat.Tests`, needs Docker and uv).
- **Native AOT:** `dotnet publish Src/TimeStore.Server -c Release -r <rid>` must succeed without trimming or AOT
  warnings; TimeStore ships as a Native AOT binary only.
- **Hot path (PUT, GET, LIST):** compare against the baselines in `docs/performance.md`; a slower hot path needs a
  reason in the pull request.

## Conventions

The project conventions (layers, dependency rules, error handling, naming, security rules) are in `AGENTS.md` and
`.claude/rules/`. They are written for coding agents and humans alike. The most important ones:

- **The Amazon S3 documentation is the specification.** Behaviour that differs from Amazon S3 is a bug unless a
  research note says why.
- **Write the code yourself.** Do not copy or translate code from other projects, and do not paste code whose license
  you cannot grant under the CLA.
- **English** for code, comments, identifiers, docs and commit messages.
- **Never log secrets** (secret keys, session tokens, signatures).
- A user-facing change updates `docs/user-guide/`; a change to the admin API updates `docs/api/` and the generated
  types.

## Pull requests

- One topic per pull request; keep unrelated refactoring out.
- Add or update tests for the behaviour you change.
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`,
  `perf:`, `test:`, `refactor:`, `chore:`), with a scope when it helps (`fix(multipart): ...`).
- Describe what changed, why, and how you tested it. Link the issue if there is one.
- Add a line to the `Unreleased` section of `CHANGELOG.md` for user-visible changes.

## Releases

Releases are made by the maintainers. A **git tag is the only source of the version**: nothing in the repository is
edited to change it. Local builds report `0.0.0-dev` (`backend/Directory.Build.props`); the release build passes the
tag's version (`-p:Version=X.Y.Z`).

**Tag format:** `v` + [Semantic Versioning](https://semver.org) `MAJOR.MINOR.PATCH`, optionally with a pre-release
suffix.

| Tag | Valid | Meaning |
|---|---|---|
| `v0.2.0` | ✅ | New features; before 1.0 a minor version may also contain breaking changes |
| `v0.2.1` | ✅ | Fixes only |
| `v1.0.0` | ✅ | Stable: API, configuration and data format stay compatible within 1.x |
| `v0.3.0-rc.1`, `v0.3.0-beta.1` | ✅ | Pre-release (marked as such on GitHub) |
| `0.2.0`, `v0.2`, `v2`, `release-2` | ❌ | Rejected: missing `v`, or not three numbers |

Which number to raise: a fix only → PATCH; a new feature or anything a user must act on when upgrading → MINOR
(before 1.0) or MAJOR (after 1.0).

**Steps:**

1. In `CHANGELOG.md`, rename `[Unreleased]` to `[X.Y.Z] - YYYY-MM-DD` and start a new empty `[Unreleased]` above it.
2. Commit: `chore(release): X.Y.Z`.
3. Tag that commit `vX.Y.Z` and push the tag (or create the tag in GitHub: Releases → Draft a new release).
4. The release workflow builds the binaries and the container image with that version and publishes the GitHub
   Release with the changelog section as its notes. A tag in the wrong format fails the workflow and publishes
   nothing.

A published tag is never moved or reused: a mistake gets a new PATCH version.

## License

By contributing you agree that your contribution is licensed under the AGPL-3.0 and covered by the [CLA](CLA.md).
