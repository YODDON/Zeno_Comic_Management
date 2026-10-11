namespace ComicManagement.Shared.Identity;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    bool IsInRole(string role);
}
