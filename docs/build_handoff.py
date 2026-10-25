"""Produce a self-contained implementation handoff without changing the application."""
import json
import shutil
from pathlib import Path

root=Path(__file__).resolve().parents[1]
out=root/'output/pdf'
out.mkdir(parents=True,exist_ok=True)
requests=out/'workflow-requests'
requests.mkdir(exist_ok=True)
captures=json.loads((root/'docs/api-captures.json').read_text(encoding='utf-8-sig'))
parts=[]
def text(value): parts.append(value.strip())
def block(value,language='json'):
    if not isinstance(value,str): value=json.dumps(value,indent=2,ensure_ascii=True)
    parts.append('```'+language+'\n'+value.rstrip()+'\n```')
def file_block(path,language): block((root/path).read_text(encoding='utf-8-sig'),language)
def capture(title): return next(c for c in captures if c['title']==title)
def command(value): block(value,'powershell')

text('''# Elsa 3.9 ticket workflows: complete handoff for another Codex

Date: 25 October 2026 (Asia/Calcutta).

## 1. Read this first

This is the final **restored and tested application**, not the abandoned native-only experiment. Implement the same behavior in the receiving application. This handoff is model-independent; the receiving Codex does not need a particular model name.

- ASP.NET Core, .NET 10 in this research app; Elsa packages pinned to **3.9.0**.
- Business workflow definition ID: **expense-claim-v1**.
- Native Elsa APIs for definition publication, workflow execution, task listing, claiming, decisions and history. No custom ticket REST controllers.
- One small custom **Tickets.TicketFlow** activity interprets a **configurationJson literal string** and schedules native Elsa User Tasks.
- One custom **TicketFormProvider** validates Approve, Reject and Resubmit data. This provider is necessary for the tested payloads.
- Attributes and stage/member/routing configuration are JSON. Do not build a second task engine or add custom ticket/task tables merely to reproduce this research sample.
- Development users/roles are native Elsa configuration providers backed by JSON. Actual workflow/task state is managed by Elsa, not by these JSON files.
- All approval/rejection/correction checks in this handoff passed in **in-memory preview mode**. PostgreSQL restart persistence of this final version is **not verified**.

**Do not tell the receiving application that configurationJson is a built-in Elsa interpreter. It is our custom input and activity.** Merely posting this JSON to a plain Elsa server without registering the activity/form provider will not reproduce the behavior.

The API-only PDF is [Elsa-3.9-Ticket-Workflow-API-Guide.pdf](Elsa-3.9-Ticket-Workflow-API-Guide.pdf). This Markdown additionally contains the configuration, source code, curl examples and receiving-agent implementation instructions.

## 2. Exact business behavior

```text
A: Alice/Amy bucket --Approve--> B: Bella/Bob bucket
  B approval, claimAmount > 5000  --> C: Clara --> D: Dan
  B approval, claimAmount <= 5000 ----------------> D: Dan
D --Approve--> Completed

A --Reject--> requester correction --Resubmit--> new A visit
B --Reject--> new A visit
C --Reject--> terminal Rejected
D --Reject--> requester correction --Resubmit--> new D visit
```

| Stage | Assignment | Approve | Reject |
|---|---|---|---|
| A | Alice or Amy claims one bucket task | B | Requester correction, then A |
| B | Bella or Bob claims one bucket task | C above 5000, otherwise D | A |
| C | Clara, conditional assignee | D | Terminal Rejected |
| D | Dan, specific assignee | Completed | Requester correction, then D |

Important details:

1. **5000 does not require C**; the comparison is strictly greaterThan 5000.
2. B is a **bucket**, not round robin. The user explicitly accepted this replacement.
3. One owner and one decision per stage visit. Candidates do not each vote.
4. Each stage visit creates a different native Elsa task. Completed A plus active B in one list is correct.
5. Review attributes are frozen. Review data may contain only string `reason` and/or `comment` fields.
6. Reject requires a non-empty `reason`. Correction uses `actionKey: Resubmit` and a complete validated `data.attributes` object.
7. Correction continues the **same workflow instance** and resumes the rejected stage.
8. **D correction does not replay B/C.** If 4000 becomes 6000 after D rejects, resubmission still resumes D. Preserve this tested behavior unless the human explicitly changes the policy. Flag its implications for a real approval system.
9. Both terminal business outcomes have Elsa `status: Finished`; distinguish them with `workflowState.output.ticketStatus` (`Completed` or `Rejected`).
10. Native operation IDs/revisions protect task commands; correlation IDs do **not** make execute/submission idempotent.

## 3. Files and responsibilities

| File | Purpose |
|---|---|
| Program.cs | Elsa host, native authentication/configuration providers, Swagger, persistence registrations, sanitized logging |
| Tickets/TicketFlow.cs | Custom configuration interpreter; schedules native UserTask and chooses next stage |
| Tickets/TicketConfiguration.cs | Configuration records, attribute/type/condition/configuration validation |
| Tickets/TicketFormProvider.cs | Native IUserTaskFormProvider implementation for review/correction submissions |
| Samples/identity.json | Development-only sample users and native roles/permissions |
| Samples/expense-claim.configuration.json | Readable business configuration |
| Samples/Definitions/expense-claim.definition.json | Complete POST body with escaped literal configurationJson |
| Tickets.http | Reusable native API requests |
| Tests/ConfigurationApi.ps1 | Verified approval/rejection/correction/access test suite |
| docs/api-captures.json | 148 captured HTTP exchanges, authentication secrets redacted |

The native-only experiment `expense-claim-native-v1` and `Tests/TicketApi.ps1` are separate, Complete-only examples. **They are not the application to port.** Do not substitute them for the implementation in this handoff.

## 4. Run and authenticate

In the source application:
''')
command('dotnet restore\ndotnet build\ndotnet run --launch-profile http')
text('''Swagger: `http://localhost:5000/swagger`. OpenAPI: `http://localhost:5000/swagger/v3/swagger.json`.

Development defaults to `SwaggerPreview=true`. It skips PostgreSQL connections/migrations and uses in-memory stores. Restart clears definitions, instances and tasks. Re-publish the definition after restart.

| Username / stable user ID | Development password | Purpose |
|---|---|---|
| admin | password | Native admin wildcard permissions |
| configurator | Password123! | Publish definitions |
| requester | Password123! | Start workflow and resubmit corrections |
| alice, amy | Password123! | A candidates |
| bella, bob | Password123! | B candidates |
| clara | Password123! | C owner |
| dan | Password123! | D owner |
| supervisor | Password123! | Native supervision/assignment permissions |

These are research credentials only. Sample passwords are represented by hashes in identity.json. In Development, changing Identity:Admin:Password in user secrets does not replace the configured admin hash. Do not overwrite an existing application's authentication with these fixtures.

## 5. The readable configuration JSON

Supported attribute types: string, decimal, integer, boolean, date (`yyyy-MM-dd`) and choice (`choices` array). Assignment policies: bucket, specific, condition. Conditions: equals, notEquals, greaterThan, greaterThanOrEqual, lessThan, lessThanOrEqual. First matching rule wins; default approveTo applies when no approval rule matches.

`members` contains user IDs, not actual directory groups. The sample validates candidates against JSON-configured users with the Approver role. External group membership and disabled-account enforcement need integration in the receiving application; they are not provided by this small interpreter.
''')
file_block('Samples/expense-claim.configuration.json','json')
text('''## 6. Exact definition publication JSON

Save this as `expense-claim.definition.json`, or use the provided companion file in workflow-requests.

Keep this exact input pattern: `configurationJson.expression.type` is `Literal`, `configurationJson.typeName` is `String`, and `configurationJson.expression.value` is an **escaped JSON string**, not an object. Root type is `Tickets.TicketFlow`.
''')
file_block('Samples/Definitions/expense-claim.definition.json','json')
text('''To change configuration safely, serialize the readable JSON into the literal string. Never concatenate or hand-escape it. Also update `model.inputs` when adding/removing attribute definitions; the current sample does not automatically generate input declarations.
''')
command('''$definition = Get-Content ./expense-claim.definition.json -Raw | ConvertFrom-Json
$config = Get-Content ./expense-claim.configuration.json -Raw | ConvertFrom-Json
$definition.model.root.configurationJson.expression.value = $config | ConvertTo-Json -Depth 60 -Compress
$definition | ConvertTo-Json -Depth 70 | Set-Content -Encoding utf8NoBOM ./expense-claim.definition.json''')
text('''Configuration validation happens **during execution**, not at native definition publication. It checks the known destinations, stage/attribute duplicates, candidate references, supported policies, condition types, start stage and approval-path cycles. Rejection return paths are allowed. Do not describe this as a complete immutable ticket-type publishing subsystem.

## 7. JSON request bodies and curl commands

The examples below use **PowerShell 7 + curl.exe**, so Windows PowerShell's old curl alias does not interfere. Curl commands use JSON files to avoid quoting bugs. Companion files are supplied in workflow-requests; run commands from that directory. Replace captured/example IDs with those returned by your own server. Response examples are excerpts; omitted fields are not absent from the actual API.

### 7.1 Login and keep a separate token per actor

Sample `login-alice.json`:
''')
block({'username':'alice','password':'Password123!'})
command('''$base = 'http://localhost:5000'
$aliceLogin = curl.exe -sS -X POST "$base/elsa/api/identity/login" -H "Content-Type: application/json" --data-binary "@login-alice.json" | ConvertFrom-Json
$aliceToken = $aliceLogin.accessToken
if (-not $aliceToken) { throw 'Alice login failed' }

# Obtain each actor's token; do not keep using Alice's token for Bob/Clara/Dan.
$tokens = @{}
foreach ($user in @('admin','configurator','requester','alice','amy','bella','bob','clara','dan','supervisor')) {
    $login = curl.exe -sS -X POST "$base/elsa/api/identity/login" -H "Content-Type: application/json" --data-binary "@login-$user.json" | ConvertFrom-Json
    if (-not $login.accessToken) { throw "Login failed: $user" }
    $tokens[$user] = $login.accessToken
}''')
text('Captured login response (tokens redacted):')
block(capture('Login configurator')['response'])
text('''In Swagger, paste the accessToken into the JWT bearer Authorize field. The UI supplies the Bearer prefix. In curl, explicitly send `Authorization: Bearer ...`.

### 7.2 Publish as configurator
''')
command('''curl.exe -sS -X POST "$base/elsa/api/workflow-definitions" -H "Authorization: Bearer $($tokens['configurator'])" -H "Content-Type: application/json" --data-binary "@expense-claim.definition.json"''')
wd=capture('Publish configurationJson definition')['response']['workflowDefinition']
text('Captured HTTP 200 response excerpt:')
block({'workflowDefinition':{k:wd[k] for k in ('id','definitionId','version','isPublished')}})
text('''The returned id is the definition-version ID. `definitionId` is the stable workflow template identifier. Neither is a task ID.

### 7.3 Execute / submit as requester

Save as `submit.json` (provided). Use a new reference for a new claim. Repeating execute with the same reference can start another instance.
''')
submission={'correlationId':'CLAIM-001','input':{'requesterId':'requester','firstName':'Pranav','lastName':'Shah','claimAmount':6000}}
block(submission)
command('''$run = curl.exe -sS -X POST "$base/elsa/api/workflow-definitions/expense-claim-v1/execute" -H "Authorization: Bearer $($tokens['requester'])" -H "Content-Type: application/json" --data-binary "@submit.json" | ConvertFrom-Json
$workflowId = $run.workflowState.id
$run.workflowState | Select-Object id, status, subStatus
# Expect Suspended while waiting at A; HTTP 200 alone does not prove success.''')
state=capture('Start approved-6000')['response']['workflowState']
text('Captured HTTP 200 excerpt from a 6000 submission; IDs/reference differ from the template:')
block({'workflowState':{k:state[k] for k in ('id','definitionId','correlationId','status','subStatus')}})
text('''### 7.4 Find available A work as Alice
''')
command('''$available = curl.exe -sS "$base/elsa/api/user-tasks?scope=available&workflowInstanceId=$workflowId&includeTotalCount=true" -H "Authorization: Bearer $($tokens['alice'])" | ConvertFrom-Json
$available.items | Select-Object id, title, status, revision
$taskId = $available.items[0].id
$revision = $available.items[0].revision''')
items=capture('Find A: task')['response']['items']
text('Observed initial A task shape (excerpt from the captured admin overview):')
block({'items':[{k:items[0][k] for k in ('id','title','status','workflowInstanceId','revision')}],'totalCount':1})
text('''Native scopes: `available` for eligible claimable work; `assigned` for owned work; `all` for elevated overview. A valid token from an unrelated user may see no tasks. Do not use the requester token to claim A.

### 7.5 Claim with the latest revision

Generate `claim.json` for the task just read:
''')
command('''@{ expectedRevision = $revision; operationId = [guid]::NewGuid().ToString() } | ConvertTo-Json | Set-Content -Encoding utf8NoBOM claim.json
$claim = curl.exe -sS -X POST "$base/elsa/api/user-tasks/$taskId/claim" -H "Authorization: Bearer $($tokens['alice'])" -H "Content-Type: application/json" --data-binary "@claim.json" | ConvertFrom-Json
$revision = $claim.task.revision''')
text('Example body:')
block({'expectedRevision':1,'operationId':'11111111-1111-4111-8111-111111111111'})
def task_excerpt(title):
    c=capture(title); b=c['response']; t=b['task']
    return {**{k:b[k] for k in ('operationId','status','revision')},'task':{k:t[k] for k in ('id','status','revision','assignee') if k in t}}
text('Captured HTTP 200 claim response excerpt:')
block(task_excerpt('Claim A: Initial review (Alice/Amy bucket)'))
text('''`expectedRevision` is the task concurrency revision. It is not a definition version. Read the task again after changes. `operationId` is a UUID for one logical command: use a new one for a new command; retry uncertain requests with the **same ID, revision and payload**.

### 7.6 Read current details and approve as owner
''')
command('''$task = curl.exe -sS "$base/elsa/api/user-tasks/$taskId" -H "Authorization: Bearer $($tokens['alice'])" | ConvertFrom-Json
@{ expectedRevision = $task.revision; operationId = [guid]::NewGuid().ToString(); actionKey = 'Approve'; data = @{ comment = 'Initial checks passed' } } | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8NoBOM approve.json
curl.exe -sS -X POST "$base/elsa/api/user-tasks/$taskId/complete" -H "Authorization: Bearer $($tokens['alice'])" -H "Content-Type: application/json" --data-binary "@approve.json"''')
block({'expectedRevision':2,'operationId':'22222222-2222-4222-8222-222222222222','actionKey':'Approve','data':{'comment':'Initial checks passed'}})
c=capture('Approve A: Initial review (Alice/Amy bucket)')
text(f"Captured HTTP {c['statusCode']} approval response excerpt:")
block(task_excerpt(c['title']))
text('''HTTP 202/status accepted/task Completing means Elsa accepted the command and is resuming the workflow. Poll for completion and the next stage; do not assume the next task is visible immediately.

### 7.7 Continue B, conditional C, then D
''')
command('''# B: Bella or Bob lists available work and claims the new task.
curl.exe -sS "$base/elsa/api/user-tasks?scope=available&workflowInstanceId=$workflowId" -H "Authorization: Bearer $($tokens['bob'])"
# Repeat claim -> GET detail -> complete Approve using B's ID/revision and Bob's token.

# C exists only if B approved with amount > 5000. It is already assigned to Clara.
curl.exe -sS "$base/elsa/api/user-tasks?scope=assigned&workflowInstanceId=$workflowId" -H "Authorization: Bearer $($tokens['clara'])"
# GET C detail -> complete Approve with Clara's token. No bucket claim is needed.

# D is already assigned to Dan; it follows B directly or follows C.
curl.exe -sS "$base/elsa/api/user-tasks?scope=assigned&workflowInstanceId=$workflowId" -H "Authorization: Bearer $($tokens['dan'])"
# GET D detail -> complete Approve with Dan's token.''')
text('''Always replace taskId, expectedRevision and actor token for each task. Do not reuse A's ID to complete B.

### 7.8 Reject a review task

This is the alternative to approval on an active owned task. After a task has completed, start a separate test claim to try rejection.
''')
command('''$owner = 'clara' # Example: rejecting C; use alice/bob/dan for their owned task.
$task = curl.exe -sS "$base/elsa/api/user-tasks/$taskId" -H "Authorization: Bearer $($tokens[$owner])" | ConvertFrom-Json
@{ expectedRevision = $task.revision; operationId = [guid]::NewGuid().ToString(); actionKey = 'Reject'; data = @{ reason = 'High-value claim declined' } } | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8NoBOM reject.json
curl.exe -sS -X POST "$base/elsa/api/user-tasks/$taskId/complete" -H "Authorization: Bearer $($tokens[$owner])" -H "Content-Type: application/json" --data-binary "@reject.json"''')
block({'expectedRevision':1,'operationId':'33333333-3333-4333-8333-333333333333','actionKey':'Reject','data':{'reason':'High-value claim declined'}})
c=capture('Reject C: High value approval (Clara)')
text(f"Captured HTTP {c['statusCode']} C rejection response excerpt:")
block(task_excerpt(c['title']))
text('''The client never supplies the next destination. `rejectTo` determines it: A -> requester, B -> A, C -> Rejected, D -> requester. Rejection without a reason is HTTP 422, not a successful rejection.

### 7.9 Correct and resubmit as requester

After A or D returns a claim, retrieve the **new correction task ID** as requester. Do not complete the rejected review task again.
''')
command('''$corrections = curl.exe -sS "$base/elsa/api/user-tasks?scope=assigned&workflowInstanceId=$workflowId" -H "Authorization: Bearer $($tokens['requester'])" | ConvertFrom-Json
$correctionId = $corrections.items[0].id
$correction = curl.exe -sS "$base/elsa/api/user-tasks/$correctionId" -H "Authorization: Bearer $($tokens['requester'])" | ConvertFrom-Json
@{ expectedRevision = $correction.revision; operationId = [guid]::NewGuid().ToString(); actionKey = 'Resubmit'; data = @{ attributes = @{ firstName = 'Pranav'; lastName = 'Shah'; claimAmount = 4000 } } } | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8NoBOM resubmit.json
curl.exe -sS -X POST "$base/elsa/api/user-tasks/$correctionId/complete" -H "Authorization: Bearer $($tokens['requester'])" -H "Content-Type: application/json" --data-binary "@resubmit.json"''')
block({'expectedRevision':1,'operationId':'44444444-4444-4444-8444-444444444444','actionKey':'Resubmit','data':{'attributes':{'firstName':'Pranav','lastName':'Shah','claimAmount':4000}}})
c=capture('Resubmit Correct and resubmit Expense Claim')
text(f"Captured HTTP {c['statusCode']} resubmission response excerpt:")
block(task_excerpt(c['title']))
text('''### 7.10 Final status and complete journey
''')
command('''curl.exe -sS "$base/elsa/api/workflow-instances/$workflowId" -H "Authorization: Bearer $($tokens['admin'])"
curl.exe -sS "$base/elsa/api/user-tasks?scope=all&workflowInstanceId=$workflowId&includeTotalCount=true" -H "Authorization: Bearer $($tokens['admin'])"
curl.exe -sS "$base/elsa/api/user-tasks/$taskId/events" -H "Authorization: Bearer $($tokens['admin'])"
curl.exe -sS "$base/elsa/api/workflow-instances/$workflowId/journal" -H "Authorization: Bearer $($tokens['admin'])"
curl.exe -sS "$base/elsa/api/workflow-instances/$workflowId/execution-state" -H "Authorization: Bearer $($tokens['admin'])"''')
for title in ('Finished instance (Completed)','Finished instance (Rejected)'):
    b=capture(title)['response']; text(f'Captured {title} excerpt:')
    block({'id':b['id'],'status':b['status'],'workflowState':{'output':b['workflowState']['output']}})
text('''Completed task data preserves the snapshot for that stage visit. Combine task details, each task's events and the workflow journal for the journey. There is no separate custom journey API, frontend or numeric attribute-revision resource. Native pagination applies when many stage visits exist; follow the endpoint's pagination schema.

### 7.11 Errors and response interpretation

| Result | Meaning |
|---|---|
| 200 login, accessToken present | Authenticated; save the accessToken |
| 200 execute, subStatus Suspended | Started and waiting for human work |
| 200 execute with a faulted subStatus/incidents | Activity failed validation/execution; HTTP 200 alone is insufficient |
| 202 completion accepted | Poll for resume/next task |
| 401 | Missing, invalid or expired bearer token |
| 403 | Missing permission or denied operation |
| 404 task | Missing or hidden from this identity |
| 409 | Revision/operation conflict; competing claim may instead yield 403/404 |
| 422 form-invalid | Invalid response data, e.g. missing rejection reason or edited attributes during review |
| Empty list | Check actor membership, scope, instance ID, current stage and version |

Captured invalid rejection response (HTTP 422):
''')
block(capture('Reject without reason is invalid')['response'])
text('''The host adds X-Trace-Id to responses. Logs include method/path/status/duration, workflow ID, stage/visit, sanitized database target and database SQLSTATE. Request bodies, passwords, bearer tokens and attribute values are not intentionally logged. Refresh tokens/signing keys must not be exposed in diagnostic output.

## 8. Host integration: minimum registrations

The source registers all Elsa packages at 3.9.0. Copy only packages needed by the receiving app; preserve its existing target/framework unless incompatible. This sample host additionally enables native JavaScript/Flowchart for the separate native-only experiment; the configuration interpreter below does not depend on those expressions for its routing.
''')
file_block('IlWorkflow.Api.csproj','xml')
block('''using Elsa.Extensions;
using Elsa.UserTasks.Activities;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Services;
using IlWorkflow.Api.Tickets;

// Merge into the receiving application's existing AddElsa configuration.
builder.Services.AddElsa(elsa =>
{
    elsa.UseWorkflowsApi()
        .UseUserTasks()
        .AddActivitiesFrom<UserTask>()
        .AddActivitiesFrom<TicketFlow>();
    // Keep the real app's authentication, persistence and Swagger integration.
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IUserTaskFormProvider, TicketFormProvider>();

// This research app needs native service lifetime overrides in Elsa 3.9.
// Both native implementations resolve scoped runtime/mediator services.
builder.Services.AddScoped<IUserTaskNotificationSink, DefaultUserTaskNotificationSink>();
builder.Services.AddScoped<IUserTaskWorkflowResumer, DefaultUserTaskWorkflowResumer>();
''','csharp')
text('''This is an integration excerpt, not a standalone production Program.cs. The exact research host appears in appendix D. Do not create a second competing AddElsa host if the receiving app already has one. Register the activity and form provider on its existing Elsa setup. Keep native authentication, workflow management/runtime and endpoint/middleware wiring in place.

The current TicketFlow checks the HTTP caller subject (`ClaimTypes.NameIdentifier` or `sub`) and the original requester. It assumes synchronous execute with an HttpContext. **Do not replace execute with background dispatch without redesigning this ownership check.**

The sample's member validation reads `Identity:Users` from IConfiguration. Replace those research-only lookups with the receiving application's real user/role directory while preserving candidate and requester validation. Keep the same IDs in JWT identities and ParticipantReference IDs. The sample uses provider `default`, user type, and empty tenant; tenant-aware apps must adapt that consistently.

## 9. PostgreSQL: configuration versus evidence

Research database target: localhost:5432, database ilworkflow, username postgres. Store real credentials and signing keys in user secrets or the receiving application's secret provider. Do not copy development secrets into another app's committed configuration.
''')
command('''dotnet user-secrets set "ConnectionStrings:Elsa" "Host=localhost;Port=5432;Database=ilworkflow;Username=postgres;Password=<local-password>"
dotnet user-secrets set "Identity:Tokens:SigningKey" "<random-secret-of-at-least-32-characters>"
dotnet user-secrets set "Identity:Admin:Password" "<local-admin-password>"
# Research host only: SwaggerPreview=false enables persistence.
# A different receiving app may use its own environment/configuration convention.''')
text('''The host registers PostgreSQL persistence for management/runtime/identity/UserTasks. In Development it checks/creates only ilworkflow through the postgres maintenance database; Elsa applies migrations. A role needs suitable connection/database creation permissions.

Do not overstate the results:

- The local server previously returned recovery-mode SQLSTATE 57P03.
- Separate native Elsa 3.9 UserTasks experiments found TagsJson model/migration mapping and nullable JSON deserialization issues.
- Prior custom PostgreSQL compatibility adapters were removed. The final restored app does not contain those patches.
- The 148 captured exchanges are preview/in-memory evidence, not restart-persistence evidence.
- The receiving Codex must reproduce provider failures, identify the cause and test any explicit workaround before claiming PostgreSQL support. Never reset an existing database to hide migration problems.

## 10. Instructions to the receiving Codex

Copy the following prompt when starting work in the other application:
''')
block('''Implement the Elsa 3.9 ticket workflow described in this handoff in the CURRENT application.

1. Inspect the current repository, AGENTS.md, target framework, authentication, database and existing Elsa setup. Preserve existing work. Do not replace Program.cs or authentication wholesale.
2. Summarize the integration points briefly, then implement. Ask only for genuinely missing blocking information. Do not silently substitute a native-only Complete workflow.
3. Keep Elsa packages at 3.9.0 and use native Elsa User Tasks/APIs. Do not create a second task engine or custom ticket REST controllers just for this sample.
4. Port the THREE source files in appendices A-C. Adapt namespaces and real user-directory/subject/tenant mapping. Keep Tickets.TicketFlow as the Elsa type name unless you also update the definition JSON. Keep the configurationJson Literal/String wrapper.
5. Register the custom activity, native UserTask, TicketFormProvider and required native scoped lifetimes in the existing Elsa host. Keep native identity/runtime/persistence/Swagger wiring working. No Elsa Studio/frontend is requested.
6. Start in a disposable research/development environment. Use JSON configuration fixtures, native API publication and one workflow instance per claim. Do not add unnecessary application tables. Existing app identity can be used instead of sample JSON accounts.
7. Implement the exact A/B bucket, C threshold, D owner, Approve/Reject/Resubmit behaviors. B is NOT round robin. Rejection at D resumes D after correction, even if the amount increases. Do not change this policy without explicit human direction.
8. Preserve configuration snapshots and native task snapshots. Final business outcome must remain in workflowState.output.ticketStatus after completion; activity-local properties alone are insufficient once Elsa prunes completed activity contexts.
9. Validate membership and frozen attributes. Do not trust client-supplied requester IDs, assignees or routing destinations. Preserve the ownership check; redesign it before using background dispatch.
10. Run the acceptance cases below through real APIs. Record sanitized actual requests/responses. Check business status and workflow incidents, not only HTTP codes. Test task operation retries using the same operationId/payload; do not claim execute is idempotent.
11. If PostgreSQL is required, verify native migrations, task read/write and restart/resume on the receiving database. Document any provider failures. Do not claim the source research run proved persistence.
12. Deliver a working implementation, request collection and concise setup/validation notes. State which behavior was tested, which remains unverified and every material deviation from this handoff. Do not claim configurationJson is built into Elsa.

Use this document as the behavioral reference. Reuse the appendix code rather than inventing additional abstractions. Do not add attachments, email, SLA, delegation, custom admin UI or Elsa 4 migration.''','text')
text('''### Receiving-agent acceptance checks

Already verified in the source research app:

- 4000 and 5000: A -> B -> D -> business Completed.
- 6000: A -> B -> C -> D -> business Completed.
- Reject A: requester task, correction to 4000, new A -> B -> D; same workflow ID.
- Reject B: new A visit, then B and D.
- Reject C: terminal business Rejected; Elsa status Finished.
- Reject D: requester task, correction to 6000, resume D; C is not replayed.
- Reject without reason -> 422.
- Attribute edit embedded in a review decision -> 422.
- Replaying the same decision operation ID/payload does not create a second transition.
- Unauthenticated task list -> 401; requester publishing -> 403; unrelated task detail -> 404.
- A competing/stale claim is denied. The restored suite tests this serially; do not describe it as a full load/concurrency test.
- Separate build: zero warnings/errors.

Required additional validation before production claims (not proven by the final source run):

- Real account disabling, tenant boundaries and external group membership.
- Concurrent claim races under the selected persistence provider.
- Blocked/no-matching-assignee behavior and native supervisor reassignment.
- New configuration publication while older instances remain active.
- PostgreSQL migrations, task querying and durable restart/resume.
- Reliable/idempotent submission if the receiving app requires it.

Use `Tests/ConfigurationApi.ps1` as the starting API suite. It creates disposable test workflows and captures exchanges. Do not run it against a shared production database. The source's ordinary Debug build was blocked by a still-running Visual Studio app locking its executable; the isolated bin/configresearch build and API checks passed. Stop debugging before rebuilding the ordinary output.

## 11. Boundaries and known gaps

- No automatic round robin; B is a user bucket.
- No full ticket-type CRUD/version publishing subsystem. Native Elsa definition versions exist; the interpreter snapshots configuration at start, but application-level immutable configuration authoring APIs are not implemented.
- Configuration validation is at execution, not publish time; malformed/null-heavy configuration is not a fully hardened public schema contract.
- New attributes need matching workflow input declarations. Initial execution builds values from configured attributes; unrelated extra workflow input keys are not necessarily rejected as unknown business attributes.
- No custom ticket/history tables, custom journey endpoint, attachment support, notifications, frontend, unanimous group approval, delegation or SLA handling.
- No guaranteed disabled-account enforcement beyond whatever the receiving identity system supplies.
- No demonstrated transactional outbox or start idempotency. Native operation IDs/revisions cover task commands, not all external application writes.
- No migration of old active instances from earlier experiments. Do not assume changing activity code safely upgrades persisted callbacks/forms/configuration from a different build. Test upgrades separately or finish old instances before changing behavior.
- No completed-journey persistence claim for the final stock PostgreSQL setup.

## Appendix A. Ticket configuration models and rules (exact source)
''')
file_block('Tickets/TicketConfiguration.cs','csharp')
text('## Appendix B. TicketFlow activity (exact source)')
file_block('Tickets/TicketFlow.cs','csharp')
text('## Appendix C. Form provider (exact source)')
file_block('Tickets/TicketFormProvider.cs','csharp')
text('''## Appendix D. Research host reference (exact source)

Reference only: merge the necessary registrations into the receiving host. Do not overwrite its authentication or database setup with this entire file.
''')
file_block('Program.cs','csharp')
text('''## Appendix E. Development identity fixture

Only for an isolated research host. These are sample password hashes for the documented development credentials. Prefer the receiving application's identity system in an existing app. Preserve native permission checks; merely adding a role name without its Elsa permissions is not sufficient.
''')
file_block('Samples/identity.json','json')
text('## Appendix F. Exact reusable HTTP request collection')
file_block('Tickets.http','http')
text('''## Appendix G. Exact API verification script

PowerShell 7. Set BaseUrl to the receiving research server. This writes docs/api-captures.json with issued authentication tokens redacted.
''')
file_block('Tests/ConfigurationApi.ps1','powershell')
text('''## Companion files

- workflow-requests/expense-claim.definition.json: complete native publication request.
- workflow-requests/expense-claim.configuration.json: readable configuration source.
- workflow-requests/submit.json and login-*.json: development curl body fixtures.
- api-captures.json: all 148 original exchanges, authentication secrets redacted.
- Elsa-3.9-Ticket-Workflow-API-Guide.pdf: API-only walkthrough.

All essential source/configuration/request bodies are embedded in this Markdown, so it can be handed to another Codex on its own. Companion files make the curl examples easier to run. No real production secrets are included.
''')
path=out/'ELSA-WORKFLOW-CODEX-HANDOFF.md'
path.write_text('\n\n'.join(parts)+'\n',encoding='utf-8')
for source,name in [('Samples/Definitions/expense-claim.definition.json','expense-claim.definition.json'),('Samples/expense-claim.configuration.json','expense-claim.configuration.json')]:
    shutil.copyfile(root/source,requests/name)
(requests/'submit.json').write_text(json.dumps(submission,indent=2)+'\n',encoding='utf-8')
for user in ('admin','configurator','requester','alice','amy','bella','bob','clara','dan','supervisor'):
    (requests/f'login-{user}.json').write_text(json.dumps({'username':user,'password':'password' if user=='admin' else 'Password123!'},indent=2)+'\n',encoding='utf-8')
shutil.copyfile(root/'docs/api-captures.json',out/'api-captures.json')
print(json.dumps({'markdown':str(path),'bytes':path.stat().st_size,'lines':len(path.read_text().splitlines()),'companion_requests':len(list(requests.glob('*.json')))}))
