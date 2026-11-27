using FluentValidation;

namespace IlWorkflow.Core.Validation;

public sealed class GetConfigurationRequestValidator : AbstractValidator<GetConfigurationRequest>
{
    public GetConfigurationRequestValidator() =>
        RuleFor(x => x.WorkflowDefinitionId).NotEmpty().WithMessage("workflow_definition_id is required.");
}
