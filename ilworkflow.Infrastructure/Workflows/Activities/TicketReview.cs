using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Expressions.Models;
using Elsa.UserTasks.Activities;
using Elsa.UserTasks.Models;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Models;
using FluentValidation;
using UserTask = Elsa.UserTasks.Activities.UserTask;
using ConfigJson = IlWorkflow.Application.Common.ConfigurationJson;

namespace IlWorkflow.Api.Workflows;

[Activity("Tickets", "Tickets", "Schedules one native approval or correction task.")]
[FlowNode("Action", "Resubmit")]
public sealed class TicketReview : Activity
{
    [Input] public Input<string> ConfigurationJson { get; set; } = null!;
    [Input] public Input<string> StageKey { get; set; } = null!;
    [Input] public Input<bool> Correction { get; set; } = new(false);
    [Input] public Input<bool> ParallelBranch { get; set; } = new(false);
    [JsonIgnore, Port("Review")] public UserTask Review { get; set; }
    private static ActivityExecutionContext Parent(ExpressionExecutionContext context) => context.GetActivityExecutionContext().ParentActivityExecutionContext!;
    // The pinned snapshot + stage key stored on this visit; never the latest editable configuration.
    // Visits recorded before stage actions existed have no "stageKey" property: return null.
    private static StageDefinition? StageOf(ActivityExecutionContext context) =>
        context.GetProperty<string>("stageKey") is { } key && context.GetProperty<string>("schema") is { } schema
            ? ConfigJson.Read<WorkflowSetup>(schema).Stages.FirstOrDefault(x => x.Key == key)
            : null;
    private static UserTaskActionDefinition[] ActionsOf(ActivityExecutionContext context) =>
        StageOf(context)?.Actions?.Select(x => new UserTaskActionDefinition(x.Key, x.DisplayLabel)).ToArray() ?? [];
    private static ResolvedAssignment Assignment(ExpressionExecutionContext context) => ConfigJson.Read<ResolvedAssignment>(Parent(context).GetProperty<string>("assignment")!);
    public TicketReview()
    {
        Review = new UserTask
        {
            Id = "review",
            Title = new(ctx => Parent(ctx).GetProperty<string>("title")!),
            Summary = new(ctx => Assignment(ctx).Blocked ? "Assignment blocked. Supervisor reassignment required." : "Review this request."),
            TaskType = new(ctx => Parent(ctx).GetProperty<bool>("parallelBranch") ? ParallelTicketReview.TaskType : "configured-ticket"),
            Tags = new(ctx => Parent(ctx).GetProperty<string>("observerTags") is { } tags
                ? ConfigJson.Read<string[]>(tags) : []),
            Reference = new(ctx => Parent(ctx).WorkflowExecutionContext.CorrelationId),
            Requester = new(ctx => TicketState.User(TicketState.Requester(Parent(ctx).WorkflowExecutionContext))),
            CandidateUsers = new(ctx => Assignment(ctx).Candidates.Select(TicketState.User).ToArray()),
            Assignee = new(ctx => Assignment(ctx).Assignee is { } id ? TicketState.User(id) : null),
            MembershipResolutionMode = new(UserTaskMembershipResolutionMode.Snapshot),
            TaskData = new(ctx => JsonSerializer.SerializeToElement(TicketState.Attributes(Parent(ctx).WorkflowExecutionContext))),
            // Typed actions avoid nested Input<string> deserialization inside a literal JSON array in Elsa 3.9.
            // Review tasks show the stage's configured actions (APPROVE, REJECT, then custom ones).
            ActionDefinitions = new(ctx => Parent(ctx).GetProperty<bool>("correction")
                ? [new UserTaskActionDefinition(TaskActionKeys.Resubmit, "Resubmit")]
                : ActionsOf(Parent(ctx))),
            // The form key carries the stage so completion validation knows which actions this task offers.
            Form = new(ctx => new UserTaskFormReference("configured-ticket",
                Parent(ctx).GetProperty<bool>("correction") ? "correction" : "review:" + Parent(ctx).GetProperty<string>("stageKey"),
                Parent(ctx).GetProperty<string>("schema")))
        };
    }
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var schema = ConfigurationJson.Get(context);
        var setup = ConfigJson.Read<WorkflowSetup>(schema);
        var stage = setup.Stages.Single(x => x.Key == StageKey.Get(context));
        var correction = Correction.Get(context);
        var parallelBranch = ParallelBranch.Get(context);
        // Assignment is resolved once per stage visit. CandidateUsers + Snapshot preserve that list
        // while Elsa owns the task's subsequent claim, assignee, revision and completion history.
        var assignment = correction
            ? new ResolvedAssignment([TicketState.Requester(context.WorkflowExecutionContext)], TicketState.Requester(context.WorkflowExecutionContext), false)
            : await context.GetRequiredService<AssignmentResolver>().ResolveAsync(stage.Assignment, TicketState.Attributes(context.WorkflowExecutionContext), context.CancellationToken);
        // Only this active activity's callback needs these properties. Final ticket data belongs in
        // workflow output instead; completed activity contexts may be removed by the runtime.
        context.SetProperty("assignment", ConfigJson.Write(assignment));
        context.SetProperty("schema", schema);
        context.SetProperty("stageKey", stage.Key);
        context.SetProperty("correction", correction);
        context.SetProperty("parallelBranch", parallelBranch);
        context.SetProperty("title", correction ? "Correct and resubmit: " + stage.Title : stage.Title);
        var tags = new List<string>();
        var users = context.GetRequiredService<IApproverDirectory>();
        if (!correction && stage.ViewableByStages is { Length: > 0 } viewerStages)
        {
            // These members may observe before their own stage exists. Snapshot their eligibility
            // at task creation without changing the native candidate or assignee lists.
            foreach (var member in setup.Stages.Where(other => viewerStages.Contains(other.Key))
                .SelectMany(other => other.Assignment.Members).Distinct(StringComparer.Ordinal))
                if (await users.HasRoleAsync(member, "Approver", context.CancellationToken))
                    tags.Add(ObservedTaskAccess.TagFor(TicketState.User(member)));
        }
        // Workflow supervisors see (and may reassign) every task of this workflow, including corrections.
        foreach (var supervisor in setup.Supervisors ?? [])
            if (await users.HasRoleAsync(supervisor, WorkflowSetup.SupervisorRole, context.CancellationToken))
                tags.Add(ObservedTaskAccess.SupervisorTagFor(TicketState.User(supervisor)));
        if (tags.Count > 0) context.SetProperty("observerTags", ConfigJson.Write(tags));
        if (!parallelBranch)
        {
            context.WorkflowExecutionContext.Output["currentStage"] = stage.Key;
            context.WorkflowExecutionContext.Output["ticketOutcome"] = (correction ? TicketOutcome.Returned : TicketOutcome.InReview).ToString();
        }
        await context.ScheduleActivityAsync(Review, OnCompletedAsync);
    }
    private async ValueTask OnCompletedAsync(ActivityCompletedContext completed)
    {
        var context = completed.TargetContext;
        var stimulus = context.GetWorkflowInput<UserTaskStimulus>(UserTask.InputKey);
        var parallelBranch = context.GetProperty<bool>("parallelBranch");
        if (!string.Equals(stimulus.ActionKey, TaskActionKeys.Resubmit, StringComparison.OrdinalIgnoreCase))
        {
            // A configured stage action. The form provider already checked it belongs to this stage.
            var action = StageOf(context)!.Actions!.First(x => string.Equals(x.Key, stimulus.ActionKey, StringComparison.OrdinalIgnoreCase));
            if (!parallelBranch)
            {
                context.ExpressionExecutionContext.SetVariable("lastAction", action.Key);
                context.WorkflowExecutionContext.Output["lastDecision"] = action.Key;
            }
            await context.CompleteActivityWithOutcomesAsync("Action");
            return;
        }
        // A correction task: the form provider already validated the payload. Revalidate at the execution boundary,
        // preserve the preceding snapshot, then expose a Resubmit outcome to the graph's configured edge.
        var attributes = stimulus.CompletionData!.Value.GetProperty("attributes").Deserialize<Dictionary<string, JsonElement>>()!;
        var schema = ConfigJson.Read<WorkflowSetup>(context.GetProperty<string>("schema")!);
        await new TicketInputValidator(schema).ValidateAndThrowAsync(attributes, context.CancellationToken);
        var output = context.WorkflowExecutionContext.Output;
        var revision = int.Parse(output["attributeRevision"].ToString()!) + 1;
        var storedHistory = JsonSerializer.SerializeToElement(output["attributeHistory"]);
        // Older Elsa property-bag serialization may wrap collections in $values.
        if (storedHistory.ValueKind == JsonValueKind.Object && storedHistory.TryGetProperty("$values", out var wrapped)) storedHistory = wrapped;
        var history = storedHistory.EnumerateArray().Select(x => x.Clone()).ToList();
        history.Add(JsonSerializer.SerializeToElement(new { revision, attributes }));
        output["attributeRevision"] = revision;
        output["attributeHistory"] = JsonSerializer.SerializeToElement(history);
        TicketState.SetAttributes(context, attributes, schema);
        await context.CompleteActivityWithOutcomesAsync("Resubmit");
    }
}
