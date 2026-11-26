# Developer guide: configurable Elsa ticket approvals

## Read this first

We own **configuration**. Elsa owns **execution**. A configuration service is not a second task engine, and the request middleware is not another ticket controller.

The implementation is one ASP.NET Core application. There are no microservices, generic repository layers or domain-wide event buses. Configuration generates a native Elsa flowchart; there is no runtime interpreter.

## Request flow

```text
Configurator token
  -> WorkflowConfigurationController
  -> WorkflowConfigurationService
  -> FluentValidation + application EF configuration snapshot
  -> native Elsa draft / publish services

Requester token
  -> POST /elsa/api/workflow-definitions/{id}/execute
  -> WorkflowRequestGuard: authenticate, validate, pin version, inject requester
  -> Elsa creates an instance
  -> InitializeTicket
  -> generated flowchart
  -> TicketReview -> native UserTask -> bookmark / persisted wait

Approver token
  -> native User Task claim / complete endpoint
  -> native task permissions, owner and revision checks
  -> configured-ticket form provider -> FluentValidation
  -> Elsa accepts completion and resumes its bookmark
  -> TicketReview.OnCompletedAsync emits Approve / Reject / Resubmit, or Custom for configured actions (routed by lastAction)
  -> native flowchart connections / FlowDecision choose the next node
  -> FinishTicket writes Approved / Rejected to native workflow output
```

## Files and important methods

| File | Entry points | Why this code exists |
|---|---|---|
| ilworkflow.API/Controllers/WorkflowConfigurationController.cs | Save, Get, Validate, Publish, NextDraft | Conventional controller; HTTP binding, authorization and responses only. POST implements create/update. |
| ilworkflow.Infrastructure/Configuration/WorkflowConfigurationService.cs | SaveAsync (create, update or next version), PublishAsync, ApplySetup | Coordinate our schema snapshot with native Elsa version lifecycle; never manage task claims. |
| ilworkflow.Domain/WorkflowConfiguration.cs | records/enums/JSON settings | Define the supported configuration vocabulary; reject unknown enum values rather than accept arbitrary strings or script source. |
| ilworkflow.Application/WorkflowConfigurationValidators.cs | WorkflowConfigurationValidator, TicketInputValidator, AttributeRules | Centralize field validation, eligibility, typed conditions and graph checks using FluentValidation. |
| ilworkflow.Infrastructure/Persistence/ApplicationDbContext.cs | model mapping and explicit migration | Store version-linked JSONB configuration and the preconfigured attribute master, using a separate schema and migration history. |
| ilworkflow.Infrastructure/Workflows/WorkflowDefinitionBuilder.cs | Build | Compile configuration into a native flowchart; native FlowDecision nodes read persisted typed comparison results. |
| ilworkflow.Infrastructure/Workflows/TicketReview.cs | InitializeTicket, TicketReview, FinishTicket | Thin typed integration activities around native human tasks; own no task table or claim state. |
| ilworkflow.Application/AssignmentResolver.cs | ResolveAsync | Select eligible candidates/initial owner from supported policies; Elsa owns the resulting assignment. |
| ilworkflow.Infrastructure/Workflows/TicketFormProvider.cs | ResolveAsync, ValidateAndNormalizeAsync | Make validation unavoidable even when a client calls native Elsa complete directly. |
| ilworkflow.API/Middleware/WorkflowRequestGuard.cs | InvokeAsync | Protect existing Elsa routes; it is middleware, not an endpoint/controller. |
| ilworkflow.Infrastructure/Authorization/WorkflowAccessService.cs | CanReadInstanceAsync | Restrict native instance reads to requester, participating workers or supervisors. |
| ilworkflow.Infrastructure/Authorization/ConfigurationAccess.cs | permission helpers | Match Elsa permission grants, including wildcard grants. |
| ilworkflow.Infrastructure/Tickets/TicketIdentity.cs | HasRoleAsync | Resolve approver roles through native Elsa Identity. |
| ilworkflow.Infrastructure/Persistence/UserTaskPostgreSqlCompatibility.cs | model handler and repository decorator | Correct two verified 3.9 provider edge cases without replacing Elsa task operations. |
| Program.cs | DI, middleware order and migrations | Compose native stores and register our small integration pieces. |

## Identifiers are different things

- Application `id`: numeric configuration record ID. Omitted/null/zero creates; positive updates a draft.
- `definitionId`: stable Elsa workflow family ID; use this in native execute URLs.
- `definitionVersionId`: exact native definition version linked to a configuration snapshot.
- `definitionVersion`: native numeric version selected by the request guard.
- `workflowState.id`: the submitted ticket/workflow instance ID.
- Task `id`: one stage visit's human task. Returning to A creates another task, not a reset of the old task.
- `operationId`: one native task command UUID. Reuse the original command body for a retry.
- `expectedRevision`: the native task revision for task commands. Configuration `revision` is a separate, internal counter returned in responses; clients never send it.
- `workflow_definition_id` / `workflow_version`: the configuration family and one of its versions (the Elsa version number, not its row ID). Updates need both; omitting the version creates a new version.

## Configuration and publishing

`SaveAsync` runs asynchronous FluentValidation, creates or loads a native draft, generates its graph and persists the application snapshot. A unique version index and EF concurrency token supplement validation.

`PublishAsync` takes a PostgreSQL advisory transaction lock per workflow family. This serializes saves, new versions and publishes across application processes. It does not serialize unrelated workflows.

Elsa uses separate EF contexts, so this is **not** a single transaction spanning every native write. We therefore commit `Ready=true` only after native publication succeeds. Native execute refuses a version without a ready, frozen matching configuration and generated-definition hash. A partial failure can leave native work to retry/reconcile; it cannot admit incomplete configuration through the guarded execute route. An initial-save failure may leave an orphan native draft that an administrator can inspect/delete.

Elsa normalizes serialized drafts. Hash the representation read back from its native store, not the pre-save JSON text. The hash is an integrity check against accidental raw edits, not an authorization secret. Trusted administrators/SDK callers must still follow the configuration boundary.

`Frozen` remains true after a newer version retracts this version. Old instances may still reference it. Saving a definition ID without a version uses Elsa's draft creation from the published version and builds the new draft from the configuration in the request, so a new version never depends on an older snapshot.

## Why a generated graph plus a typed review activity?

The graph's connections are the executable routes. The review wrapper does not calculate a destination. It emits an outcome, and Elsa chooses the connected node.

Custom action labels represented as nested `Input<string>` inside literal JSON failed conversion in Elsa 3.9 during the research. `TicketReview` constructs `UserTaskActionDefinition` in typed C# with uppercase keys and readable labels. The completion callback maps the public key to the existing graph port, so pending tasks with older casing can still resume.

Conditions use the shared C# `AttributeRules.Matches` implementation for both routing and assignment. `TicketState.SetAttributes` calculates route results at submission and correction and persists them under the internal `$routes` key in `attributesJson`. Native `FlowDecision` nodes read those booleans, so JavaScript cannot round decimal/Int64 amounts or inherit properties for missing fields. Public attributes and task data do not contain `$routes`. A configurator never submits raw JavaScript. Existing immutable definitions retain their old expressions; publish a new version to adopt the corrected graph generation.

## Attributes, revisions and correction

All field definitions belong to the application snapshot. Every submitted field is validated. Fields used in routing/assignment conditions are additionally described as top-level native workflow inputs. Clients still send one `input.attributes` object; the guard supplies normalized values and identity.

The complete attributes and revision history live in workflow output; `attributesJson` is a workflow-storage-backed variable for conditions. Completed activity contexts can be pruned, so final outcome/history must not live only in `ActivityExecutionContext.Properties`.

Pending review tasks have a snapshot of their task data and form binding. Form validation must use that binding, not the latest editable database configuration. Review accepts reason/comment only; attributes are frozen. Rejection requires a nonblank reason.

Correction creates an assigned task for the original requester. Resubmit supplies the full validated attribute object, appends a revision, updates conditions' persisted variable, and emits Resubmit. The graph fixes the return stage using the rejection destination's `stageKey`. D -> correction -> D intentionally does not rerun B/C when an amount changes.

## Middleware and access control

Middleware runs after authentication/authorization and before native Elsa handlers. The configuration controller does not own native execute URLs; therefore endpoint validation alone would be bypassable if placed only there.

`SwaggerPreview=true` is Development-only documentation mode. Application migrations are skipped along with Elsa's migrations. The guard returns 503 for configuration and business mutations; login remains available for browsing authorized endpoints. `ConfiguredWorkflowTests` with `ILWORKFLOW_VERIFY_PREVIEW=1` checks this against a preview server, which can be started with an unavailable PostgreSQL port to verify no connection is required.

The guard validates before an instance is created, injects the real requester, pins the published version, rejects alternate activity starts/custom variables, and prevents ordinary users from dispatch/import/administrative paths. Native execute is synchronous for this phase; no HTTP identity is read from a later background workflow callback.

Native `workflows/instances:view` is broader than ticket ownership. Individual instance/journal reads have an additional requester/participant check. Participation is queried through Elsa's scoped task repository, not by fetching all work and filtering after pagination. Broad instance listing/export/administration remains supervisor-only. API IDs do not themselves grant access.

Request and action keys use the documented casing. Resource/action route names are normalized because ASP.NET routes are case-insensitive. Do not expose an alternative route/prefix without applying the same guards. Configuration/directory tenancy is limited to the default organization in this version.

Native User Tasks still enforce candidate claim rights, current owner completion, manager permissions, task revisions and operation idempotency. Do not replace these with application booleans or state columns.

## Identity EF versus User Tasks EF

Elsa Identity EF persists local users, password hashes, roles and permissions. Native user creation returns a generated ID. Username and email are not interchangeable with that ID.

User Tasks EF persists tasks and references to participant IDs. It does not create login accounts. Approvers, requesters and supervisors are all local Elsa Identity users.

## PostgreSQL compatibility fixes

Both were reproduced against installed Elsa 3.9.0 packages:

1. Native migration creates `UserTasks.TagsJson` as text, while Npgsql infers an array for the primitive collection. An Elsa model-creation handler applies a text-JSON conversion and change comparer. Legacy empty-array text `{}` is read as an empty list, preserving tasks written during earlier unpatched experiments. No native table rewrite or replacement task table is introduced.
2. Native nullable JSON reads return a nullable JsonElement containing Undefined for a database JSON null. A thin repository decorator normalizes only those values before native REST serialization; queries, writes, events, revision checks and mutations delegate to the native repository.

The native keyword-search predicate includes `Tags.Any`, which is not translatable through the text converter. The middleware returns explanatory HTTP 400 for keyword `search` rather than a provider HTTP 500. Scope, status, instance, reference and other supported filters remain native. Revisit this compatibility file when upgrading Elsa; do not silently retain it against another version.

## Verification and remaining boundaries

`ilworkflow.Tests/ConfiguredWorkflowTests.cs` creates realistic accounts and exercises configuration versioning, conditional approvals, all rejection routes, correction, stale/duplicate commands, concurrent claims, missing assignment repair and access/validation failures. It saves only IDs for a post-restart phase. The test runner never prints or persists issued tokens.

`ilworkflow.Tests/HttpFileSequenceTests.cs` executes the actual main HTTP-file requests, resolving their response variables and filters. Optional reject/update branches are deliberately excluded from a happy-path Send All.

Pending-task restart recovery must be tested after changes to activity types, callback method names, serialization, storage drivers or provider configuration. Keep legacy registered activity types while persisted instances can reference them.

The native task command store can race after its initial operation lookup. PostgreSQL uniqueness errors are mapped to HTTP 409 at the API boundary; the database constraint remains authoritative. Concurrent-claim tests use distinct operation IDs and require one winner, with an explicit denial/conflict for the other caller.

Native completion is asynchronous: 202 is acceptance, not proof the next task exists yet. Poll the next task/instance. Submission retries are not yet deduplicated; correlation IDs are searchable references, not idempotency keys. An outbox/idempotent submission mechanism, parallel approval thresholds, large payload/retention policies, multi-node runtime coordination, production identity-directory linking, attachments and notifications are later work.

## Named definitions and active field master

Use Attribute-Catalog.http to create master fields through POST /api/workflow-attributes before creating the workflow. Pass workflow_definition_id in the configuration POST to set the native Elsa family ID. Use the catalog key in a rule's conditions[].attribute (same orGroup = OR, otherwise AND). The adapter resolves the keys used by approval and assignment conditions against active catalog fields for the same definition, then FluentValidation checks every condition against that mapping.

The snapshot preserves catalog types and constraints and marks each mapped field required. Draft saves and new-version creation refresh the mapping using the existing definition ID. Published snapshots and running instances retain their saved fields. Attribute keys are unique within a definition. Configuration and attribute controllers require the configurator permission and are unavailable in preview mode.

AttributeDefinitionValidator is shared by master writes and configuration validation. WorkflowAttributeService implements the application port with EF Core; the API controller contains no persistence logic. ilworkflow.Tests/AttributeCatalogTests.cs exercises both field storage and native workflow execution/approval.
