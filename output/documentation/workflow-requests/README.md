# Run the requests in order

Open either `.http` file in VS Code with the **REST Client** extension. Use **Send Request** on each numbered request, top to bottom. Named responses fill tokens, generated user IDs, task IDs and revisions automatically. Do not run every request at once; the rejection alternatives at the end are mutually exclusive with approval.

## Existing sample accounts

1. Start normally: `dotnet run --launch-profile http`.
2. Open **Tickets.http**.
3. Follow its main sequence: admin login, configurator login/publication, requester login/submission, Alice at A, Bob at B, Clara at C, Dan at D, final status and history.

## Create roles and users through Elsa APIs

1. Start a fresh disposable server with `dotnet run --launch-profile http -- --Identity:UseConfigurationProviders=false`.
2. Open **Identity-And-ABCD.http**.
3. Run admin login, all five role creations, all nine user creations, then the complete A-B-C-D sequence.
4. Do not repeat the creation requests against an already provisioned store. API-created user IDs are generated; the request file uses response IDs in the workflow's literal configurationJson.

The administrator is bootstrapped by Elsa at startup; there is no anonymous create-admin request. Default credentials: admin/password; other research users/Password123!. Set `@adminPassword` if your configured bootstrap password differs.

Both modes currently default to in-memory preview storage: restart clears store-created accounts and workflows. JSON fixture mode recreates its configured accounts. This collection does not certify PostgreSQL durability.

## Approval and rejection

The main example submits 6000 and follows A-B-C-D. At 4000 or 5000, skip all C requests and proceed from B to D. A and B need a claim; C and D are directly assigned.

If the next task list is empty after an HTTP 202 completion, repeat its GET until the next task appears. Refresh detail before using `expectedRevision`. A new command needs a new operation ID; retries need the same ID, revision and body, so save the original generated request before retrying.

To test rejection, submit another claim and stop before approving the desired stage. Use that stage's Reject alternative instead: A -> requester -> A; B -> A; C -> terminal Rejected; D -> requester -> D. Run requester correction/resubmission only after A/D returns a claim. Correction at D resumes D; it does not replay B/C even if the amount changes.

Final Elsa status is Finished for both business outcomes. Inspect `workflowState.output.ticketStatus` for Completed or Rejected.
