using System.Security.Claims;
using ComicManagement.Identity.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace ComicManagement.Api.Authentication;

internal sealed class GoogleOpenIdConnectEvents(
    GoogleSignInHandler signInHandler,
    ApiProblemWriter problemWriter) : OpenIdConnectEvents
{
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        try
        {
            var providerPrincipal = context.Principal
                ?? throw new InvalidOperationException("Google principal is missing.");
            var subject = providerPrincipal.FindFirstValue("sub")
                ?? throw new InvalidOperationException("Google subject is missing.");
            var displayName = providerPrincipal.FindFirstValue("name")
                ?? throw new InvalidOperationException("Google display name is missing.");
            var email = providerPrincipal.FindFirstValue("email");
            var avatarUrl = providerPrincipal.FindFirstValue("picture");
            var emailVerified = providerPrincipal.FindFirstValue("email_verified");

            if (email is not null &&
                (!bool.TryParse(emailVerified, out var verified) || !verified))
            {
                throw new InvalidOperationException("Google email is not verified.");
            }

            var result = await signInHandler.HandleAsync(
                new GoogleUserProfile(subject, displayName, email, avatarUrl),
                context.HttpContext.RequestAborted);

            if (result.Outcome == GoogleSignInOutcome.Disabled)
            {
                context.Fail(new AccountDisabledException());
                return;
            }

            context.Principal = LocalPrincipalFactory.Create(
                result.User ?? throw new InvalidOperationException("Authenticated user is missing."));
        }
        catch (AccountDisabledException exception)
        {
            context.Fail(exception);
        }
        catch (Exception exception) when (
            (exception is ArgumentException || exception is InvalidOperationException) &&
            exception is not IdentityConcurrencyException)
        {
            context.Fail(new InvalidGoogleIdentityException(exception));
        }
    }

    public override Task RemoteFailure(RemoteFailureContext context)
    {
        context.HandleResponse();

        if (context.Failure is AccountDisabledException)
        {
            return problemWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                "Forbidden",
                "This account is disabled.");
        }

        if (ContainsException<IdentityConcurrencyException>(context.Failure))
        {
            return problemWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status409Conflict,
                "CONCURRENCY_CONFLICT",
                "Conflict",
                "The sign-in state changed concurrently. Start sign-in again.");
        }

        if (ContainsException<HttpRequestException>(context.Failure) ||
            ContainsException<TaskCanceledException>(context.Failure))
        {
            return problemWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status503ServiceUnavailable,
                "PROVIDER_UNAVAILABLE",
                "Provider unavailable",
                "Google authentication is temporarily unavailable.");
        }

        return problemWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status401Unauthorized,
            "AUTH_REQUIRED",
            "Authentication failed",
            "Google authentication could not be completed.");
    }

    private sealed class AccountDisabledException : InvalidOperationException;

    private sealed class InvalidGoogleIdentityException(Exception innerException) :
        InvalidOperationException("Google identity validation failed.", innerException);

    private static bool ContainsException<TException>(Exception? exception)
        where TException : Exception
    {
        while (exception is not null)
        {
            if (exception is TException)
            {
                return true;
            }

            exception = exception.InnerException;
        }

        return false;
    }
}
