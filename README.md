# Configurable ticket approvals with Elsa 3.9

An approval workflow API. A configurator describes the stages of a workflow in JSON; the API turns that JSON
into a normal **Elsa 3.9** flowchart. Elsa then runs the tickets, stores them in PostgreSQL and creates the
user tasks (inbox items with buttons such as Approve / Reject).

## Projects

| Project | What it contains |
|---|---|
| ilworkflow.Domain | The workflow model (`WorkflowSetup`, `StageDefinition`, `StageAction`, ...) and the configuration entity. No dependencies. |
| ilworkflow.Contracts | HTTP request/response records, one folder per module (`WorkflowConfigurations`, `Tasks`). |
| ilworkflow.Application | FluentValidation validators, `AssignmentResolver`, service interfaces. No Elsa or EF Core. |
| ilworkflow.Infrastructure | Everything Elsa and PostgreSQL: the flowchart builder, custom activities, task service, EF Core. |
| ilworkflow.API | `Program.cs`, controllers, middleware, settings. |

Dependencies point inward: API → Infrastructure → Application → Domain. See [architecture notes](docs/ARCHITECTURE.md) and the [developer guide](docs/DEVELOPER-GUIDE.md).

## Start

```powershell
dotnet build ilworkflow.slnx
# First setup only:
pwsh -NoProfile -ExecutionPolicy Bypass -File ./Setup.ps1
dotnet run --project ilworkflow.API --launch-profile http
```

Open http://localhost:5000/swagger. Development uses PostgreSQL on localhost:5432, database `ilworkflow`, user `postgres`.
Put the database password and `Identity:Tokens:SigningKey` (32+ characters) in user secrets (`UserSecretsId` `ilworkflow-elsa-local`).

Documentation only, without PostgreSQL: `dotnet run --project ilworkflow.API --launch-profile http -- --SwaggerPreview true` (business operations return 503).

## How a ticket flows

```
1. Configurator  POST /api/workflow-configurations                   save a draft (validated, compiled to an Elsa flowchart)
2. Configurator  POST /api/workflow-configurations/{id}/versions/{v}/publish
3. Requester     POST /elsa/api/workflow-definitions/{id}/execute    { "input": { "attributes": { ... } } }
4. Approver      GET  /api/tasks?scope=assigned                       the task shows the stage's buttons
5. Approver      POST /api/tasks/{taskId}/complete                    { "action_key": "APPROVE", "data": { "comment": "ok" } }
6. Anyone        GET  /elsa/api/workflow-instances/{instanceId}       workflowState.output.ticketOutcome = Approved | Rejected
```

`attributes` is the ticket's business data (for example `{ "claimAmount": 6000 }`). It is stored with every revision and
shown on tasks, but the workflow does not interpret it. Elsa's status `Finished` only means the workflow ended; read
`ticketOutcome` for the business result.

## Requests to try, in order

Run one request at a time (VS Code REST Client) and copy the IDs between files.

1. [Authentication.http](Authentication.http): roles, users and login. Sample password `Password123!`.
2. [Workflow-Configuration.http](Workflow-Configuration.http): create, update, validate and publish a configuration.
3. [Ticket-Workflow.http](Ticket-Workflow.http): submit a ticket, approve, reject, correct and resubmit.
4. [Parallel-Approvals.http](Parallel-Approvals.http) and [Observed-Tasks.http](Observed-Tasks.http).

## The configuration request

`POST /api/workflow-configurations` is the only write endpoint. All `/api/*` JSON names are snake_case.

| Body contains | Result |
|---|---|
| `configuration` only | New definition with a generated `workflow_definition_id`, version 1. |
| `workflow_definition_id` + `configuration` | That definition, or its **next version** if it exists (409 while a draft is still open). |
| `workflow_definition_id` + `workflow_version` + `configuration` | Updates that draft (404 unknown version, 409 if already published). |

```json
{
  "workflow_definition_id": "expense-claim",
  "configuration": {
    "name": "Expense claim",
    "start_stage": "A",
    "supervisors": ["<userId>"],
    "stages": [
      {
        "key": "A",
        "title": "Initial review",
        "assignment": { "policy": "Bucket", "members": ["<userId>", "<userId>"] },
        "viewable_by_stages": ["B"],
        "actions": [
          { "key": "APPROVE", "label": "Approve", "destination": { "kind": "Stage", "stage_key": "B" } },
          { "key": "REJECT", "label": "Reject", "destination": { "kind": "Correction", "stage_key": "A" }, "require_reason": true }
        ]
      },
      {
        "key": "B",
        "title": "Final approval",
        "assignment": { "policy": "Specific", "members": ["<userId>"] },
        "actions": [
          { "key": "APPROVE", "destination": { "kind": "Approved" } },
          { "key": "REFER_UW", "label": "Refer to UW", "destination": { "kind": "Stage", "stage_key": "A" }, "require_reason": true },
          { "key": "REJECT", "destination": { "kind": "Rejected" }, "require_reason": true }
        ]
      }
    ]
  }
}
```

Read a version with `POST /api/workflow-configurations/get` and `{ "workflow_definition_id": "...", "workflow_version": 2 }`
(omit the version for the latest). Validate and publish: `POST /api/workflow-configurations/{id}/versions/{version}/validate` and `.../publish`.
Published versions never change; running tickets keep the version they started with.

### Fields

- **`actions`**: the buttons of a stage. `key` is UPPER_CASE (`RESUBMIT` is reserved), `label` defaults to the key,
  `require_reason: true` makes `data.reason` mandatory.
- **`destination.kind`**:
  - `Stage` + `stage_key`: open that stage.
  - `Correction` + `stage_key`: send the ticket back to the requester; after RESUBMIT it continues at `stage_key`.
  - `Approved` / `Rejected`: finish the ticket.
- **`assignment.policy`**:
  - `Bucket`: all members see the task and one claims it. A single member gets it directly.
  - `Specific`: one owner. Set `user_id` when there is more than one member.
- **`parallel_with`**: `["C"]` on stage B opens B and C together. The first APPROVE continues, the group is rejected only when every
  task rejects. Every stage of a group offers exactly APPROVE and REJECT with the same destinations.
- **`viewable_by_stages`**: members of those stages can read (not act on) this stage's task: `GET /api/tasks?scope=observed`.
- **`supervisors`**: users with role `workflow_supervisor` who see and can reassign every task of this workflow:
  `GET /api/tasks?scope=supervised`.

Members, owners and supervisors are Elsa user IDs (returned when the user is created), not usernames. Members need the `Approver` role.

## Responses

`/api/*` endpoints return `{ "trace_id", "status_code", "messages", "data" }`; errors put a `code` (and `errors[]` for validation) in `data`.
Elsa's own `/elsa/api/*` endpoints keep Elsa's format. Task commands return 202 (accepted; the next task may take a moment to appear)
or 409 (the task changed: reload and retry). Invalid task data returns Elsa's 422.

## Verify

`Tests/smoke_workflow.py` runs the full flow against a live API (configuration, publish, custom action, correction, rejection,
parallel group, observed and supervised lists):

```powershell
python Tests/smoke_workflow.py --attributes
```

The C# live tests in `ilworkflow.Tests` still target the removed conditional-routing feature and need updating before use.

## Not included yet

Conditional routing on attribute values, all-approver/quorum policies, notifications, SLA escalation, attachments and external identity providers.
