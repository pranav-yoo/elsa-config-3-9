using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace IlWorkflow.Api.Workflows;

[Activity("Tickets", "Tickets", "Records an explicit business result while Elsa owns technical status.")]
public sealed class FinishTicket : Activity
{
    [Input] public Input<string> Outcome { get; set; } = null!;
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var outcome = Enum.Parse<TicketOutcome>(Outcome.Get(context));
        context.WorkflowExecutionContext.Output["ticketOutcome"] = outcome.ToString();
        context.WorkflowExecutionContext.Output["currentStage"] = null!;
        await context.CompleteActivityAsync();
    }
}
