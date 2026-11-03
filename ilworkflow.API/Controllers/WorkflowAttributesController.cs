using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace IlWorkflow.API.Controllers;

/// <summary>Configure the field master before saving a workflow. Positive IDs update existing fields.</summary>
[ApiController, Route("api/workflow-attributes"), Authorize(Policy = "WorkflowConfigurator")]
public sealed class WorkflowAttributesController(IWorkflowAttributeService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Save(SaveWorkflowAttributeRequest request, CancellationToken ct) => Ok(await service.SaveAsync(request, ct));
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string definitionId, CancellationToken ct) => Ok(new { items = await service.ListAsync(definitionId, ct) });
}
