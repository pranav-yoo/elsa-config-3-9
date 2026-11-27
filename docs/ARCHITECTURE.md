# Three-project architecture

```text
API -> Infrastructure -> Core
API -> Core
```

- **Core:** the business core, with no Elsa, EF Core or ASP.NET dependency (FluentValidation and DI abstractions only). One folder per feature, each with its model, request/response records and validators:
  - `WorkflowConfigurations/`: `Model/` (WorkflowSetup, StageDefinition, StageAction, Destination, AssignmentDefinition, TaskActionKeys, enums), `Requests/`, `Responses/`, `Validators/`, the `WorkflowConfiguration` database row and `IWorkflowConfigurationService`.
  - `Tasks/`: task requests, `TaskDecisionValidator`, `AssignmentResolver`, `IApproverDirectory`.
  - `Tickets/`: `TicketOutcome`, `ExecuteRequestValidator`.
  - `Common/`: `Result<T>`, `ApiException`, `ConfigurationJson`.
  - `DependencyInjection.cs`: `AddCore()`.
- **Infrastructure:** WorkflowConfigurationService implements the Core interface using EF and native Elsa publishing. `DependencyInjection.cs` (`AddInfrastructure()`) registers Elsa, the database and the task services. Folders: Authentication (user-token scheme), Authorization (permission checks, observer/supervisor access policy, instance read access), Configuration (the service), Identity (ApproverDirectory resolves approver roles from Elsa Identity), Persistence (DbContext, Configurations/ table mappings, Migrations/, UserTasks/ provider fixes), Tasks (TaskService, observed/supervised query) and Workflows (graph builder, form adapter, parallel-task locking, Activities/).
- **API:** Controllers, Common (ApiResultHelper builds every Result<T> response), Extensions (`AddApi()` for controllers/JSON/policies, startup helpers), Middleware (ExceptionHandlingMiddleware, WorkflowRequestGuard). Program.cs calls `AddCore()`, `AddInfrastructure()` and `AddApi()`, then builds the request pipeline.

Why one Core project instead of separate Domain/Application/Contracts: the domain is a configuration model without behavior of its own, so the separate projects added navigation cost without protecting anything. The boundary that matters, Core never referencing Elsa or EF Core, is still enforced by the compiler. Split `Core/WorkflowConfigurations/Model` out again if it gains real business rules.

No generic repository wrappers, mediator pipeline or replacement task tables were added. EF/Elsa-dependent orchestration stays behind the application service port.

Run `dotnet run --project ilworkflow.API --launch-profile http`. API owns settings, Samples and launch profiles. Root setup/token scripts now target that project. The user-secret ID remains ilworkflow-elsa-local.

Infrastructure uses namespace IlWorkflow.Infrastructure.* (assembly ilworkflow.Infrastructure). Elsa identifies the custom activities by their `[Activity]` type names (`Tickets.TicketReview`, ...), not by CLR namespace. Migration identifiers/schemas stay unchanged.
