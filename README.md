# Configurable ticket approvals with Elsa 3.9

The application exposes a small configuration controller and uses native Elsa APIs for workflow execution, instances, journals and User Tasks. Elsa packages remain pinned to 3.9.0.

## Start

```powershell
dotnet restore
dotnet build
# First setup only, if local connection/admin credentials are not configured:
pwsh -NoProfile -ExecutionPolicy Bypass -File ./Setup.ps1
# First external-token setup; this also prints the username provider's John token:
pwsh -NoProfile -ExecutionPolicy Bypass -File ./New-TestAccessToken.ps1 -Provider Username -Identity john
dotnet run --launch-profile http
```

Open [Swagger](http://localhost:5000/swagger). Development now defaults to **PostgreSQL persistence** (`SwaggerPreview=false`, `Identity:UseConfigurationProviders=false`). Database: `localhost:5432/ilworkflow`, account `postgres`; passwords and signing keys belong in .NET user secrets. Visual Studio: right-click project -> Manage User Secrets. Setup changes the local admin password; existing database accounts are not reset by restart.

For documentation without PostgreSQL, run `dotnet run --launch-profile http -- --SwaggerPreview true`. Preview skips every database migration, needs no external signing key, and returns 503 for configuration/business operations. Use persistent mode to submit tickets or work on tasks.

Elsa Identity EF stores users/roles. Elsa management/runtime EF stores definitions/instances/bookmarks. Elsa User Tasks EF stores tasks, candidate snapshots, revisions, operations and events. Our own EF context stores only version-linked configuration in the `Application` schema. The instance ID is the ticket reference; submitted values and correction snapshots stay in Elsa.

## External employees: GET only, no registration database

The two fictional Development masters each return five employees: John, Alice, Bob, Clara and Dan.

- `GET /api/demo-masters/usernames`: username-based records.
- `GET /api/demo-masters/emails`: email-based records.
- `GET /api/external-providers` and `GET /api/external-providers/{providerId}/members`: provider selection for an authenticated configurator.

Copy the returned `participantId` directly into a stage's `members` and `userId`. There is **no `POST /api/external-members`**, external-member table, password account or first-login database write. IDs are computed from provider ID and master member ID, for example `external:username-demo:U001`. Elsa persists that ID in its native task assignment. The email provider's John has a different ID. Internal configurators manage definitions and internal requesters start instances; external employees receive only Approver permissions and act on eligible tasks after creation. Their tokens cannot execute workflows.

Settings in [Samples/external-providers.json](Samples/external-providers.json) control each provider's issuer, audience, `matchClaim`, `memberField` and `ignoreCase`. Username tokens default to `name=john`; email tokens use `email=john@example.test`. The signature, issuer, audience and expiry are validated before claim matching. A display name such as `John Doe` does not match `username=john` unless you explicitly choose `memberField=displayName`.

Follow [External-Masters.http](External-Masters.http) from GET selection through workflow publication, execution and native approval/rejection. These demo masters and signing handlers are enabled only in Development. Real Azure/Keycloak trust and master adapters remain future work. Existing tasks with the old manually mapped John ID are not rewritten.

## Ordered request collections

1. [Authentication.http](Authentication.http): native roles/users and local/external authentication. Inspect existing users/roles first; provision only missing records. Test accounts are `configured-requester`, `configured-alice`, `configured-amy`, `configured-bob`, `configured-clara`, `configured-dan`, `configured-configurator`, `configured-supervisor`, `configured-outsider`, with sample password `Password123!`.
2. [Workflow-Configuration.http](Workflow-Configuration.http): copy the generated employee IDs, create, POST-update, validate and publish. Then copy `definitionId` into the next file.
3. [Ticket-Workflow.http](Ticket-Workflow.http): submit and work through A -> B -> C -> D, inspect history, or choose the documented rejection/correction alternatives.

Run one request at a time in VS Code REST Client. Named response references transfer tokens, IDs and revisions within each file. Copy only the explicitly marked values between files. After 202 completion, retry the next task GET until it appears. A new operation gets a new UUID; retry the same operation using its saved original UUID/body/revision.

`POST /api/workflow-configurations` creates when `id` is absent/null/zero and updates an existing draft for a positive ID. Updates require `expectedRevision`. Missing positive IDs return 404; stale revisions and published edits return 409. There is no custom PUT or `/api/tickets` API.

The example routes amounts above 5000 through C; 5000 and below bypass C. A rejection returns to correction/A, B rejection returns to A, C rejection ends Rejected, D rejection returns to correction/D. Correction never implicitly replays earlier stages: its configured `stageKey` chooses where approval resumes.

## Developer documentation

Read [the developer guide](docs/DEVELOPER-GUIDE.md) for file/function responsibilities, middleware versus controller, persistence, validation, task callbacks, security boundaries and provider compatibility notes. Source comments explain decisions and execution boundaries rather than repeating each statement.

## Verification

Use a research database: the checks create accounts, definitions, instances and task history.

```powershell
python Tests/ConfiguredWorkflows.py --base-url http://localhost:5000
# Restart the application, then:
python Tests/ConfiguredWorkflows.py --base-url http://localhost:5000 --verify-restart
# Executes the actual two .http main sequences using the provisioned research accounts:
python Tests/VerifyConfiguredRequests.py --base-url http://localhost:5000
# Execute all 18 requests in External-Masters.http, including approve and reject:
python Tests/VerifyConfiguredRequests.py --base-url http://localhost:5000 --external-masters
# GET masters, provider isolation, token rejection and native external task approval:
python Tests/ExternalMasters.py --base-url http://localhost:5000
# Restart again, then verify its pending external assignments:
python Tests/ExternalMasters.py --base-url http://localhost:5000 --verify-restart
# When running documentation preview instead of persistent mode:
python Tests/ConfiguredWorkflows.py --base-url http://localhost:5000 --verify-preview
```

Verified against local PostgreSQL: 4000/5000/6000 routing; approval/rejection/correction; conflicting claims and revisions; missing-assignee supervisor repair; unauthorized access; immutable configuration/version pinning; and restart recovery of user IDs/login, configuration, pending tasks, callbacks and correction history. The two local HTTP sequences passed 30 requests, and the external master sequence passed 18 requests including approval and rejection. Both provider tokens passed task authorization/isolation checks without creating local users.

No tokens are printed or saved by the test runners. The ignored restart manifest contains only identifiers. `docs/build_configured_requests.py` regenerates the new collections from the acceptance fixture.

## Boundaries

This phase implements sequential/conditional approvals and requester corrections. Parallel all/any/N-of-M, round robin, asynchronous submission outbox, attachments, notifications and SLA escalation are deferred. Production Azure/Keycloak integration still needs the real master-login contract; local Elsa accounts and both development master-token profiles work alongside each other.

Elsa 3.9 PostgreSQL User Tasks requires the two narrow compatibility fixes documented in the guide. Keyword task `search` returns an explanatory 400 because the native predicate includes tags stored through the provider compatibility converter; use scope/status/instance/reference filters. No replacement task engine is introduced.

Legacy `Tickets.http`, `Identity-And-ABCD.http`, old sample definitions and their historical reports describe the earlier interpreter and remain as research material. They are not the setup/run guide for the new configuration path. Legacy workflow execution is restricted to supervisors/admins through the new API guard.
