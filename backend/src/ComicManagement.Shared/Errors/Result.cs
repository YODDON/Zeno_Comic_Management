namespace ComicManagement.Shared.Errors;

public sealed class Result
{
    private readonly Error? _error;

    private Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        _error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error => _error
        ?? throw new InvalidOperationException("A successful result does not contain an error.");

    public static Result Success() => new(true, null);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new Result(false, error);
    }
}
