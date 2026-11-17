---
paths:
  - "backend/Src/TimeStore.Metadata.*/**"
  - "backend/Tests/TimeStore.Metadata.ContractTests/**"
---

# Metadata provider rules

## Contract first

- `IMetadataStore` (Domain) is the contract; `MetadataStoreContract` pins its behaviour. New behaviour = a new fact
  in the contract, then the implementation in every provider. A provider-specific test is only for internals.
- Providers reference only Domain + Primitives and are registered only by the Server.

## SQLite (current provider)

- Raw ADO.NET (`Microsoft.Data.Sqlite`); SQL lives in `const string` fields next to the code that runs it.
- Parameters are typed explicitly (`AddId`, `AddText`, `AddInt64` in `SqliteParameters`; never `AddWithValue`).
  **Guids are 16-byte big-endian BLOBs** (`AddId` / `GetId`): Microsoft.Data.Sqlite binds `Guid` as TEXT by default
  (STRICT BLOB columns reject it), and `Guid.ToByteArray()` is mixed-endian, which would break UUIDv7 ordering.
- Timestamps: INTEGER unix milliseconds UTC (`SqliteTime`); domain values are already truncated to ms.
- Tables are `STRICT`, snake_case; keys and names use `COLLATE BINARY` (S3 binary order).
- Pragmas: `journal_mode=WAL` (once, at init), per connection `foreign_keys=ON`, `synchronous=FULL`,
  `busy_timeout=5000`.
- Migrations: `Schema/SqliteMigrations.All` is append-only; each runs in its own transaction and is recorded in
  `schema_migrations`. Never edit a released migration.
- Single writer: every write goes through `SqliteWriter` (one connection, group commit, a `SAVEPOINT` per write);
  reads use pooled connections. A new write method wraps its body in `writer.WriteAsync` and sets
  `command.Transaction` on every command.
- Scale path without touching services: one file → one file per bucket (config switch) → Postgres provider.

## Every schema change

- Update `docs/database.md` (tables, columns, indexes, why each index exists).
- Review indexes for each new query; listing queries must be index range scans, never full scans.
- Later providers (Postgres) get the same migration as their own SQL and must pass the same contract.
