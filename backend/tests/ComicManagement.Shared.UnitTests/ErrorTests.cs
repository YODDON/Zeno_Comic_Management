using ComicManagement.Shared.Errors;
using Xunit;

namespace ComicManagement.Shared.UnitTests;

public sealed class ErrorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_missing_code(string? code)
    {
        Assert.ThrowsAny<ArgumentException>(() => new Error(code!, ErrorType.Validation));
    }

    [Fact]
    public void Constructor_rejects_unknown_error_type()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Error("UNKNOWN", (ErrorType)int.MaxValue));
    }

    [Fact]
    public void Constructor_preserves_code_and_type()
    {
        var error = new Error("VALIDATION_FAILED", ErrorType.Validation);

        Assert.Equal("VALIDATION_FAILED", error.Code);
        Assert.Equal(ErrorType.Validation, error.Type);
    }
}
