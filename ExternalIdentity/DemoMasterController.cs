using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IlWorkflow.Api.ExternalIdentity;

/// <summary>Two fake master APIs: GET only, five employees each, never available outside Development.</summary>
[ApiController]
[Route("api/demo-masters")]
[AllowAnonymous]
public sealed class DemoMasterController(DemoMemberDirectory directory) : ControllerBase
{
    [HttpGet("usernames")]
    public IActionResult Usernames() => Get("username-demo");

    [HttpGet("emails")]
    public IActionResult Emails() => Get("email-demo");

    private IActionResult Get(string providerId)
    {
        var provider = directory.Provider(providerId);
        return provider is null ? NotFound() : Ok(new { providerId, items = directory.Members(provider) });
    }
}
