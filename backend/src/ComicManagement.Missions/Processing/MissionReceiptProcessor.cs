namespace ComicManagement.Missions.Processing;

public enum MissionReceiptProcessingOutcome
{
    NoWork = 0,
    Processed = 1,
}

public interface IMissionReceiptProcessingStore
{
    Task<MissionReceiptProcessingOutcome> ProcessNextAsync(
        CancellationToken cancellationToken = default);

    Task<MissionReceiptProcessingOutcome> ProcessAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default);
}

public interface IMissionReceiptCriticalSectionObserver
{
    ValueTask WalletLockedAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed class NoOpMissionReceiptCriticalSectionObserver :
    IMissionReceiptCriticalSectionObserver
{
    public ValueTask WalletLockedAsync(Guid userId, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class MissionReceiptProcessor(IMissionReceiptProcessingStore store)
{
    public Task<MissionReceiptProcessingOutcome> ProcessNextAsync(
        CancellationToken cancellationToken = default) =>
        store.ProcessNextAsync(cancellationToken);

    public Task<MissionReceiptProcessingOutcome> ProcessAsync(
        Guid receiptId,
        CancellationToken cancellationToken = default) =>
        receiptId == Guid.Empty
            ? Task.FromResult(MissionReceiptProcessingOutcome.NoWork)
            : store.ProcessAsync(receiptId, cancellationToken);
}
