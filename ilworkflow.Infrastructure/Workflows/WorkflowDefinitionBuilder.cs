namespace IlWorkflow.Infrastructure.Workflows;

/// <summary>
/// Turns a <see cref="WorkflowSetup"/> into the JSON of a normal Elsa flowchart (activities + connections).
/// Elsa then runs, saves and resumes that flowchart; this project has no workflow engine of its own.
///
/// Example: stage A with buttons APPROVE → B and REJECT → correction becomes
/// <code>
///   initialize ──Done──► stage_A ──APPROVE──► stage_B
///                           └──REJECT──► correction_A ──Resubmit──► stage_A
/// </code>
/// </summary>
public sealed class WorkflowDefinitionBuilder
{
    private const string StartNode = "initialize";
    private const string ApprovedNode = "approved";
    private const string RejectedNode = "rejected";

    public string Build(WorkflowSetup setup) => new Graph(setup).Build();

    /// <summary>Collects nodes and connections while walking the stages once.</summary>
    private sealed class Graph(WorkflowSetup setup)
    {
        private readonly List<object> _activities = [];
        private readonly List<object> _connections = [];
        // One correction node per (stage that sends back, stage to continue at after resubmit).
        private readonly Dictionary<(string From, string ResumeAt), string> _corrections = [];
        // The setup is embedded in each review node, so a running ticket never sees a later edit.
        private readonly string _snapshot = ConfigurationJson.Write(setup);

        public string Build()
        {
            _activities.Add(new { id = StartNode, type = "Tickets.InitializeTicket", version = 1 });
            _activities.Add(Finish(ApprovedNode, TicketOutcome.Approved));
            _activities.Add(Finish(RejectedNode, TicketOutcome.Rejected));
            Connect(StartNode, "Done", StageNode(setup.StartStage));

            var parallelMembers = setup.Stages.SelectMany(stage => stage.ParallelWith ?? []).ToHashSet();
            foreach (var stage in setup.Stages.Where(stage => !parallelMembers.Contains(stage.Key)))
            {
                if (stage.IsParallelGroup) AddParallelGroup(stage);
                else AddStage(stage);
            }

            return ConfigurationJson.Write(new
            {
                id = "ticket_graph", type = "Elsa.Flowchart", version = 1, start = StartNode,
                activities = _activities, connections = _connections
            });
        }

        /// <summary>A review node with one outgoing connection per button, named after the button key.</summary>
        private void AddStage(StageDefinition stage)
        {
            _activities.Add(Review(StageNode(stage.Key), stage.Key));
            foreach (var action in stage.Actions!)
                Connect(StageNode(stage.Key), action.Key, Target(action.Destination, stage.Key));
        }

        /// <summary>All stages of the group open at once; the group leaves through Approve or Reject.</summary>
        private void AddParallelGroup(StageDefinition entry)
        {
            var branchKeys = entry.ParallelWith!.Prepend(entry.Key);
            _activities.Add(new
            {
                id = StageNode(entry.Key), type = "Tickets.ParallelTicketReview", version = 1, stageKey = Literal(entry.Key),
                branches = branchKeys.Select(key => Review($"parallel-branch-{entry.Key}-{key}", key, parallel: true)).ToArray()
            });
            var approve = entry.Actions!.Single(action => action.Key == TaskActionKeys.Approve);
            var reject = entry.Actions!.Single(action => action.Key == TaskActionKeys.Reject);
            Connect(StageNode(entry.Key), StagePorts.Approve, Target(approve.Destination, entry.Key));
            Connect(StageNode(entry.Key), StagePorts.Reject, Target(reject.Destination, entry.Key));
        }

        /// <summary>The node a destination points to. Correction nodes are created on first use.</summary>
        private string Target(Destination destination, string fromStage) => destination.Kind switch
        {
            DestinationKind.Approved => ApprovedNode,
            DestinationKind.Rejected => RejectedNode,
            DestinationKind.Stage => StageNode(destination.StageKey!),
            DestinationKind.Correction => CorrectionNode(fromStage, destination.StageKey!),
            _ => throw new ArgumentOutOfRangeException(nameof(destination))
        };

        private string CorrectionNode(string fromStage, string resumeAt)
        {
            if (_corrections.TryGetValue((fromStage, resumeAt), out var existing)) return existing;

            var id = $"correction_{fromStage}_{resumeAt}";
            _corrections[(fromStage, resumeAt)] = id;
            _activities.Add(Review(id, fromStage, correction: true));
            Connect(id, StagePorts.Resubmit, StageNode(resumeAt));
            return id;
        }

        private object Review(string id, string stageKey, bool correction = false, bool parallel = false) => new
        {
            id, type = "Tickets.TicketReview", version = 1,
            configurationJson = Literal(_snapshot), stageKey = Literal(stageKey),
            correction = Literal(correction, "Boolean"), parallelBranch = Literal(parallel, "Boolean")
        };

        private static object Finish(string id, TicketOutcome outcome) =>
            new { id, type = "Tickets.FinishTicket", version = 1, outcome = Literal(outcome.ToString()) };

        private void Connect(string from, string port, string to) =>
            _connections.Add(new { source = new { activity = from, port }, target = new { activity = to, port = "In" } });

        private static string StageNode(string stageKey) => "stage_" + stageKey;

        /// <summary>A constant activity input in Elsa's JSON format.</summary>
        private static object Literal(object value, string type = "String") =>
            new { typeName = type, expression = new { type = "Literal", value } };
    }
}
