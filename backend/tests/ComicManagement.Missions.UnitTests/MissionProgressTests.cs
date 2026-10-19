using ComicManagement.Missions.Definitions;
using ComicManagement.Missions.Progress;
using ComicManagement.Missions.Rewards;
using Xunit;

namespace ComicManagement.Missions.UnitTests;

public sealed class MissionProgressTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Activation_uses_half_open_interval_and_can_only_close_once()
    {
        var activation = MissionActivation.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Now);

        Assert.False(activation.Contains(Now.AddTicks(-1)));
        Assert.True(activation.Contains(Now));
        activation.Close(Now.AddHours(1));
        Assert.True(activation.Contains(Now.AddHours(1).AddTicks(-1)));
        Assert.False(activation.Contains(Now.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => activation.Close(Now.AddHours(2)));
    }

    [Fact]
    public void Progress_completes_once_and_never_exceeds_target()
    {
        var progress = MissionProgress.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "daily:2026-10-18");

        Assert.False(progress.Increment(2, Now));
        Assert.True(progress.Increment(2, Now.AddSeconds(1)));
        Assert.False(progress.Increment(2, Now.AddSeconds(2)));
        Assert.Equal(2, progress.ProgressCount);
        Assert.Equal(MissionProgressStatuses.Completed, progress.Status);
        Assert.Equal(Now.AddSeconds(1), progress.CompletedAt);
    }

    [Fact]
    public void Daily_counter_denies_whole_reward_over_cap()
    {
        var counter = MissionDailyCounter.Create(Guid.NewGuid(), new DateOnly(2026, 10, 18));

        Assert.True(counter.TryGrant(200));
        Assert.False(counter.TryGrant(101));
        Assert.Equal(200, counter.GrantedTotal);
        Assert.True(counter.TryGrant(100));
        Assert.Equal(300, counter.GrantedTotal);
    }
}
