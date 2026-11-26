namespace IlWorkflow.Contracts.WorkflowConfigurations;

/// <summary>Result of validating a configuration version without publishing it. Failures return 400 instead.</summary>
public sealed record ValidateConfigurationResponse(bool Valid);
