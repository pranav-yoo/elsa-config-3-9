using FluentValidation;

namespace IlWorkflow.Core.WorkflowConfigurations.Validators;

public sealed class SaveConfigurationRequestValidator : AbstractValidator<SaveConfigurationRequest>
{
    public SaveConfigurationRequestValidator()
    {
        RuleFor(x => x.Configuration).NotNull().WithMessage("Configuration is required.");
        // A blank workflow_definition_id means "none": the service creates a new definition with a generated ID.
        RuleFor(x => x.WorkflowDefinitionId!).SetValidator(new DefinitionIdValidator()).When(x => !string.IsNullOrWhiteSpace(x.WorkflowDefinitionId));
        RuleFor(x => x.WorkflowDefinitionId).Must(id => !string.IsNullOrWhiteSpace(id))
            .When(x => x.WorkflowVersion is not null)
            .WithMessage("workflow_definition_id is required when workflow_version is supplied.");
    }
}
