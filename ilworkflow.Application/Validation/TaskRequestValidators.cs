using FluentValidation;
using IlWorkflow.Contracts.Tasks.Requests;

namespace IlWorkflow.Application.Validation;

public sealed class AssignTaskRequestValidator : AbstractValidator<AssignTaskRequest>
{
    public AssignTaskRequestValidator() => RuleFor(x => x.UserId).NotEmpty().WithMessage("user_id is required.");
}

public sealed class CompleteTaskRequestValidator : AbstractValidator<CompleteTaskRequest>
{
    public CompleteTaskRequestValidator() => RuleFor(x => x.ActionKey).NotEmpty().WithMessage("action_key is required.");
}
