namespace ComicManagement.Shared.Persistence;

/// <summary>
/// Executes a read operation in a repeatable-read-or-stronger consistency boundary.
/// A new repeatable-read transaction is opened when none exists. An existing
/// transaction may be reused only when its isolation level is RepeatableRead or
/// Serializable; weaker transactions are rejected rather than silently reused.
/// </summary>
public interface IConsistentReadExecutor
{
    Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default);
}
