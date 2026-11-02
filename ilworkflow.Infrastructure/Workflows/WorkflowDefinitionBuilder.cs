using System.Text.Json;
using IlWorkflow.Infrastructure.Configuration;

namespace IlWorkflow.Api.Workflows;

/// <summary>Compiles typed setup into a normal Elsa flowchart. No runtime graph interpreter.</summary>
public sealed class WorkflowDefinitionBuilder
{
    public string Build(WorkflowSetup setup)
    {
        var activities = new List<object>();
        var connections = new List<object>();
        // Embed an immutable setup snapshot in the definition. An existing instance must never read
        // a newer editable database draft when it resumes days later.
        var snapshot = ConfigurationJson.Write(setup);
        object Literal(object value, string type = "String") => new { typeName = type, expression = new { type = "Literal", value } };
        void Link(string source, string port, string target) => connections.Add(new
        {
            source = new { activity = source, port }, target = new { activity = target, port = "In" }
        });
        activities.Add(new { id = "initialize", type = "Tickets.InitializeTicket", version = 1, configurationJson = Literal(snapshot) });
        activities.Add(new { id = "approved", type = "Tickets.FinishTicket", version = 1, outcome = Literal(nameof(TicketOutcome.Approved)) });
        activities.Add(new { id = "rejected", type = "Tickets.FinishTicket", version = 1, outcome = Literal(nameof(TicketOutcome.Rejected)) });
        // Correction nodes are explicit graph nodes with a fixed return edge. They are not a second
        // interpreter or a client-supplied destination. Each rejecting stage gets its own correction node.
        string Target(Destination destination, string origin)
        {
            if (destination.Kind == DestinationKind.Approved) return "approved";
            if (destination.Kind == DestinationKind.Rejected) return "rejected";
            if (destination.Kind == DestinationKind.Stage) return "stage_" + destination.StageKey;
            var correctionId = "correction_" + origin;
            activities.Add(new { id = correctionId, type = "Tickets.TicketReview", version = 1,
                configurationJson = Literal(snapshot), stageKey = Literal(origin), correction = Literal(true, "Boolean") });
            Link(correctionId, nameof(TaskDecision.Resubmit), "stage_" + destination.StageKey);
            return correctionId;
        }
        Link("initialize", "Done", "stage_" + setup.StartStage);
        foreach (var stage in setup.Stages)
        {
            var id = "stage_" + stage.Key;
            activities.Add(new { id, type = "Tickets.TicketReview", version = 1,
                configurationJson = Literal(snapshot), stageKey = Literal(stage.Key), correction = Literal(false, "Boolean") });
            Link(id, nameof(TaskDecision.Reject), Target(stage.RejectTo, stage.Key));
            var rules = stage.ApproveRules ?? [];
            Link(id, nameof(TaskDecision.Approve), rules.Length == 0 ? Target(stage.ApproveTo, stage.Key) : "rule_" + stage.Key + "_0");
            for (var i = 0; i < rules.Length; i++)
            {
                var ruleId = "rule_" + stage.Key + "_" + i;
                // Read the persisted result of the shared typed comparison. JavaScript must not
                // round decimal/Int64 values or treat an omitted field as an inherited object property.
                var ruleKey = JsonSerializer.Serialize(stage.Key + "_" + i);
                activities.Add(new { id = ruleId, type = "Elsa.FlowDecision", version = 1,
                    condition = new { typeName = "Boolean", expression = new { type = "JavaScript",
                        value = $"JSON.parse(getVariable('attributesJson'))['$routes'][{ruleKey}] === true" } } });
                Link(ruleId, "True", Target(rules[i].Destination, stage.Key));
                Link(ruleId, "False", i + 1 < rules.Length ? "rule_" + stage.Key + "_" + (i + 1) : Target(stage.ApproveTo, stage.Key));
            }
        }
        return ConfigurationJson.Write(new { id = "ticket_graph", type = "Elsa.Flowchart", version = 1, start = "initialize",
            activities, connections, variables = new[] { new { id = "attributesJson", name = "attributesJson", typeName = "String",
                storageDriverTypeName = "Elsa.Workflows.WorkflowInstanceStorageDriver, Elsa.Workflows.Core" } } });
    }
}
