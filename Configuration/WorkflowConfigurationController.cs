using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IlWorkflow.Api.Configuration;

/// <summary>Configuration is application-owned; execution, tasks and history use Elsa's native APIs.</summary>
[ApiController]
[Route("api/workflow-configurations")]
[Authorize(Policy = "WorkflowConfigurator")]
public sealed class WorkflowConfigurationController(WorkflowConfigurationService service) : ControllerBase
{
    /// <summary>Missing/zero ID creates a draft; positive ID updates that draft using ExpectedRevision.</summary>
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SaveConfigurationRequest request, CancellationToken ct) =>
        Ok(ToResponse(await service.SaveAsync(request, ct)));

    /// <summary>Read the configuration snapshot, native definition identifiers and current revision.</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct) => Ok(ToResponse(await service.GetAsync(id, ct)));

    /// <summary>Run FluentValidation without changing or publishing the configuration.</summary>
    [HttpPost("{id:long}/validate")]
    public async Task<IActionResult> Validate(long id, CancellationToken ct)
    {
        await service.ValidateAsync(id, ct);
        return Ok(new { valid = true });
    }

    /// <summary>Publish through Elsa; the bound snapshot becomes immutable only when publication succeeds.</summary>
    [HttpPost("{id:long}/publish")]
    public async Task<IActionResult> Publish(long id, [FromBody] RevisionRequest request, CancellationToken ct) =>
        Ok(ToResponse(await service.PublishAsync(id, request.ExpectedRevision, ct)));

    /// <summary>Reuse Elsa's version lifecycle to create the next editable configuration draft.</summary>
    [HttpPost("{id:long}/next-draft")]
    public async Task<IActionResult> NextDraft(long id, [FromBody] RevisionRequest request, CancellationToken ct) =>
        Ok(ToResponse(await service.CreateNextDraftAsync(id, request.ExpectedRevision, ct)));

    private static object ToResponse(WorkflowConfiguration row) => new
    {
        row.Id, row.DefinitionId, row.DefinitionVersionId, row.DefinitionVersion, row.Revision,
        isPublished = row.Ready, configuration = ConfigurationJson.Read<WorkflowSetup>(row.ConfigurationJson)
    };
}
