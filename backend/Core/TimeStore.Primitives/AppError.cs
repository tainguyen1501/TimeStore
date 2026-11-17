namespace TimeStore.Primitives;

/// <summary>What kind of failure an <see cref="AppError"/> is. Hosts map it to their status (HTTP, S3 error).</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,

    /// <summary>The caller is not signed in (or its credentials are wrong).</summary>
    Unauthorized,

    /// <summary>The caller is signed in but may not do this.</summary>
    Forbidden,

    PreconditionFailed,

    /// <summary>Slow down (e.g. repeated failed sign-ins).</summary>
    TooManyRequests,

    Internal,
}

/// <summary>
/// The one error type. Created only by a feature's error catalog (<c>XxxErrors</c>), never at a call site.
/// A class on purpose: it is allocated only on failure; parameterless errors are cached in the catalog.
/// </summary>
public sealed class AppError
{
    private AppError(ErrorKind kind, string code, string message)
    {
        Kind = kind;
        Code = code;
        Message = message;
    }

    public ErrorKind Kind { get; }

    /// <summary>Stable contract (UPPER_SNAKE_CASE, feature prefix); clients branch on it, never on the message.</summary>
    public string Code { get; }

    /// <summary>English, user-facing, without secrets or internal names.</summary>
    public string Message { get; }

    public static AppError Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static AppError NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static AppError Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static AppError Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);

    public static AppError Unauthorized(string code, string message) => new(ErrorKind.Unauthorized, code, message);

    public static AppError TooManyRequests(string code, string message) => new(ErrorKind.TooManyRequests, code, message);

    public static AppError PreconditionFailed(string code, string message) =>
        new(ErrorKind.PreconditionFailed, code, message);

    public static AppError Internal(string code, string message) => new(ErrorKind.Internal, code, message);

    public override string ToString() => $"{Code}: {Message}";
}
