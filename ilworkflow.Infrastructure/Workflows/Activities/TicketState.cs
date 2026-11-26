using System.Text.Json;
using Elsa.Extensions;
using Elsa.UserTasks.Models;
using Elsa.Workflows;

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
