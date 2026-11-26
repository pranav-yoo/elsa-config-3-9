using System.Text.Json;

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
        object Review(string id, string key, bool correction = false, bool parallel = false) => new
        {
            id, type = "Tickets.TicketReview", version = 1, configurationJson = Literal(snapshot),
            stageKey = Literal(key), correction = Literal(correction, "Boolean"), parallelBranch = Literal(parallel, "Boolean")
        };
        activities.Add(new { id = "initialize", type = "Tickets.InitializeTicket", version = 1, configurationJson = Literal(snapshot) });
        activities.Add(new { id = "approved", type = "Tickets.FinishTicket", version = 1, outcome = Literal(nameof(TicketOutcome.Approved)) });
        activities.Add(new { id = "rejected", type = "Tickets.FinishTicket", version = 1, outcome = Literal(nameof(TicketOutcome.Rejected)) });
        // Correction nodes are explicit graph nodes with a fixed return edge. They are not a second
        // interpreter or a client-supplied destination. One node per (origin stage, return stage), so
        // several actions of a stage can send the ticket to the same correction.
        var corrections = new Dictionary<(string Origin, string Return), string>();
        string Target(Destination destination, string origin)
        {
            if (destination.Kind == DestinationKind.Approved) return "approved";
            if (destination.Kind == DestinationKind.Rejected) return "rejected";
            if (destination.Kind == DestinationKind.Stage) return "stage_" + destination.StageKey;
            if (corrections.TryGetValue((origin, destination.StageKey!), out var existing)) return existing;
            var correctionId = corrections.Keys.Any(x => x.Origin == origin) ? $"correction_{origin}_{destination.StageKey}" : "correction_" + origin;
            corrections[(origin, destination.StageKey!)] = correctionId;
            activities.Add(Review(correctionId, origin, correction: true));
            Link(correctionId, nameof(TaskDecision.Resubmit), "stage_" + destination.StageKey);
            return correctionId;
        }
        // An action's rules become a chain of FlowDecision nodes reading the persisted typed comparison
        // results. JavaScript must not round decimal/Int64 values or treat an omitted field as an
        // inherited object property. Returns the node the action's outcome should connect to.
        string ActionEntry(StageDefinition stage, StageAction action)
        {
            var rules = action.Rules ?? [];
            string RuleId(int i) => action.Key == TaskActionKeys.Approve ? $"rule_{stage.Key}_{i}" : $"rule_{stage.Key}_{action.Key}_{i}";
            for (var i = 0; i < rules.Length; i++)
            {
                var routeKey = JsonSerializer.Serialize(StageAction.RouteKey(stage.Key, action.Key, i));
                activities.Add(new { id = RuleId(i), type = "Elsa.FlowDecision", version = 1,
                    condition = new { typeName = "Boolean", expression = new { type = "JavaScript",
                        value = $"JSON.parse(getVariable('attributesJson'))['$routes'][{routeKey}] === true" } } });
                Link(RuleId(i), "True", Target(rules[i].Destination, stage.Key));
                Link(RuleId(i), "False", i + 1 < rules.Length ? RuleId(i + 1) : Target(action.Destination, stage.Key));
            }
            return rules.Length == 0 ? Target(action.Destination, stage.Key) : RuleId(0);
        }
        Link("initialize", "Done", "stage_" + setup.StartStage);
        var parallelMembers = setup.Stages.SelectMany(stage => stage.ParallelWith ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var stage in setup.Stages.Where(stage => !parallelMembers.Contains(stage.Key)))
        {
            var id = "stage_" + stage.Key;
            if (stage.ParallelWith is { } members)
                activities.Add(new { id, type = "Tickets.ParallelTicketReview", version = 1, stageKey = Literal(stage.Key),
                    branches = members.Prepend(stage.Key).Select(key => Review($"parallel-branch-{stage.Key}-{key}", key, parallel: true)).ToArray() });
            else
                activities.Add(Review(id, stage.Key));
            Link(id, nameof(TaskDecision.Reject), ActionEntry(stage, stage.Reject!));
            Link(id, nameof(TaskDecision.Approve), ActionEntry(stage, stage.Approve!));
            // Custom actions share one "Custom" port; the activity records the chosen key in lastAction
            // and a FlowDecision chain sends it to that action's rules/destination.
            var custom = stage.CustomActions;
            for (var i = 0; i < custom.Length; i++)
            {
                var nodeId = $"action_{stage.Key}_{i}";
                if (i == 0) Link(id, nameof(TaskDecision.Custom), nodeId);
                activities.Add(new { id = nodeId, type = "Elsa.FlowDecision", version = 1,
                    condition = new { typeName = "Boolean", expression = new { type = "JavaScript",
                        value = $"getVariable('lastAction') === {JsonSerializer.Serialize(custom[i].Key)}" } } });
                Link(nodeId, "True", ActionEntry(stage, custom[i]));
                // Unreachable for validated input (the form provider only accepts configured keys); fail closed.
                Link(nodeId, "False", i + 1 < custom.Length ? $"action_{stage.Key}_{i + 1}" : "rejected");
            }
        }
        const string storageDriver = "Elsa.Workflows.WorkflowInstanceStorageDriver, Elsa.Workflows.Core";
        return ConfigurationJson.Write(new { id = "ticket_graph", type = "Elsa.Flowchart", version = 1, start = "initialize",
            activities, connections, variables = new[]
            {
                new { id = "attributesJson", name = "attributesJson", typeName = "String", storageDriverTypeName = storageDriver },
                new { id = "lastAction", name = "lastAction", typeName = "String", storageDriverTypeName = storageDriver }
            } });
    }
}
