using static IlWorkflow.UnitTests.SetupFactory;

namespace IlWorkflow.UnitTests.Core;

public class WorkflowConfigurationValidatorTests
{
    private static async Task<string[]> ErrorsAsync(WorkflowSetup setup, params string[] approvers)
    {
        var result = await new WorkflowConfigurationValidator(new FakeDirectory(approvers.Length == 0 ? ["alice", "bob", "pia", "quinn"] : approvers)).ValidateAsync(setup);
        return result.Errors.Select(error => error.ErrorMessage).ToArray();
    }

    [Fact]
    public async Task A_consistent_setup_is_valid() => Assert.Empty(await ErrorsAsync(TwoStages()));

    [Fact]
    public async Task Unknown_start_stage_is_rejected() =>
        Assert.Contains(await ErrorsAsync(TwoStages() with { StartStage = "Z" }), e => e.Contains("Start stage 'Z'"));

    [Fact]
    public async Task Duplicate_stage_keys_are_rejected()
    {
        var setup = TwoStages();
        var errors = await ErrorsAsync(setup with { Stages = [setup.Stages[0], setup.Stages[0]] });
        Assert.Contains(errors, e => e.Contains("used more than once"));
    }

    [Fact]
    public async Task A_stage_without_actions_is_rejected()
    {
        var setup = TwoStages() with { Stages = [Stage("A", ["alice"], [])] };
        Assert.Contains(await ErrorsAsync(setup), e => e.Contains("needs at least one action"));
    }

    [Fact]
    public async Task Resubmit_is_reserved_for_correction_tasks()
    {
        var setup = TwoStages() with { Stages = [Stage("A", ["alice"], [Action("RESUBMIT", DestinationKind.Approved)])] };
        Assert.Contains(await ErrorsAsync(setup), e => e.Contains("reserved"));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("2FAST")]
    public async Task Action_keys_must_be_upper_case(string key)
    {
        var setup = TwoStages() with { Stages = [Stage("A", ["alice"], [Action(key, DestinationKind.Approved)])] };
        Assert.Contains(await ErrorsAsync(setup), e => e.Contains("UPPER_CASE"));
    }

    [Fact]
    public async Task A_destination_must_point_to_an_existing_stage()
    {
        var setup = TwoStages() with { Stages = [Stage("A", ["alice"], [Action("APPROVE", DestinationKind.Stage, "NOPE")])] };
        Assert.Contains(await ErrorsAsync(setup), e => e.Contains("invalid destination"));
    }

    [Fact]
    public async Task Members_need_the_approver_role() =>
        Assert.Contains(await ErrorsAsync(TwoStages(), "alice"), e => e.Contains("'bob'") && e.Contains("Approver"));

    [Fact]
    public async Task A_specific_stage_with_several_members_needs_a_user_id()
    {
        var setup = TwoStages() with { Stages = [Stage("A", ["alice", "bob"], [Action("APPROVE", DestinationKind.Approved)], AssignmentPolicy.Specific)] };
        Assert.Contains(await ErrorsAsync(setup), e => e.Contains("needs user_id"));
    }

    [Fact]
    public async Task Supervisors_need_the_supervisor_role()
    {
        var errors = await ErrorsAsync(TwoStages() with { Supervisors = ["nobody"] });
        Assert.Contains(errors, e => e.Contains("Supervisor 'nobody'"));
        Assert.Empty(await ErrorsAsync(TwoStages() with { Supervisors = ["sam"] }));
    }

    private static StageAction[] ApproveReject(DestinationKind approve = DestinationKind.Approved) =>
        [Action("APPROVE", approve), Action("REJECT", DestinationKind.Rejected, reason: true)];

    [Fact]
    public async Task A_parallel_group_of_approve_and_reject_stages_is_valid()
    {
        var setup = new WorkflowSetup("Parallel", "P", [Stage("P", ["pia"], ApproveReject(), parallelWith: ["Q"]), Stage("Q", ["quinn"], ApproveReject())]);
        Assert.Empty(await ErrorsAsync(setup));
    }

    [Fact]
    public async Task Parallel_stages_must_offer_exactly_approve_and_reject()
    {
        var custom = new[] { Action("APPROVE", DestinationKind.Approved), Action("REFER", DestinationKind.Rejected) };
        var setup = new WorkflowSetup("Parallel", "P", [Stage("P", ["pia"], ApproveReject(), parallelWith: ["Q"]), Stage("Q", ["quinn"], custom)]);
        Assert.Contains(await ErrorsAsync(setup), e => e.Contains("exactly the APPROVE and REJECT"));
    }

    [Fact]
    public async Task Parallel_stages_must_share_destinations()
    {
        var setup = new WorkflowSetup("Parallel", "P",
        [
            Stage("P", ["pia"], ApproveReject(), parallelWith: ["Q"]),
            Stage("Q", ["quinn"], [Action("APPROVE", DestinationKind.Rejected), Action("REJECT", DestinationKind.Rejected)])
        ]);
        Assert.Contains(await ErrorsAsync(setup), e => e.Contains("same destinations"));
    }

    [Fact]
    public async Task The_start_stage_cannot_be_a_group_member()
    {
        var setup = new WorkflowSetup("Parallel", "Q", [Stage("P", ["pia"], ApproveReject(), parallelWith: ["Q"]), Stage("Q", ["quinn"], ApproveReject())]);
        Assert.Contains(await ErrorsAsync(setup), e => e.Contains("entry stage"));
    }
}
