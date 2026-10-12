using ComicManagement.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ComicManagement.Api.IntegrationTests;

[ApiController]
[Route("api/v1/i02-test")]
public sealed class I02AuthorizationProbeController : ControllerBase
{
    [HttpGet("user")]
    [Authorize(Policy = AuthorizationPolicies.User)]
    public IActionResult UserRoute() => NoContent();

    [HttpGet("admin")]
    [Authorize(Policy = AuthorizationPolicies.Admin)]
    public IActionResult AdminRoute() => NoContent();

    [HttpPost("mutation")]
    [HttpPut("mutation")]
    [HttpPatch("mutation")]
    [HttpDelete("mutation")]
    [Authorize(Policy = AuthorizationPolicies.User)]
    public IActionResult AuthenticatedMutation() => NoContent();

    [HttpPost("public-mutation")]
    [AllowAnonymous]
    public IActionResult PublicMutation() => NoContent();
}
