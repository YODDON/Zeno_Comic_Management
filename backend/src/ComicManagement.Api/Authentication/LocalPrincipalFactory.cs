using System.Globalization;
using System.Security.Claims;
using ComicManagement.Identity.Authentication;

namespace ComicManagement.Api.Authentication;

internal static class LocalPrincipalFactory
{
    internal static ClaimsPrincipal Create(AuthenticatedUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role),
            new(SessionClaimTypes.Status, user.Status),
            new(SessionClaimTypes.Version, user.Version.ToString(CultureInfo.InvariantCulture)),
        };

        if (user.Email is not null)
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        if (user.AvatarUrl is not null)
        {
            claims.Add(new Claim(SessionClaimTypes.AvatarUrl, user.AvatarUrl));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            AuthenticationConstants.SessionScheme,
            ClaimTypes.Name,
            ClaimTypes.Role));
    }
}
