using System.Text.Json;
using IlWorkflow.Infrastructure.Workflows;
using static IlWorkflow.UnitTests.SetupFactory;

namespace IlWorkflow.UnitTests.Infrastructure;

public class WorkflowDefinitionBuilderTests
{
    /// <summary>The generated flowchart as "from:port -> to" strings, plus the node IDs and types.</summary>
    private sealed record Graph(string[] Connections, Dictionary<string, string> Nodes);

    private static Graph Build(WorkflowSetup setup)
    {
        using var json = JsonDocument.Parse(new WorkflowDefinitionBuilder().Build(setup));
        var root = json.RootElement;
        var connections = root.GetProperty("connections").EnumerateArray().Select(c =>
            $"{c.GetProperty("source").GetProperty("activity").GetString()}:{c.GetProperty("source").GetProperty("port").GetString()} -> {c.GetProperty("target").GetProperty("activity").GetString()}").ToArray();
        var nodes = root.GetProperty("activities").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!, a => a.GetProperty("type").GetString()!);
        return new(connections, nodes);
    }

    [Fact]
    public void Each_action_key_is_a_port_wired_to_its_destination()
    {
        var graph = Build(TwoStages());
        Assert.Contains("initialize:Done -> stage_A", graph.Connections);
        Assert.Contains("stage_A:APPROVE -> stage_B", graph.Connections);
        Assert.Contains("stage_B:APPROVE -> approved", graph.Connections);
        Assert.Contains("stage_B:REJECT -> rejected", graph.Connections);
    }

    [Fact]
    public void A_correction_destination_adds_a_node_that_returns_to_the_stage()
    {
        var graph = Build(TwoStages());
        Assert.Contains("stage_A:REJECT -> correction_A_A", graph.Connections);
        Assert.Contains("correction_A_A:Resubmit -> stage_A", graph.Connections);
        Assert.Equal("Tickets.TicketReview", graph.Nodes["correction_A_A"]);
    }

    [Fact]
    public void Two_actions_with_the_same_correction_share_one_node()
    {
        var setup = new WorkflowSetup("Shared", "A", [Stage("A", ["alice"],
        [
            Action("REJECT", DestinationKind.Correction, "A"), Action("RETURN", DestinationKind.Correction, "A"), Action("APPROVE", DestinationKind.Approved)
        ])]);
        var graph = Build(setup);
        Assert.Single(graph.Nodes.Keys, id => id.StartsWith("correction_"));
    }

    [Fact]
    public void Custom_actions_work_like_any_other()
    {
        var setup = new WorkflowSetup("Custom", "A",
        [
            Stage("A", ["alice"], [Action("REFER_UW", DestinationKind.Stage, "B"), Action("APPROVE", DestinationKind.Approved)]),
            Stage("B", ["bob"], [Action("APPROVE", DestinationKind.Approved)])
        ]);
        Assert.Contains("stage_A:REFER_UW -> stage_B", Build(setup).Connections);
    }

    [Fact]
    public void A_parallel_group_becomes_one_node_with_approve_and_reject_ports()
    {
        StageAction[] actions = [Action("APPROVE", DestinationKind.Approved), Action("REJECT", DestinationKind.Rejected)];
        var setup = new WorkflowSetup("Parallel", "P", [Stage("P", ["pia"], actions, parallelWith: ["Q"]), Stage("Q", ["quinn"], actions)]);
        var graph = Build(setup);
        Assert.Equal("Tickets.ParallelTicketReview", graph.Nodes["stage_P"]);
        Assert.DoesNotContain("stage_Q", graph.Nodes.Keys);
        Assert.Contains("stage_P:Approve -> approved", graph.Connections);
        Assert.Contains("stage_P:Reject -> rejected", graph.Connections);
    }

    [Fact]
    public void The_graph_has_no_javascript_decisions()
    {
        var json = new WorkflowDefinitionBuilder().Build(TwoStages());
        Assert.DoesNotContain("JavaScript", json);
        Assert.DoesNotContain("FlowDecision", json);
    }

    [Fact]
    public void Every_review_node_embeds_the_setup_it_was_built_from()
    {
        var json = new WorkflowDefinitionBuilder().Build(TwoStages());
        Assert.Contains("\"configurationJson\"", json);
        Assert.Contains("Expense", json);
    }
}
