using ComicManagement.Shared.Errors;
using Xunit;

namespace ComicManagement.Shared.UnitTests;

public sealed class ResultTests
{
    private static readonly Error ValidationError =
        new("VALIDATION_FAILED", ErrorType.Validation);

    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Failure_exposes_its_error()
    {
        var result = Result.Failure(ValidationError);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Same(ValidationError, result.Error);
    }

    [Fact]
    public void Failure_rejects_null_error()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Fact]
    public void Generic_success_exposes_its_value()
    {
        var result = Result<string>.Success("value");

        Assert.True(result.IsSuccess);
        Assert.Equal("value", result.Value);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Generic_success_rejects_null_value()
    {
        Assert.Throws<ArgumentNullException>(() => Result<string>.Success(null!));
    }

    [Fact]
    public void Generic_failure_exposes_error_and_not_value()
    {
        var result = Result<string>.Failure(ValidationError);

        Assert.True(result.IsFailure);
        Assert.Same(ValidationError, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Generic_failure_rejects_null_error()
    {
        Assert.Throws<ArgumentNullException>(() => Result<string>.Failure(null!));
    }
}
