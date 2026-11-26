using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IlWorkflow.API.Controllers;

/// <summary>Configuration is application-owned; execution, tasks and history use Elsa's native APIs.</summary>
[ApiController]
[Route("api/workflow-configurations")]
[Authorize(Policy = "WorkflowConfigurator")]
public sealed class WorkflowConfigurationController(IWorkflowConfigurationService service) : ControllerBase
{
    /// <summary>
    /// The only write endpoint. Without workflow_definition_id it creates a new definition. With only workflow_definition_id
    /// it creates that definition, or its next version when it exists. With both IDs it updates that draft version.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SaveConfigurationRequest request, CancellationToken ct) =>
        ApiResultHelper.Ok(ToResponse(await service.SaveAsync(request, ct)));

    /// <summary>Read one configuration version. Without a version number (<c>GET {workflow_definition_id}</c>) the latest version is returned.</summary>
    [HttpGet("{workflowDefinitionId}")]
    [HttpGet("{workflowDefinitionId}/versions/{workflowVersion:int}")]
    public async Task<IActionResult> Get(string workflowDefinitionId, int? workflowVersion, CancellationToken ct) =>
        ApiResultHelper.Ok(ToResponse(await service.GetAsync(workflowDefinitionId, workflowVersion, ct)));

    /// <summary>Run FluentValidation without changing or publishing the configuration.</summary>
    [HttpPost("{workflowDefinitionId}/versions/{workflowVersion:int}/validate")]
    public async Task<IActionResult> Validate(string workflowDefinitionId, int workflowVersion, CancellationToken ct)
    {
        await service.ValidateAsync(workflowDefinitionId, workflowVersion, ct);
        return ApiResultHelper.Ok(new ValidateConfigurationResponse(true));
    }

    /// <summary>Publish through Elsa; the bound snapshot becomes immutable only when publication succeeds.</summary>
    [HttpPost("{workflowDefinitionId}/versions/{workflowVersion:int}/publish")]
    public async Task<IActionResult> Publish(string workflowDefinitionId, int workflowVersion, CancellationToken ct) =>
        ApiResultHelper.Ok(ToResponse(await service.PublishAsync(workflowDefinitionId, workflowVersion, ct)));

    private static WorkflowConfigurationResponse ToResponse(WorkflowConfiguration row) => new(
        row.DefinitionId, row.DefinitionVersion, row.Revision, row.Ready,
        ConfigurationJson.Read<WorkflowSetup>(row.ConfigurationJson));
}
