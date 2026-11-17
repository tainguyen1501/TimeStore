namespace TimeStore.Primitives;

/// <summary>
/// Success or an <see cref="AppError"/>. A struct so the success path allocates nothing.
/// <c>default(Result)</c> is a success.
/// </summary>
public readonly struct Result
{
    private readonly AppError? _error;

    private Result(AppError error) => _error = error;

    public static Result Success => default;

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    public AppError Error => _error ?? throw new InvalidOperationException("A successful result has no error.");

    public static implicit operator Result(AppError error) =>
        new(error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>
/// A value or an <see cref="AppError"/>. A struct so the success path allocates nothing.
/// <c>default(Result&lt;T&gt;)</c> is a success holding <c>default(T)</c>; always build results from a value or an error.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly AppError? _error;

    private Result(T value)
    {
        _value = value;
        _error = null;
    }

    private Result(AppError error)
    {
        _value = default;
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    public T Value => _error is null ? _value! : throw new InvalidOperationException("A failed result has no value.");

    public AppError Error => _error ?? throw new InvalidOperationException("A successful result has no error.");

    /// <summary>A success; needed where <typeparamref name="T"/> is an interface (C# has no implicit conversion then).</summary>
    public static Result<T> Ok(T value) => new(value);

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(AppError error) =>
        new(error ?? throw new ArgumentNullException(nameof(error)));
}
