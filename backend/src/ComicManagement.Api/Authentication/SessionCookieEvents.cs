using System.Globalization;
using System.Security.Claims;
using ComicManagement.Identity.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ComicManagement.Api.Authentication;

internal sealed class SessionCookieEvents(
    IGoogleIdentityStore identityStore) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var userIdClaim = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            await RejectAsync(context, disabled: false);
            return;
        }

        var user = await identityStore.FindUserByIdAsync(
            userId,
            context.HttpContext.RequestAborted);

        if (user is null || !user.IsActive)
        {
            await RejectAsync(context, disabled: user is not null);
            return;
        }

        var currentVersion = context.Principal?.FindFirstValue(SessionClaimTypes.Version);
        var expectedVersion = user.Version.ToString(CultureInfo.InvariantCulture);

        if (!string.Equals(currentVersion, expectedVersion, StringComparison.Ordinal))
        {
            context.ReplacePrincipal(LocalPrincipalFactory.Create(new AuthenticatedUser(
                user.Id,
                user.DisplayName,
                user.Email,
                user.AvatarUrl,
                user.Role,
                user.Status,
                user.Version)));
            context.ShouldRenew = true;
        }
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.HttpContext.Items.ContainsKey(AuthenticationConstants.DisabledAccountItem))
        {
            return ApiProblemWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                "Forbidden",
                "This account is disabled.");
        }

        return ApiProblemWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status401Unauthorized,
            "AUTH_REQUIRED",
            "Authentication required",
            "Sign in with Google to continue.");
    }

    public override Task RedirectToAccessDenied(
        RedirectContext<CookieAuthenticationOptions> context) => ApiProblemWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            "FORBIDDEN",
            "Forbidden",
            "You do not have permission to perform this action.");

    private static async Task RejectAsync(
        CookieValidatePrincipalContext context,
        bool disabled)
    {
        if (disabled)
        {
            context.HttpContext.Items[AuthenticationConstants.DisabledAccountItem] = true;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(AuthenticationConstants.SessionScheme);
    }
}
