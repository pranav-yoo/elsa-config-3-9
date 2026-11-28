using IlWorkflow.Infrastructure.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IlWorkflow.API.Controllers;

/// <summary>
/// Application task API over Elsa's user-task manager. Put company authentication/validation here;
/// Elsa's native /elsa/api/user-tasks endpoints remain available with the same rules.
/// Commands return 202 (accepted, the next task may appear shortly) or 409 (conflict, reload and retry).
/// </summary>
[ApiController]
[Route("api/tasks")]
[Authorize]
public sealed class TasksController(TaskService tasks) : ControllerBase
{
    /// <summary>scope: assigned | available | history | all.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string scope = "assigned", [FromQuery] string? status = null,
        [FromQuery(Name = "workflow_instance_id")] string? workflowInstanceId = null, [FromQuery] string? cursor = null, [FromQuery] int limit = 50,
        [FromQuery(Name = "include_total_count")] bool includeTotalCount = false, CancellationToken ct = default) =>
        ApiResultHelper.Ok(await tasks.ListAsync(User, new ListTasksRequest(scope, status, workflowInstanceId, cursor, limit, includeTotalCount), ct));

    /// <summary>Task detail, including its actions (buttons) and allowedActions for the caller.</summary>
    [HttpGet("{taskId}")]
    public async Task<IActionResult> Get(string taskId, CancellationToken ct) => ApiResultHelper.Ok(await tasks.GetAsync(User, taskId, ct));

    [HttpGet("{taskId}/events")]
    public async Task<IActionResult> Events(string taskId, [FromQuery] string? cursor = null, [FromQuery] int limit = 50, CancellationToken ct = default) =>
        ApiResultHelper.Ok(await tasks.EventsAsync(User, taskId, cursor, limit, ct));

    [HttpPost("{taskId}/claim")]
    public async Task<IActionResult> Claim(string taskId, [FromBody] TaskCommandRequest? request, CancellationToken ct) =>
        Send(await tasks.ClaimAsync(User, taskId, request ?? new(), ct));

    [HttpPost("{taskId}/release")]
    public async Task<IActionResult> Release(string taskId, [FromBody] TaskCommandRequest? request, CancellationToken ct) =>
        Send(await tasks.ReleaseAsync(User, taskId, request ?? new(), ct));

    /// <summary>Reassign (owner or supervisor). Body: { "userId": "...", "reason": "..." }.</summary>
    [HttpPost("{taskId}/assign")]
    public async Task<IActionResult> Assign(string taskId, [FromBody] AssignTaskRequest request, CancellationToken ct) =>
        Send(await tasks.AssignAsync(User, taskId, request, ct));

    /// <summary>Press a task button. Body: { "actionKey": "APPROVE" | "REJECT" | custom | "RESUBMIT", "data": { "reason": "..." } }.</summary>
    [HttpPost("{taskId}/complete")]
    public async Task<IActionResult> Complete(string taskId, [FromBody] CompleteTaskRequest request, CancellationToken ct) =>
        Send(await tasks.CompleteAsync(User, taskId, request, ct));

    private ObjectResult Send(TaskCommandResult result) => result.Accepted
        ? ApiResultHelper.Accepted(result.Body, "Accepted. The next task may take a moment to appear.")
        : ApiResultHelper.Conflict(result.Body, "Conflict. Reload the task and retry.");
}
