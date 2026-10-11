namespace ComicManagement.Shared.Errors;

public sealed class Result<T>
{
    private readonly Error? _error;
    private readonly T? _value;

    private Result(bool isSuccess, T? value, Error? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        _error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result does not contain a value.");

    public Error Error => _error
        ?? throw new InvalidOperationException("A successful result does not contain an error.");

    public static Result<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new Result<T>(true, value, null);
    }

    public static Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new Result<T>(false, default, error);
    }
}
