namespace Samian.Domain.Common;

// The return type for DOMAIN SERVICES (a cross-aggregate check like stock availability).
// The split with entities is deliberate:
//   - an entity method throws, because a broken invariant means the object is not allowed to exist;
//   - a service returns Result, because "is there enough stock?" is a question the caller asks
//     BEFORE mutating anything, and a failed answer is a normal business outcome, not a crash.
public class Result
{
    // protected: the only ways in are Success()/Failure(), so an instance can never be
    // "neither success nor failure" (and Result<T> still reaches the base through it).
    protected Result(bool isSuccess, string? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public string? Error { get; }

    public static Result Success() => new(true, null);

    public static Result Failure(string error) => new(false, error);

    public static Result<T> Success<T>(T value) => new(value, true, null);

    public static Result<T> Failure<T>(string error) => new(default, false, error);
}

public sealed class Result<T> : Result
{
    internal Result(T? value, bool isSuccess, string? error) : base(isSuccess, error)
    {
        Value = value;
    }

    public T? Value { get; }

    // Throwing on Value when the result is a failure turns "forgot to check IsSuccess" into a loud
    // bug instead of a silent null reference deep in a handler.
    public T ValueOrThrow() => IsSuccess
        ? Value!
        : throw new InvalidOperationException($"Result is a failure: {Error}");
}
