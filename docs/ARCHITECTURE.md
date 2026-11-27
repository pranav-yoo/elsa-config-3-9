# Three-project architecture

```text
API -> Infrastructure -> Core
API -> Core
```

- **Core:** the business core, with no Elsa, EF Core or ASP.NET dependency (only FluentValidation).
  - `Domain/`: Entities (WorkflowConfiguration), Enums, Configuration (WorkflowSetup, StageDefinition, StageAction, Destination, AssignmentDefinition, TaskActionKeys).
  - `Contracts/`: every HTTP request and response model, grouped by module (`WorkflowConfigurations`, `Tasks`) with `Requests/` and `Responses/` folders. Elsa's own native response types stay with Elsa.
  - `Abstractions/`: IWorkflowConfigurationService and IApproverDirectory, implemented by Infrastructure.
  - `Common/` (ConfigurationJson, ApiException), `Validation/` (FluentValidation rules) and `Workflows/` (AssignmentResolver).
- **Infrastructure:** WorkflowConfigurationService implements the Core interface using EF and native Elsa publishing. Folders: Authentication (user-token scheme), Authorization (permission checks, observer/supervisor access policy, instance read access), Configuration (the service), Identity (ApproverDirectory resolves approver roles from Elsa Identity), Persistence (DbContext, Migrations/, UserTasks/ provider fixes), Tasks (TaskService, observed/supervised query) and Workflows (graph builder, form adapter, parallel-task locking, Activities/).
- **API:** Controllers, Common (ApiResultHelper builds every Result<T> response), Extensions (service registration, startup helpers), Middleware (ExceptionHandlingMiddleware, WorkflowRequestGuard). Extensions/WorkflowServiceRegistration.cs registers Elsa and the application services. Program.cs owns startup and the request pipeline.

Why one Core project instead of separate Domain/Application/Contracts: the domain is a configuration model without behavior of its own, so the separate projects added navigation cost without protecting anything. The boundary that matters, Core never referencing Elsa or EF Core, is still enforced by the compiler. Split `Core/Domain` out again if it gains real business rules.

No generic repository wrappers, mediator pipeline or replacement task tables were added. EF/Elsa-dependent orchestration stays behind the application service port.

Run `dotnet run --project ilworkflow.API --launch-profile http`. API owns settings, Samples and launch profiles. Root setup/token scripts now target that project. The user-secret ID remains ilworkflow-elsa-local.

Infrastructure uses namespace IlWorkflow.Infrastructure.* (assembly ilworkflow.Infrastructure). Elsa identifies the custom activities by their `[Activity]` type names (`Tickets.TicketReview`, ...), not by CLR namespace. Migration identifiers/schemas stay unchanged.
