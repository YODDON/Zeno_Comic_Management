namespace ComicManagement.Billing.Payments;

public static class TopUpAmountPolicy
{
    public const long MinimumAmountVnd = 10_000;
    public const long MaximumAmountVnd = 2_000_000;
    public const long AmountStepVnd = 1_000;
    public const long VndPerPaidCoin = 10;

    public static bool IsValid(long amountVnd) =>
        amountVnd is >= MinimumAmountVnd and <= MaximumAmountVnd &&
        amountVnd % AmountStepVnd == 0;

    public static long CalculatePaidCoins(long amountVnd)
    {
        EnsureValid(amountVnd);
        return amountVnd / VndPerPaidCoin;
    }

    internal static void EnsureValid(long amountVnd)
    {
        if (!IsValid(amountVnd))
        {
            throw new ArgumentOutOfRangeException(
                nameof(amountVnd),
                $"TopUp amount must be between {MinimumAmountVnd} and {MaximumAmountVnd} VND in increments of {AmountStepVnd} VND.");
        }
    }
}
