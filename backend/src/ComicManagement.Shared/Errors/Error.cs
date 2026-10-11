namespace ComicManagement.Shared.Errors;

public sealed record Error
{
    public Error(string code, ErrorType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown error type.");
        }

        Code = code;
        Type = type;
    }

    public string Code { get; }

    public ErrorType Type { get; }
}
