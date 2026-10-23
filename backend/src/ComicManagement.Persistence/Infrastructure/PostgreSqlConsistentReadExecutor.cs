using System.Data;
using ComicManagement.Shared.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ComicManagement.Persistence;

internal sealed class PostgreSqlConsistentReadExecutor(AppDbContext dbContext) :
    IConsistentReadExecutor
{
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (dbContext.Database.CurrentTransaction is { } existingTransaction)
        {
            var isolationLevel = existingTransaction.GetDbTransaction().IsolationLevel;
            if (isolationLevel is not IsolationLevel.RepeatableRead and not IsolationLevel.Serializable)
            {
                throw new InvalidOperationException(
                    $"A consistent read requires RepeatableRead or Serializable isolation, but the existing transaction uses {isolationLevel}.");
            }

            return await action(cancellationToken);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        var result = await action(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
