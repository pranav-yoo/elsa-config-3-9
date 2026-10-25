param([string]$BaseUrl = 'http://localhost:5000')
$ErrorActionPreference = 'Stop'
$tokens = @{}
function Call([string]$user, [string]$method, [string]$path, $body = $null, [int[]]$expect = @(200)) {
    $parameters = @{ Uri = "$BaseUrl$path"; Method = $method; SkipHttpErrorCheck = $true }
    if ($user) { $parameters.Headers = @{ Authorization = "Bearer $($tokens[$user])" } }
    if ($null -ne $body) { $parameters.ContentType = 'application/json'; $parameters.Body = $body | ConvertTo-Json -Depth 40 -Compress }
    $response = Invoke-WebRequest @parameters
    if ([int]$response.StatusCode -notin $expect) { throw "$method $path returned $($response.StatusCode), expected $($expect -join ','). Trace=$($response.Headers['X-Trace-Id'])" }
    if ($response.Content) { return $response.Content | ConvertFrom-Json }
}
foreach ($user in @('admin','configurator','requester','alice','amy','bella','bob','clara','dan','supervisor')) {
    $password = if ($user -eq 'admin') {'password'} else {'Password123!'}
    $login = Call '' POST '/elsa/api/identity/login' @{username=$user;password=$password}
    if (-not $login.accessToken) {throw "Native configuration login failed for $user"}
    $tokens[$user] = $login.accessToken
}
$definition = Get-Content "$PSScriptRoot/../Samples/Definitions/expense-claim-native.definition.json" -Raw | ConvertFrom-Json
$allowed = @('Elsa.Flowchart','Elsa.SetVariable','Elsa.FlowDecision','Elsa.UserTask')
if (@($definition.model.root.activities | Where-Object {$_.type -notin $allowed}).Count) {throw 'Definition contains a custom activity'}
Call '' GET '/elsa/api/user-tasks' $null @(401) | Out-Null
Call 'requester' POST '/elsa/api/workflow-definitions' $definition @(403) | Out-Null
Call 'configurator' POST '/elsa/api/workflow-definitions' $definition | Out-Null
$doc = Call '' GET '/swagger/v3/swagger.json'
if (@($doc.paths.PSObject.Properties.Name | Where-Object {$_ -like '/api/samples*'}).Count) {throw 'Custom API remains'}
function StartClaim([int]$amount) {
    $reference = "native-$amount-$([guid]::NewGuid().ToString('N'))"
    $run = Call 'requester' POST '/elsa/api/workflow-definitions/expense-claim-native-v1/execute' @{
        correlationId=$reference;input=@{ticketReference=$reference;requesterId='requester';firstName='Pranav';lastName='Shah';claimAmount=$amount}
    }
    if ($run.workflowState.subStatus -ne 'Suspended') {throw "Workflow did not suspend: $($run.workflowState.subStatus)"}
    return $run.workflowState.id
}
function Active([string]$workflow, [string]$prefix) {
    for ($i=0; $i -lt 30; $i++) {
        $list = Call 'admin' GET "/elsa/api/user-tasks?scope=all&workflowInstanceId=$workflow"
        $task = $list.items | Where-Object {$_.title.StartsWith($prefix) -and $_.status -in @('Available','Assigned')} | Select-Object -First 1
        if ($task) {return $task}
        Start-Sleep -Milliseconds 100
    }
    throw "No active $prefix task for $workflow"
}
function Own($task,[string]$user) {
    if ($task.status -eq 'Available') {Call $user POST "/elsa/api/user-tasks/$($task.id)/claim" @{expectedRevision=$task.revision;operationId=[guid]::NewGuid().ToString()} | Out-Null}
    return Call $user GET "/elsa/api/user-tasks/$($task.id)"
}
function Complete($task,[string]$user) {
    $request = @{expectedRevision=$task.revision;operationId=[guid]::NewGuid().ToString();actionKey='Complete'}
    Call $user POST "/elsa/api/user-tasks/$($task.id)/complete" $request @(200,202) | Out-Null
    return $request
}
foreach ($amount in @(4000,5000,6000)) {
    $workflow = StartClaim $amount
    $a = Active $workflow 'A:'
    Call 'dan' GET "/elsa/api/user-tasks/$($a.id)" $null @(404) | Out-Null
    $a = Own $a 'alice'
    Call 'amy' POST "/elsa/api/user-tasks/$($a.id)/claim" @{expectedRevision=1;operationId=[guid]::NewGuid().ToString()} @(403,404,409) | Out-Null
    # A native task without a form provider rejects submitted data: this is a research finding.
    $unsupported = Call 'alice' POST "/elsa/api/user-tasks/$($a.id)/complete" @{expectedRevision=$a.revision;operationId=[guid]::NewGuid().ToString();actionKey='Complete';data=@{decision='Approve'}} @(422)
    $request = Complete $a 'alice'
    Call 'alice' POST "/elsa/api/user-tasks/$($a.id)/complete" $request @(200,202) | Out-Null
    $b = Active $workflow 'B:'
    if ($b.status -ne 'Available' -or $b.assignee) {throw 'B must be a native Bella/Bob bucket, not automatic round robin'}
    $b = Own $b 'bob'; Complete $b 'bob' | Out-Null
    if ($amount -gt 5000) {$c = Own (Active $workflow 'C:') 'clara'; Complete $c 'clara' | Out-Null}
    $d = Own (Active $workflow 'D:') 'dan'; Complete $d 'dan' | Out-Null
    $instance = Call 'admin' GET "/elsa/api/workflow-instances/$workflow"
    if ($instance.status -ne 'Finished') {throw "Workflow did not finish: $($instance.status)"}
    $events = Call 'bob' GET "/elsa/api/user-tasks/$($b.id)/events"
    if (-not $events.items) {throw 'Native task history is missing'}
    Write-Host "PASS: $amount follows A-B-$(if($amount -gt 5000){'C-'})D; native task history and Finished status verified"
}
$workflow = StartClaim 4000
$a = Active $workflow 'A:'
$taskId = $a.id; $revision = $a.revision; $aliceToken = $tokens.alice; $amyToken = $tokens.amy
$statuses = 1,2 | ForEach-Object -Parallel {
    $token = if ($_ -eq 1) {$using:aliceToken} else {$using:amyToken}
    $response = Invoke-WebRequest "$using:BaseUrl/elsa/api/user-tasks/$using:taskId/claim" -Method Post -ContentType application/json -Headers @{Authorization="Bearer $token"} -Body (@{expectedRevision=$using:revision;operationId=[guid]::NewGuid().ToString()}|ConvertTo-Json) -SkipHttpErrorCheck
    [int]$response.StatusCode
} -ThrottleLimit 2
if (@($statuses | Where-Object {$_ -eq 200}).Count -ne 1) {throw 'Concurrent claim did not produce one owner'}
Write-Host 'PASS: configuration-based accounts, native-only activities/APIs, task authorization, concurrent claims and idempotent completion'
Write-Host 'CONFIRMED LIMITATION: completion data requires a form provider (HTTP 422); no form adapter is registered'
