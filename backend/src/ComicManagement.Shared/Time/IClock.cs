namespace ComicManagement.Shared.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
