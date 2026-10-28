using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Expressions.Models;
using Elsa.Identity.Contracts;
using Elsa.Identity.Models;
using UserTask = Elsa.UserTasks.Activities.UserTask;
using Elsa.UserTasks.Activities;
using Elsa.UserTasks.Models;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace IlWorkflow.Api.Tickets;

[Activity("Tickets", "Tickets", "Interprets configurationJson and schedules native Elsa User Tasks.")]
public sealed class TicketFlow : Activity
{
    [Input] public Input<string> ConfigurationJson { get; set; } = null!;
    [JsonIgnore, Port("Review")] public UserTask Review { get; set; }

    private static ActivityExecutionContext Parent(ExpressionExecutionContext context) => context.GetActivityExecutionContext().ParentActivityExecutionContext!;
    private static StageTask State(ExpressionExecutionContext context) => TicketJson.Read<StageTask>(Parent(context).GetProperty<string>("stageTask")!);
    private static ParticipantReference User(string id) => new("", "default", UserTaskParticipantType.User, id);

    public TicketFlow()
    {
        Review = new UserTask
        {
            Id = "review",
            Title = new(ctx => State(ctx).Title),
            Summary = new(ctx => State(ctx).Blocked ? "No eligible assignee. Supervisor assignment required." : "Complete the configured stage action."),
            Reference = new(ctx => Parent(ctx).WorkflowExecutionContext.CorrelationId),
            TaskType = new("ticket"),
            Requester = new(ctx => User(Parent(ctx).GetProperty<string>("requester")!)),
            Assignee = new(ctx => State(ctx).Assignee is { } id ? User(id) : null),
            CandidateUsers = new(ctx => State(ctx).Candidates.Select(User).ToArray()),
            MembershipResolutionMode = new(UserTaskMembershipResolutionMode.Snapshot),
            AllowManagerExclusionOverride = new(true),
            TaskData = new(ctx => JsonDocument.Parse(Parent(ctx).GetProperty<string>("attributes")!).RootElement.Clone()),
            Form = new(ctx => new UserTaskFormReference("ticket-json", State(ctx).Correction ? "correction" : "review", Parent(ctx).GetProperty<string>("configuration"))),
            ActionDefinitions = new(ctx => State(ctx).Correction
                ? [new UserTaskActionDefinition("Resubmit", "Resubmit corrections")]
                : [new UserTaskActionDefinition("Approve", "Approve"), new UserTaskActionDefinition("Reject", "Reject")])
        };
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var configuration = ConfigurationJson.Get(context);
        var ticket = TicketJson.Read<TicketConfiguration>(configuration);
        var users = context.GetRequiredService<TicketIdentity>();
        await TicketJson.ValidateConfigurationAsync(ticket, users, context.CancellationToken);
        var attributes = ticket.Definition.Attributes.ToDictionary(f => f.Key, f => JsonSerializer.SerializeToElement(context.WorkflowExecutionContext.Input.GetValueOrDefault(f.Key)));
        TicketJson.ValidateAttributes(ticket.Definition, attributes);
        var requester = context.GetWorkflowInput<string>("requesterId");
        var actor = context.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
        var actorId = actor?.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor?.FindFirstValue("sub");
        TicketJson.Require(actor?.Identity?.IsAuthenticated == true && (actorId == requester || actor.IsInRole("admin")), "Submit only for your own user ID using synchronous execute.");
        TicketJson.Require(await users.HasRoleAsync(requester, "Requester", context.CancellationToken), "Unknown requester.");
        context.SetProperty("configuration", configuration);
        context.SetProperty("attributes", TicketJson.Write(attributes));
        context.SetProperty("requester", requester);
        context.SetProperty("stage", ticket.Definition.StartStage);
        context.SetProperty("visit", 0);
        await Schedule(context, false);
    }

    private async ValueTask Schedule(ActivityExecutionContext context, bool correction)
    {
        var ticket = TicketJson.Read<TicketConfiguration>(context.GetProperty<string>("configuration")!);
        var stageKey = context.GetProperty<string>("stage")!;
        if (stageKey is "$completed" or "$rejected")
        {
            var status = stageKey == "$completed" ? "Completed" : "Rejected";
            context.SetProperty("ticketStatus", status);
            context.WorkflowExecutionContext.Output["ticketStatus"] = status;
            context.WorkflowExecutionContext.Output["configurationVersion"] = ticket.Version;
            await context.CompleteActivityAsync();
            return;
        }
        var visit = context.GetProperty<int>("visit") + 1;
        context.SetProperty("visit", visit);
        StageTask state;
        if (correction)
        {
            var requester = context.GetProperty<string>("requester")!;
            state = new("$requester", "Correct and resubmit " + ticket.Definition.Name, [requester], requester, true, false);
        }
        else
        {
            var stage = ticket.Definition.Stages.Single(s => s.Key == stageKey);
            var attributes = TicketJson.Read<Dictionary<string, JsonElement>>(context.GetProperty<string>("attributes")!);
            var assignee = stage.Assignment.Policy switch
            {
                "specific" => stage.Assignment.UserId,
                "condition" => (stage.Assignment.Rules ?? []).FirstOrDefault(r => TicketJson.Matches(r.When, attributes))?.UserId,
                _ => null
            };
            var blocked = stage.Assignment.Members.Length == 0 || stage.Assignment.Policy != "bucket" && assignee is null;
            state = new(stage.Key, stage.Name, blocked ? [] : stage.Assignment.Members, assignee, false, blocked);
        }
        context.SetProperty("stageTask", TicketJson.Write(state));
        context.SetProperty("ticketStatus", correction ? "Returned" : state.Blocked ? "AssignmentBlocked" : "InReview");
        context.GetRequiredService<ILogger<TicketFlow>>().LogInformation("WorkflowId={WorkflowId} Stage={Stage} Visit={Visit} AssignmentBlocked={Blocked}", context.WorkflowExecutionContext.Id, state.Key, visit, state.Blocked);
        await context.ScheduleActivityAsync(Review, OnCompleted);
    }

    private async ValueTask OnCompleted(ActivityCompletedContext completed)
    {
        var context = completed.TargetContext;
        var stimulus = context.GetWorkflowInput<UserTaskStimulus>(UserTask.InputKey);
        var ticket = TicketJson.Read<TicketConfiguration>(context.GetProperty<string>("configuration")!);
        var state = TicketJson.Read<StageTask>(context.GetProperty<string>("stageTask")!);
        if (state.Correction)
        {
            TicketJson.Require(stimulus.ActionKey == "Resubmit", "Expected Resubmit.");
            var attributes = stimulus.CompletionData!.Value.GetProperty("attributes").Deserialize<Dictionary<string, JsonElement>>(TicketJson.Options)!;
            TicketJson.ValidateAttributes(ticket.Definition, attributes);
            context.SetProperty("attributes", TicketJson.Write(attributes));
            await Schedule(context, false);
            return;
        }
        var stage = ticket.Definition.Stages.Single(s => s.Key == state.Key);
        var values = TicketJson.Read<Dictionary<string, JsonElement>>(context.GetProperty<string>("attributes")!);
        var destination = stimulus.ActionKey == "Approve"
            ? (stage.ApproveRules ?? []).FirstOrDefault(r => TicketJson.Matches(r.When, values))?.Destination ?? stage.ApproveTo
            : stage.RejectTo;
        if (destination != "$requester") context.SetProperty("stage", destination);
        await Schedule(context, destination == "$requester");
    }
}
