using ComicManagement.Wallet.Lots;
using ComicManagement.Wallet.Spending;
using Xunit;
using WalletAccount = ComicManagement.Wallet.Accounts.Wallet;

namespace ComicManagement.Wallet.UnitTests;

public sealed class WalletSpendingPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 14, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Apply_consumes_bonus_before_paid_and_paid_lots_by_created_at_then_id()
    {
        var userId = Guid.NewGuid();
        var wallet = WalletAccount.Create(userId, Now);
        wallet.CreditBonus(30, Now);
        wallet.CreditPaid(120, Now);
        var higherId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var lowerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var lots = new[]
        {
            PaidCoinLot.Create(higherId, userId, Guid.NewGuid(), 100, Now),
            PaidCoinLot.Create(lowerId, userId, Guid.NewGuid(), 20, Now),
        };

        var decision = WalletSpendingPolicy.Apply(
            ValidCommand(userId, 80),
            wallet,
            lots,
            Now.AddSeconds(1));

        Assert.Equal(SpendCoinsOutcome.Succeeded, decision.Result.Outcome);
        Assert.NotNull(decision.Operation);
        Assert.Equal(30, decision.Result.Receipt!.BonusCoins);
        Assert.Equal(50, decision.Result.Receipt.PaidCoins);
        Assert.Collection(
            decision.Result.Receipt.PaidAllocations,
            allocation =>
            {
                Assert.Equal(lowerId, allocation.PaidCoinLotId);
                Assert.Equal(20, allocation.AmountCoins);
            },
            allocation =>
            {
                Assert.Equal(higherId, allocation.PaidCoinLotId);
                Assert.Equal(30, allocation.AmountCoins);
            });
        Assert.Equal(0, wallet.BonusAvailable);
        Assert.Equal(70, wallet.PaidAvailable);
        Assert.Equal(20, lots[1].SpentCoins);
        Assert.Equal(30, lots[0].SpentCoins);
    }

    [Fact]
    public void Apply_rejects_insufficient_balance_without_mutating_aggregates()
    {
        var userId = Guid.NewGuid();
        var wallet = WalletAccount.Create(userId, Now);
        wallet.CreditBonus(10, Now);
        wallet.CreditPaid(20, Now);
        var lot = PaidCoinLot.Create(Guid.NewGuid(), userId, Guid.NewGuid(), 20, Now);

        var decision = WalletSpendingPolicy.Apply(
            ValidCommand(userId, 31),
            wallet,
            [lot],
            Now.AddSeconds(1));

        Assert.Equal(SpendCoinsOutcome.InsufficientCoins, decision.Result.Outcome);
        Assert.Null(decision.Operation);
        Assert.Empty(decision.Entries);
        Assert.Empty(decision.Allocations);
        Assert.Equal(10, wallet.BonusAvailable);
        Assert.Equal(20, wallet.PaidAvailable);
        Assert.Equal(0, lot.SpentCoins);
    }

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    public void Apply_rejects_invalid_command_without_mutation(SpendCoinsCommand command)
    {
        var wallet = WalletAccount.Create(
            command.UserId == Guid.Empty ? Guid.NewGuid() : command.UserId,
            Now);

        var decision = WalletSpendingPolicy.Apply(command, wallet, [], Now);

        Assert.Equal(SpendCoinsOutcome.ValidationFailed, decision.Result.Outcome);
        Assert.Null(decision.Operation);
        Assert.Empty(decision.Entries);
        Assert.Empty(decision.Allocations);
    }

    [Fact]
    public void Apply_rejects_a_paid_lot_owned_by_another_user()
    {
        var userId = Guid.NewGuid();
        var wallet = WalletAccount.Create(userId, Now);
        wallet.CreditPaid(10, Now);
        var foreignLot = PaidCoinLot.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            10,
            Now);

        var decision = WalletSpendingPolicy.Apply(
            ValidCommand(userId, 10),
            wallet,
            [foreignLot],
            Now.AddSeconds(1));

        Assert.Equal(SpendCoinsOutcome.InvariantViolation, decision.Result.Outcome);
        Assert.Equal(10, wallet.PaidAvailable);
        Assert.Equal(0, foreignLot.SpentCoins);
    }

    [Fact]
    public void Apply_rejects_wallet_to_lot_mismatch_without_partial_mutation()
    {
        var userId = Guid.NewGuid();
        var wallet = WalletAccount.Create(userId, Now);
        wallet.CreditPaid(20, Now);
        var lot = PaidCoinLot.Create(Guid.NewGuid(), userId, Guid.NewGuid(), 10, Now);

        var decision = WalletSpendingPolicy.Apply(
            ValidCommand(userId, 10),
            wallet,
            [lot],
            Now.AddSeconds(1));

        Assert.Equal(SpendCoinsOutcome.InvariantViolation, decision.Result.Outcome);
        Assert.Equal(20, wallet.PaidAvailable);
        Assert.Equal(0, lot.SpentCoins);
        Assert.Null(decision.Operation);
    }

    public static TheoryData<SpendCoinsCommand> InvalidCommands()
    {
        var valid = ValidCommand(Guid.NewGuid(), 80);
        return new TheoryData<SpendCoinsCommand>
        {
            valid with { UserId = Guid.Empty },
            valid with { SourceId = Guid.Empty },
            valid with { AmountCoins = 0 },
            valid with { AmountCoins = -1 },
            valid with { OperationType = " " },
            valid with { SourceType = " Unlock " },
            valid with { IdempotencyKey = string.Empty },
            valid with { IdempotencyKey = new string('x', 201) },
        };
    }

    private static SpendCoinsCommand ValidCommand(Guid userId, long amount) => new(
        userId,
        amount,
        "ChapterUnlock",
        "ReadingUnlock",
        Guid.NewGuid(),
        "unlock-key");
}
