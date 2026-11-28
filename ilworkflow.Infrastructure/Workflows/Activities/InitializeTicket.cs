using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;

namespace IlWorkflow.Infrastructure.Workflows;

/// <summary>First node of every generated workflow: records who submitted the ticket and its attributes.</summary>
[Activity("Tickets", "Tickets", "Initializes a ticket from the workflow input.")]
public sealed class InitializeTicket : Activity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        // WorkflowRequestGuard has already validated the request and set requesterId from the caller's token.
        var requester = context.GetWorkflowInput<string>(TicketState.Requester);
        if (string.IsNullOrWhiteSpace(requester))
            throw new InvalidOperationException("requesterId must be set by the execute guard.");

        var values = context.GetWorkflowInput<Dictionary<string, JsonElement>>(TicketState.Attributes) ?? [];
        var attributes = JsonSerializer.SerializeToElement(values);

        var workflow = context.WorkflowExecutionContext;
        workflow.Output[TicketState.Requester] = requester;
        TicketState.SetOutcome(workflow, TicketOutcome.InReview);
        TicketState.AddAttributeRevision(workflow, attributes);

        await context.CompleteActivityAsync();
    }
}
