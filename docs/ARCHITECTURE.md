# Four-project architecture

HTTP contracts, workflow behavior and database schemas are preserved.

```text
API -> Application -> Domain
API -> Infrastructure -> Application / Domain
```

- **Domain:** WorkflowConfiguration.cs contains the entity, enums and value records. AttributeRules.cs contains exact comparisons and field constraints. No external package dependencies.
- **Application:** Contracts.cs contains DTOs, JSON helpers, errors and the IWorkflowConfigurationService/IApproverDirectory ports. Validators enforce configuration/input/decision rules. AssignmentResolver selects eligible participants through the directory port. References Domain and FluentValidation only.
- **Infrastructure:** WorkflowConfigurationService implements the application port using EF and native Elsa publishing. Persistence contains the existing migration and provider fixes. Workflows contains graph generation, review activities and the form adapter. Authentication, ExternalIdentity and Tickets implement token/master/local identity integration. Authorization contains native participation checks. Legacy activity types remain for old instances.
- **API:** Controllers bind requests; middleware guards native execution. Services/WorkflowServiceRegistration.cs connects ports/adapters and registers Elsa/custom services. Program.cs owns startup, diagnostics, database initialization and the request pipeline.

No generic repository wrappers, mediator pipeline or replacement task tables were added. EF/Elsa-dependent orchestration stays behind the application service port.

Run `dotnet run --project ilworkflow.API --launch-profile http`. API owns settings, Samples and launch profiles. Root setup/token scripts now target that project. The user-secret ID remains ilworkflow-elsa-local.

Infrastructure deliberately keeps assembly name IlWorkflow.Api and the IlWorkflow.Api.Workflows/Tickets activity namespaces, because persisted Elsa state can refer to those CLR identities. The API host uses ilworkflow.API.Host. These compatibility names do not reverse project dependencies. Migration identifiers/schemas stay unchanged; moving files does not recreate data or rewrite published definitions.

README documents the live workflow, identity, request-file, preview and restart checks.

## Preconfigured attribute master

Domain `WorkflowAttribute` represents a field linked to a stable definition ID with an Active/Inactive status. Application `IWorkflowAttributeService` and shared FluentValidation validators define its contract. Infrastructure `WorkflowAttributeService` stores it in `Application.WorkflowAttributes`; API `WorkflowAttributesController` exposes configurator-only POST create/update and GET listing.

`SaveConfigurationRequest.definition_id` supplies the native Elsa family ID. The configuration adapter locks/checks that ID and snapshots only its active master fields before generating the graph. Catalog-backed saves and next-version creation refresh that snapshot; publication and running instances remain pinned. Legacy clients without definition_id continue using inline schema. The table/catalog flag migration leaves all existing configurations and native Elsa tables intact.
