using ComicManagement.Shared.Pagination;
using Xunit;

namespace ComicManagement.Shared.UnitTests;

public sealed class OffsetPageTests
{
    [Theory]
    [InlineData(1, 30, 0)]
    [InlineData(100_000, 100, 9_999_900)]
    [InlineData(21_474_837, 100, 2_147_483_600)]
    [InlineData(int.MaxValue, 1, int.MaxValue - 1)]
    public void Supported_pages_produce_an_exact_non_overflowing_offset(
        int page,
        int pageSize,
        int expectedOffset)
    {
        Assert.True(OffsetPage.TryCreate(page, pageSize, 100, out var result));
        Assert.Equal(page, result.Page);
        Assert.Equal(pageSize, result.PageSize);
        Assert.Equal(expectedOffset, result.Offset);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(21_474_838, 100)]
    [InlineData(int.MaxValue, 100)]
    public void Unsupported_pages_are_rejected_without_overflow(int page, int pageSize)
    {
        Assert.False(OffsetPage.TryCreate(page, pageSize, 100, out _));
    }

    [Fact]
    public void Total_pages_is_rounded_up_and_capped_to_the_supported_response_range()
    {
        Assert.True(OffsetPage.TryCreate(1, 30, 100, out var page));
        Assert.Equal(0, page.TotalPages(0));
        Assert.Equal(2, page.TotalPages(31));
        Assert.Equal(int.MaxValue, page.TotalPages(long.MaxValue));
    }
}
