using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using FluentValidation;
using ConfigJson = IlWorkflow.Application.Configuration.ConfigurationJson;

namespace IlWorkflow.Api.Workflows;

[Activity("Tickets", "Tickets", "Initializes a validated ticket snapshot.")]
public sealed class InitializeTicket : Activity
{
    [Input] public Input<string> ConfigurationJson { get; set; } = null!;
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var setup = ConfigJson.Read<WorkflowSetup>(ConfigurationJson.Get(context));
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
