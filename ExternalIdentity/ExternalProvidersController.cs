using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IlWorkflow.Api.ExternalIdentity;

/// <summary>Configurator selection is a GET flow. Copy participantId directly into stage candidates/assignee.</summary>
[ApiController]
[Route("api/external-providers")]
[Authorize(Policy = "WorkflowConfigurator")]
public sealed class ExternalProvidersController(DemoMemberDirectory directory) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { items = directory.Providers.Select(provider => new
    {
        provider.Id, provider.DisplayName, provider.Issuer, provider.Audience, provider.MatchClaim, provider.MemberField,
        membersUrl = $"/api/external-providers/{provider.Id}/members"
    }) });

    [HttpGet("{providerId}/members")]
    public IActionResult Members(string providerId)
    {
        var provider = directory.Provider(providerId);
        return provider is null ? NotFound() : Ok(new { providerId, items = directory.Members(provider) });
    }
}
