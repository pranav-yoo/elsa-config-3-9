using System.Text.Json;
using static IlWorkflow.UnitTests.SetupFactory;

namespace IlWorkflow.UnitTests.Core;

public class TaskDecisionValidatorTests
{
    private static readonly StageDefinition Review = TwoStages().Stages[0]; // APPROVE, REJECT (reason required)

    private static string[] Errors(StageDefinition? stage, string action, string json) =>
        new TaskDecisionValidator(stage).Validate(new DecisionData(action, JsonDocument.Parse(json).RootElement.Clone()))
            .Errors.Select(error => error.ErrorMessage).ToArray();

    [Fact] public void Approve_with_a_comment_is_valid() => Assert.Empty(Errors(Review, "APPROVE", """{"comment":"ok"}"""));
    [Fact] public void Action_keys_ignore_case() => Assert.Empty(Errors(Review, "approve", "{}"));
    [Fact] public void Unknown_actions_are_rejected() => Assert.Contains(Errors(Review, "NOPE", "{}"), e => e.Contains("Unknown action"));
    [Fact] public void A_reason_is_required_when_the_action_says_so() => Assert.Contains(Errors(Review, "REJECT", "{}"), e => e.Contains("requires a reason"));
    [Fact] public void A_blank_reason_does_not_count() => Assert.NotEmpty(Errors(Review, "REJECT", """{"reason":"  "}"""));
    [Fact] public void A_reason_satisfies_the_requirement() => Assert.Empty(Errors(Review, "REJECT", """{"reason":"too high"}"""));
    [Fact] public void Reviewers_cannot_change_attributes() => Assert.NotEmpty(Errors(Review, "APPROVE", """{"attributes":{"amount":1}}"""));
    [Fact] public void Data_must_be_an_object() => Assert.Contains(Errors(Review, "APPROVE", "[]"), e => e.Contains("data object"));

    [Fact] public void A_correction_needs_attributes() => Assert.NotEmpty(Errors(null, "RESUBMIT", "{}"));
    [Fact] public void A_correction_with_attributes_is_valid() => Assert.Empty(Errors(null, "RESUBMIT", """{"attributes":{"amount":4000}}"""));
    [Fact] public void A_correction_accepts_nothing_else() => Assert.NotEmpty(Errors(null, "RESUBMIT", """{"attributes":{},"reason":"x"}"""));
}
