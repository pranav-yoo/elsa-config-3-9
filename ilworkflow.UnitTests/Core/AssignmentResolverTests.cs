namespace IlWorkflow.UnitTests.Core;

public class AssignmentResolverTests
{
    private static Task<ResolvedAssignment> ResolveAsync(AssignmentDefinition assignment, params string[] approvers) =>
        new AssignmentResolver(new FakeDirectory(approvers)).ResolveAsync(assignment, CancellationToken.None);

    [Fact]
    public async Task A_bucket_with_several_members_waits_for_a_claim()
    {
        var result = await ResolveAsync(new(AssignmentPolicy.Bucket, ["a", "b"]), "a", "b");
        Assert.Equal(["a", "b"], result.Candidates);
        Assert.Null(result.Assignee);
        Assert.False(result.Blocked);
    }

    [Fact]
    public async Task A_bucket_with_one_member_is_assigned_directly()
    {
        var result = await ResolveAsync(new(AssignmentPolicy.Bucket, ["a"]), "a");
        Assert.Equal("a", result.Assignee);
    }

    [Fact]
    public async Task Members_who_lost_the_approver_role_are_skipped()
    {
        var result = await ResolveAsync(new(AssignmentPolicy.Bucket, ["a", "b"]), "b");
        Assert.Equal(["b"], result.Candidates);
        Assert.Equal("b", result.Assignee);
    }

    [Fact]
    public async Task A_specific_assignment_goes_to_its_owner()
    {
        var result = await ResolveAsync(new(AssignmentPolicy.Specific, ["a", "b"], "b"), "a", "b");
        Assert.Equal("b", result.Assignee);
        Assert.False(result.Blocked);
    }

    [Fact]
    public async Task A_specific_owner_defaults_to_the_only_member()
    {
        var result = await ResolveAsync(new(AssignmentPolicy.Specific, ["a"]), "a");
        Assert.Equal("a", result.Assignee);
    }

    [Fact]
    public async Task A_specific_owner_without_the_role_blocks_the_task()
    {
        var result = await ResolveAsync(new(AssignmentPolicy.Specific, ["a", "b"], "b"), "a");
        Assert.True(result.Blocked);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Nobody_eligible_blocks_the_task_instead_of_approving_it()
    {
        var result = await ResolveAsync(new(AssignmentPolicy.Bucket, ["a"]));
        Assert.True(result.Blocked);
        Assert.Null(result.Assignee);
    }
}
