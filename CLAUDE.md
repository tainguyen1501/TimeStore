@AGENTS.md

## Claude Code

- Topic rules load automatically from `.claude/rules/` when you touch matching files. Keep them in sync with
  `AGENTS.md`; if two instructions conflict, fix the files rather than picking one.
- When a decision in a conversation changes a convention, update `AGENTS.md` or the matching rule file in the
  same change, and update `docs/roadmap.md` if it contradicts the new decision.
- Use the `convention-reviewer` subagent (`.claude/agents/`) to check a change against these conventions
  before calling it done.
- The .NET SDK is installed at `~/.dotnet` and may not be on `PATH`; use `~/.dotnet/dotnet` if `dotnet` is missing.
- Before calling backend work done: `dotnet build` with zero warnings, all tests green, and for changes on the
  hot path, the S3 port or packages, a Native AOT publish without warnings plus the end-to-end suite against that
  binary (`TIMESTORE_SERVER_BINARY`, `.claude/rules/aot.md`).
