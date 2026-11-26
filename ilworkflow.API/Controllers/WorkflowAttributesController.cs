using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace IlWorkflow.API.Controllers;

/// <summary>Configure the field master before saving a workflow. Positive IDs update existing fields.</summary>
[ApiController, Route("api/workflow-attributes"), Authorize(Policy = "WorkflowConfigurator")]
public sealed class WorkflowAttributesController(IWorkflowAttributeService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Save(SaveWorkflowAttributeRequest request, CancellationToken ct) => Ok(ToResponse(await service.SaveAsync(request, ct)));
    [HttpGet]
    public async Task<IActionResult> List([FromQuery(Name = "definition_id")] string definitionId, CancellationToken ct) => Ok(new WorkflowAttributeListResponse((await service.ListAsync(definitionId, ct)).Select(ToResponse).ToArray()));

    private static WorkflowAttributeResponse ToResponse(WorkflowAttribute row) => new(
        row.Id, row.DefinitionId, row.Key, row.Type, row.Required, row.Choices, row.MaxLength, row.Minimum, row.Maximum, row.Status);
}
