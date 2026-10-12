using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ComicManagement.Api.Authentication;

internal sealed class AuthenticatedAntiforgeryFilter(
    IAntiforgery antiforgery,
    ApiProblemWriter problemWriter) : IAsyncAuthorizationFilter, IOrderedFilter
{
    public int Order => 1_000;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.HttpContext.Request;
        if (!IsUnsafe(request.Method) ||
            context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new ObjectResult(problemWriter.Create(
                context.HttpContext,
                StatusCodes.Status400BadRequest,
                "VALIDATION_FAILED",
                "Validation failed",
                "The CSRF token is missing or invalid."))
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { "application/problem+json" },
            };
        }
    }

    private static bool IsUnsafe(string method) =>
        !HttpMethods.IsGet(method) &&
        !HttpMethods.IsHead(method) &&
        !HttpMethods.IsOptions(method) &&
        !HttpMethods.IsTrace(method);
}
