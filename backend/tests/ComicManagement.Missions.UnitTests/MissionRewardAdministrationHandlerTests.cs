using ComicManagement.Missions.Administration;
using ComicManagement.Missions.Rewards;
using ComicManagement.Shared.Pagination;
using Xunit;

namespace ComicManagement.Missions.UnitTests;

public sealed class MissionRewardAdministrationHandlerTests
{
    [Theory]
    [InlineData("Unknown", 1, 30)]
    [InlineData("Granted", 0, 30)]
    [InlineData("DeniedDailyCap", 1, 0)]
    [InlineData("Granted", 1, 101)]
    [InlineData("Granted", int.MaxValue, 100)]
    public async Task Invalid_criteria_fail_before_the_store_is_called(
        string outcome,
        int page,
        int pageSize)
    {
        var store = new RecordingStore();
        var handler = new MissionRewardAdministrationHandler(store);

        var result = await handler.ListAsync(outcome, page, pageSize);

        Assert.Equal(MissionRewardAdministrationOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Granted")]
    [InlineData("DeniedDailyCap")]
    public async Task Closed_reward_outcomes_are_forwarded_to_the_store(string? outcome)
    {
        var store = new RecordingStore();
        var handler = new MissionRewardAdministrationHandler(store);

        var result = await handler.ListAsync(outcome, 1, 30);

        Assert.Equal(MissionRewardAdministrationOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.Page);
        Assert.Equal(1, store.Calls);
        Assert.Equal(string.IsNullOrWhiteSpace(outcome) ? null : outcome, store.Outcome);
    }

    private sealed class RecordingStore : IMissionRewardAdministrationStore
    {
        public int Calls { get; private set; }
        public string? Outcome { get; private set; }
        public OffsetPage Page { get; private set; }

        public Task<AdminMissionRewardPage> ListAsync(
            string? outcome,
            OffsetPage page,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Outcome = outcome;
            Page = page;
            return Task.FromResult(new AdminMissionRewardPage([], page.Page, page.PageSize, 0, 0));
        }
    }
}
