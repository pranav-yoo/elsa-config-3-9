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
using IlWorkflow.Infrastructure.Configuration;
using UserTask = Elsa.UserTasks.Activities.UserTask;

namespace IlWorkflow.Api.Workflows;

/// <summary>State helpers operate on the persisted workflow output, not short-lived activity properties.</summary>
public static class TicketState
{
    public static Dictionary<string, JsonElement> Attributes(WorkflowExecutionContext context) =>
        ConfigurationJson.Read<Dictionary<string, JsonElement>>(ConfigurationJson.Write(context.Output["attributes"]));
    public static string Requester(WorkflowExecutionContext context) => context.Output["requesterId"]?.ToString() ?? "";
    public static ParticipantReference User(string id) => new("", "default", UserTaskParticipantType.User, id);
    public static void SetAttributes(ActivityExecutionContext context, Dictionary<string, JsonElement> values, WorkflowSetup setup)
    {
        // Workflow output survives pruning of completed activity contexts. The flowchart variable is
        // persisted alongside typed route results, recalculated whenever attributes change.
        context.WorkflowExecutionContext.Output["attributes"] = values;
        var routes = new Dictionary<string, bool>();
        foreach (var stage in setup.Stages)
            foreach (var action in stage.Actions ?? [])
                for (var index = 0; index < (action.Rules?.Length ?? 0); index++)
                    routes[StageAction.RouteKey(stage.Key, action.Key, index)] = AttributeRules.Matches(action.Rules![index].Conditions, values);
        var snapshot = JsonSerializer.SerializeToNode(values)!;
        snapshot["$routes"] = JsonSerializer.SerializeToNode(routes);
        // Keep the original fields for existing persisted graphs. New graphs read only $routes;
        // public attributes/task data stay free of this internal metadata.
        context.ExpressionExecutionContext.SetVariable("attributesJson", snapshot.ToJsonString());
    }
}

[Activity("Tickets", "Tickets", "Initializes a validated ticket snapshot.")]
public sealed class InitializeTicket : Activity
{
    [Input] public Input<string> ConfigurationJson { get; set; } = null!;
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var setup = IlWorkflow.Application.Configuration.ConfigurationJson.Read<WorkflowSetup>(ConfigurationJson.Get(context));
        var values = context.GetWorkflowInput<Dictionary<string, JsonElement>>("attributes");
        await new TicketInputValidator(setup).ValidateAndThrowAsync(values, context.CancellationToken);
        var requester = context.GetWorkflowInput<string>("requesterId");
        if (string.IsNullOrWhiteSpace(requester)) throw new InvalidOperationException("Requester must be supplied by the authenticated execute guard.");
        var output = context.WorkflowExecutionContext.Output;
        output["requesterId"] = requester;
        output["ticketOutcome"] = TicketOutcome.InReview.ToString();
        output["attributeRevision"] = 1;
        output["attributeHistory"] = JsonSerializer.SerializeToElement(new[] { new { revision = 1, attributes = values } });
        TicketState.SetAttributes(context, values, setup);
        await context.CompleteActivityAsync();
    }
}

[Activity("Tickets", "Tickets", "Schedules one native approval or correction task.")]
[FlowNode("Approve", "Reject", "Resubmit", "Custom")]
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
            ? IlWorkflow.Application.Configuration.ConfigurationJson.Read<WorkflowSetup>(schema).Stages.FirstOrDefault(x => x.Key == key)
            : null;
    private static readonly UserTaskActionDefinition[] BuiltInActions =
        [new(TaskActionKeys.Approve, "Approve"), new(TaskActionKeys.Reject, "Reject")];
    private static UserTaskActionDefinition[] ActionsOf(ActivityExecutionContext context) =>
        StageOf(context)?.Actions?.Select(x => new UserTaskActionDefinition(x.Key, x.DisplayLabel)).ToArray() ?? BuiltInActions;
    private static ResolvedAssignment Assignment(ExpressionExecutionContext context) => IlWorkflow.Application.Configuration.ConfigurationJson.Read<ResolvedAssignment>(Parent(context).GetProperty<string>("assignment")!);
    public TicketReview()
    {
        Review = new UserTask
        {
            Id = "review",
            Title = new(ctx => Parent(ctx).GetProperty<string>("title")!),
            Summary = new(ctx => Assignment(ctx).Blocked ? "Assignment blocked. Supervisor reassignment required." : "Review this request."),
            TaskType = new(ctx => Parent(ctx).GetProperty<bool>("parallelBranch") ? ParallelTicketReview.TaskType : "configured-ticket"),
            Tags = new(ctx => Parent(ctx).GetProperty<string>("observerTags") is { } tags
                ? IlWorkflow.Application.Configuration.ConfigurationJson.Read<string[]>(tags) : []),
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
        var setup = IlWorkflow.Application.Configuration.ConfigurationJson.Read<WorkflowSetup>(schema);
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
        context.SetProperty("assignment", IlWorkflow.Application.Configuration.ConfigurationJson.Write(assignment));
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
        if (tags.Count > 0) context.SetProperty("observerTags", IlWorkflow.Application.Configuration.ConfigurationJson.Write(tags));
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
        var decision = TaskActionKeys.Parse(stimulus.ActionKey);
        if (decision is null)
        {
            // A configured custom action (e.g. REFER_UW). The form provider already checked it belongs to this stage.
            // Older pending tasks have no stageKey property and only offer the built-in actions.
            var custom = StageOf(context)?.CustomActions.FirstOrDefault(x => string.Equals(x.Key, stimulus.ActionKey, StringComparison.OrdinalIgnoreCase));
            if (custom is null) throw new InvalidOperationException("Unknown task decision.");
            context.ExpressionExecutionContext.SetVariable("lastAction", custom.Key);
            context.WorkflowExecutionContext.Output["lastDecision"] = custom.Key;
            await context.CompleteActivityWithOutcomesAsync(nameof(TaskDecision.Custom));
            return;
        }
        // The task form provider already validated the payload. Revalidate at the execution boundary,
        // preserve the preceding snapshot, then expose a Resubmit outcome to the graph's configured edge.
        if (decision == TaskDecision.Resubmit)
        {
            var attributes = stimulus.CompletionData!.Value.GetProperty("attributes").Deserialize<Dictionary<string, JsonElement>>()!;
            var schema = IlWorkflow.Application.Configuration.ConfigurationJson.Read<WorkflowSetup>(context.GetProperty<string>("schema")!);
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
        }
        var outcome = decision.Value.ToString();
        if (!context.GetProperty<bool>("parallelBranch"))
            context.WorkflowExecutionContext.Output["lastDecision"] = outcome;
        // Persisted graphs use enum names for ports, independently of the public task action keys.
        await context.CompleteActivityWithOutcomesAsync(outcome);
    }
}

[Activity("Tickets", "Tickets", "Records an explicit business result while Elsa owns technical status.")]
public sealed class FinishTicket : Activity
{
    [Input] public Input<string> Outcome { get; set; } = null!;
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var outcome = Enum.Parse<TicketOutcome>(Outcome.Get(context));
        if (outcome is not (TicketOutcome.Approved or TicketOutcome.Rejected)) throw new InvalidOperationException("A terminal outcome is required.");
        context.WorkflowExecutionContext.Output["ticketOutcome"] = outcome.ToString();
        context.WorkflowExecutionContext.Output["currentStage"] = null!;
        await context.CompleteActivityAsync();
    }
}
