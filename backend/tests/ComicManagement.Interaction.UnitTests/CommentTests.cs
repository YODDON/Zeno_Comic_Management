using ComicManagement.Interaction.Comments;
using Xunit;

namespace ComicManagement.Interaction.UnitTests;

public sealed class CommentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_requires_exactly_one_target_and_normalizes_body()
    {
        var comment = Comment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, "  Nội dung  ", Now);
        Assert.Equal("Nội dung", comment.Body);
        Assert.Equal(CommentStatuses.Visible, comment.Status);
        Assert.Throws<ArgumentException>(() => Comment.Create(Guid.NewGuid(), Guid.NewGuid(), null, null, null, "body", Now));
        Assert.Throws<ArgumentException>(() => Comment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "body", Now));
    }

    [Fact]
    public void Restore_allows_hidden_but_never_deleted()
    {
        var hidden = NewComment();
        hidden.Hide(1, Now.AddMinutes(1));
        hidden.Restore(2, Now.AddMinutes(2));
        Assert.Equal(CommentStatuses.Visible, hidden.Status);

        var deleted = NewComment();
        deleted.Delete(1, Now.AddMinutes(1));
        var exception = Assert.Throws<CommentRuleViolationException>(() => deleted.Restore(2, Now.AddMinutes(2)));
        Assert.Equal(CommentRuleViolation.InvalidState, exception.Violation);
    }

    [Fact]
    public void Edit_and_transitions_enforce_version_and_state()
    {
        var comment = NewComment();
        Assert.Throws<CommentRuleViolationException>(() => comment.Edit("new", 2, Now.AddMinutes(1)));
        comment.Hide(1, Now.AddMinutes(1));
        Assert.Throws<CommentRuleViolationException>(() => comment.Edit("new", 2, Now.AddMinutes(2)));
        Assert.Throws<CommentRuleViolationException>(() => comment.Hide(2, Now.AddMinutes(2)));
    }

    private static Comment NewComment() => Comment.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, "body", Now);
}
