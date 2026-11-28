using FluentValidation;

namespace IlWorkflow.Core.WorkflowConfigurations.Validators;

public sealed class GetConfigurationRequestValidator : AbstractValidator<GetConfigurationRequest>
{
    public GetConfigurationRequestValidator() =>
        RuleFor(x => x.WorkflowDefinitionId).NotEmpty().WithMessage("workflow_definition_id is required.");
}
