using System.Text.Json;
using Elsa.UserTasks.Models;
using Elsa.Workflows;

namespace IlWorkflow.Infrastructure.Workflows;

/// <summary>
/// Read/write helpers for ticket data kept in the workflow OUTPUT.
/// Workflow output is saved with the instance and survives restarts, so it is the single place for ticket state.
/// Clients read it from GET /elsa/api/workflow-instances/{id} (workflowState.output).
/// </summary>
public static class TicketState
{
    public const string Requester = "requesterId";
    public const string Outcome = "ticketOutcome";
    public const string CurrentStage = "currentStage";
    public const string LastDecision = "lastDecision";
    public const string Attributes = "attributes";
    public const string AttributeRevision = "attributeRevision";
    public const string AttributeHistory = "attributeHistory";

    public static string GetRequester(WorkflowExecutionContext workflow) => workflow.Output[Requester]?.ToString() ?? "";

    public static JsonElement GetAttributes(WorkflowExecutionContext workflow) =>
        JsonSerializer.SerializeToElement(workflow.Output[Attributes]);

    public static void SetOutcome(WorkflowExecutionContext workflow, TicketOutcome outcome) =>
        workflow.Output[Outcome] = outcome.ToString();

    /// <summary>Stores new attributes and appends them to the history as the next revision.</summary>
    public static void AddAttributeRevision(WorkflowExecutionContext workflow, JsonElement attributes)
    {
        var history = ReadHistory(workflow);
        var revision = history.Count + 1;
        history.Add(JsonSerializer.SerializeToElement(new { revision, attributes }));

        workflow.Output[Attributes] = attributes;
        workflow.Output[AttributeRevision] = revision;
        workflow.Output[AttributeHistory] = JsonSerializer.SerializeToElement(history);
    }

    /// <summary>A task participant in Elsa's format, from one of our user IDs.</summary>
    public static ParticipantReference User(string id) => new("", "default", UserTaskParticipantType.User, id);

    private static List<JsonElement> ReadHistory(WorkflowExecutionContext workflow)
    {
        if (!workflow.Output.TryGetValue(AttributeHistory, out var stored) || stored is null) return [];
        var json = JsonSerializer.SerializeToElement(stored);
        // Elsa may store collections wrapped as { "$values": [...] }.
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("$values", out var values)) json = values;
        return json.EnumerateArray().Select(item => item.Clone()).ToList();
    }
}
