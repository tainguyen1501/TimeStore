---
paths:
  - "backend/Src/**/*.cs"
  - "backend/Core/**/*.cs"
---

# Backend code conventions

## Feature layout

```
Application/Features/Buckets/   IBucketService, BucketErrors, BucketValidation, BucketMappings
Infra/Services/Buckets/         BucketService (sealed)
Contracts/Buckets/              CreateBucketRequest, BucketResponse
Domain/Buckets/                 Bucket (record), BucketNames (rules)
Server/Admin/                   endpoints; Server/S3/ dispatcher + handlers
```

## Services (use cases)

- Interface in `Application/Features/{Feature}`, `sealed` implementation in `Infra/Services/{Feature}`, one method per
  use case, split by area so no service grows into a god class.
- **Stateless singletons**, constructor-injected with ports (`IMetadataStore`, `IBlobStore`) and `TimeProvider`.
  Registered by hand in `InfraServiceCollectionExtensions` (AOT: no scanning).
- **Services never touch SQL, connections, transactions, file paths or HTTP.** Data goes through the Domain ports;
  if a port lacks a question, add a named method to the port (and to the contract tests), never a generic query.
- Validate first (`XxxValidation.Validate(request)` returns `AppError?`), then call ports, then map.
- Return Contracts DTOs to hosts. On the object hot path a service may return Domain records when mapping would cost
  an allocation per request; decide per method and say why in the XML doc.
- Atomicity belongs to the store: use `TryCreate...` / compare-and-swap methods, never "find, then insert".

## Libraries behind interfaces

- Every third-party or framework library used by features (cache, checksums, crypto, compression, serialization of
  stored data, database) is wrapped by a TimeStore interface. Services, Protocol code and middlewares depend on the
  interface; one implementation class knows the library and is registered by hand.
- Interfaces are shaped by our use (`ICacheService.GetOrCreateAsync(key, factory, tags)`), not copies of the library
  API, so a replacement does not leak through.
- Hot-path exception: a value type or static helper is fine when an interface would cost per-byte work (e.g. the
  checksum *instance* is a struct/class from `IChecksumFactory`; the factory is the interface).

## Storage ports (`Domain/Stores`)

- Shaped by S3 operations (`CommitObjectVersion`, `ListObjects(prefix, delimiter, startAfter, max)`), not CRUD.
- Names say the question: `Find...` (one, null when missing), `List...` (many, defined order), `TryCreate...`
  (false on conflict), `Delete...` (false when missing).
- Take and return Domain types or primitives, never Contracts DTOs. No business rules, no errors, no validation.
- Every behaviour is pinned by a fact in `Tests/TimeStore.Metadata.ContractTests/MetadataStoreContract.cs`.

## Errors (standard)

One error type, `AppError` (Primitives: `Kind`, `Code`, `Message`), one catalog per feature:

```csharp
public static class BucketErrors
{
    public static class Codes                       // stable: never change a value once released
    {
        public const string NotFound = "BUCKET_NOT_FOUND";
    }

    public static AppError NotFound(string name) =>
        AppError.NotFound(Codes.NotFound, $"Bucket '{name}' was not found.");
}

if (!await store.DeleteBucketAsync(name, ct)) return BucketErrors.NotFound(name);   // AppError → Result
return bucket.ToResponse();                                                        // value → Result<T>
```

- Codes: constants, `UPPER_SNAKE_CASE`, feature prefix, unique. Never derived from a C# member name.
- **Never create an error at a call site** (no `AppError.Conflict(...)` inside a service): add it to the catalog.
  Parameterless errors are `static readonly` fields (no allocation per failure).
- Shared errors live in `Application/Common/CommonErrors` (`INTERNAL_ERROR`).
- Exception (by design): the verifying request-body streams of `TimeStore.Protocol/Streaming` throw
  `S3BodyVerificationException` (an `IOException` carrying an `AppError`) because a `Stream` cannot return a
  `Result`. `S3Dispatcher` catches it and answers with the mapped S3 error; services let it propagate and must not
  commit metadata when reading the body failed.
- Services return errors; they never throw for business rules. Exceptions are bugs or I/O failures and are answered
  by `ExceptionHandling`: S3 `InternalError` XML on the S3 port, `INTERNAL_ERROR` JSON on the admin port.
- Hosts map `ErrorKind` to a status (`ApiResults.StatusFor`) and, on the S3 port, `Code` to an S3 error code
  (`S3ErrorCodes`). Every new code is listed in `docs/api/` (admin) or the S3 error map (S3).
- Messages are English, user-facing, without secrets or internal names.

## No magic strings

Literals appear **only in constant declarations** or the one catalog that owns them: error codes, routes, SQL,
header names, XML element names, config keys, file and directory names. Exceptions: log message templates,
exception messages for bugs, interpolations built from constants, test data.

## Endpoints (admin API)

- Live in `Server/Admin/`, mapped under `/api/v1`. Port separation is by **local port**: the S3 branch (`S3Pipeline`) answers
  everything on the S3 port (except `/health`), so admin endpoints are only reachable on the admin port. Never
  select by Host header (port mapping and proxies change it).
- Malformed bodies throw `BadHttpRequestException` (`ThrowOnBadRequest`) and are answered as 400
  `COMMON_INVALID_REQUEST` by `ExceptionHandling`.
- Inject the service, call one method, map with `ApiResults` / `TypedResults`. No logic, no ports, no SQL.
- Every request/response type is listed in `AdminJsonContext` (source-generated JSON).

## Secrets

Secret access keys are encrypted at rest through one protector type (to be added with access keys). Nothing else
encrypts or decrypts secrets; secrets never appear in logs, errors or responses.
