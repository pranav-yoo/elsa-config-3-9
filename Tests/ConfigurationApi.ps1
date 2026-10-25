param([string]$BaseUrl = 'http://localhost:5007')
$ErrorActionPreference = 'Stop'
$tokens = @{}
$captures = [System.Collections.Generic.List[object]]::new()
function Call([string]$user, [string]$method, [string]$path, $body=$null, [int[]]$expected=@(200), [string]$title='') {
    $parameters=@{Uri="$BaseUrl$path";Method=$method;SkipHttpErrorCheck=$true}
    if($user){$parameters.Headers=@{Authorization="Bearer $($tokens[$user])"}}
    if($null -ne $body){$parameters.ContentType='application/json';$parameters.Body=$body | ConvertTo-Json -Depth 60 -Compress}
    $response=Invoke-WebRequest @parameters
    $result=if($response.Content){$response.Content | ConvertFrom-Json}else{$null}
    if([int]$response.StatusCode -notin $expected){throw "$method $path -> $($response.StatusCode), expected $expected; response: $($response.Content)"}
    $safeBody=if($null -ne $body){$body | ConvertTo-Json -Depth 60 | ConvertFrom-Json}else{$null}
    if($path -eq '/elsa/api/identity/login'){
        $safeBody.password='[development sample password]'
        $safeResult=$result | ConvertTo-Json -Depth 60 | ConvertFrom-Json
        foreach($field in @('accessToken','refreshToken','idToken')){if($safeResult.PSObject.Properties[$field]){$safeResult.$field='[REDACTED]'}}
    }else{$safeResult=$result}
    $captures.Add([ordered]@{title=$title;actor=$user;request=[ordered]@{method=$method;path=$path;authorization=$(if($user){"Bearer <$user-accessToken>"}else{''});body=$safeBody};statusCode=[int]$response.StatusCode;traceId=$response.Headers['X-Trace-Id'];response=$safeResult})
    return $result
}
foreach($user in @('admin','configurator','requester','alice','amy','bella','bob','clara','dan','supervisor')){
    $password=if($user -eq 'admin'){'password'}else{'Password123!'}
    $login=Call '' POST '/elsa/api/identity/login' @{username=$user;password=$password} @(200) "Login $user"
    if(-not $login.accessToken){throw "Login failed: $user"}
    $tokens[$user]=$login.accessToken
}
$definition=Get-Content "$PSScriptRoot/../Samples/Definitions/expense-claim.definition.json" -Raw | ConvertFrom-Json
if($definition.model.root.configurationJson.expression.type -ne 'Literal' -or $definition.model.root.configurationJson.typeName -ne 'String'){throw 'configurationJson must be a literal string'}
Call '' GET '/elsa/api/user-tasks' $null @(401) 'Unauthenticated tasks rejected' | Out-Null
Call 'requester' POST '/elsa/api/workflow-definitions' $definition @(403) 'Requester cannot publish' | Out-Null
Call 'configurator' POST '/elsa/api/workflow-definitions' $definition @(200) 'Publish configurationJson definition' | Out-Null
function StartClaim([int]$amount,[string]$scenario){
    $reference="DOC-$scenario-$([guid]::NewGuid().ToString('N').Substring(0,6))"
    $run=Call 'requester' POST '/elsa/api/workflow-definitions/expense-claim-v1/execute' @{correlationId=$reference;input=@{requesterId='requester';firstName='Pranav';lastName='Shah';claimAmount=$amount}} @(200) "Start $scenario"
    if($run.workflowState.subStatus -ne 'Suspended'){throw "Expected Suspended: $($run | ConvertTo-Json -Depth 20 -Compress)"}
    return $run.workflowState.id
}
function Active([string]$workflow,[string]$prefix){
    for($i=0;$i -lt 30;$i++){
        $list=Call 'admin' GET "/elsa/api/user-tasks?scope=all&workflowInstanceId=$workflow&includeTotalCount=true" $null @(200) "Find $prefix task"
        $task=$list.items | Where-Object {$_.title.StartsWith($prefix) -and $_.status -in @('Available','Assigned')} | Select-Object -First 1
        if($task){return $task}; Start-Sleep -Milliseconds 100
    }; throw "Missing active $prefix in $workflow"
}
function Own($task,[string]$user){
    if($task.status -eq 'Available'){Call $user POST "/elsa/api/user-tasks/$($task.id)/claim" @{expectedRevision=$task.revision;operationId=[guid]::NewGuid().ToString()} @(200) "Claim $($task.title)" | Out-Null}
    return Call $user GET "/elsa/api/user-tasks/$($task.id)" $null @(200) "Read $($task.title)"
}
function Decision($task,[string]$user,[string]$action,$data=@{},[string]$operationId=[guid]::NewGuid().ToString()){
    $body=@{expectedRevision=$task.revision;operationId=$operationId;actionKey=$action;data=$data}
    Call $user POST "/elsa/api/user-tasks/$($task.id)/complete" $body @(200,202) "$action $($task.title)" | Out-Null
    return $body
}
function Approve([string]$workflow,[string]$stage,[string]$user){$task=Own (Active $workflow "$($stage):") $user; Decision $task $user 'Approve' @{comment='Reviewed and approved'} | Out-Null}
function Finish([string]$workflow,[string]$status='Completed'){
    $instance=Call 'admin' GET "/elsa/api/workflow-instances/$workflow" $null @(200) "Finished instance ($status)"
    if($instance.status -ne 'Finished'){throw "Not Finished: $($instance.status)"}
    $state=$instance | ConvertTo-Json -Depth 100 -Compress
    if($state -notmatch ('ticketStatus.{0,30}'+$status)){throw "Business status $status not found in persisted activity properties"}
}
foreach($amount in @(4000,5000,6000)){
    $workflow=StartClaim $amount "approved-$amount"
    $a=Own (Active $workflow 'A:') 'alice'
    Call 'dan' GET "/elsa/api/user-tasks/$($a.id)" $null @(404) 'Unrelated approver cannot read A' | Out-Null
    Call 'alice' POST "/elsa/api/user-tasks/$($a.id)/complete" @{expectedRevision=$a.revision;operationId=[guid]::NewGuid().ToString();actionKey='Reject';data=@{}} @(422) 'Reject without reason is invalid' | Out-Null
    Call 'alice' POST "/elsa/api/user-tasks/$($a.id)/complete" @{expectedRevision=$a.revision;operationId=[guid]::NewGuid().ToString();actionKey='Approve';data=@{attributes=@{claimAmount=1}}} @(422) 'Attributes frozen during review' | Out-Null
    $decision=Decision $a 'alice' 'Approve' @{comment='Initial checks passed'}
    Call 'alice' POST "/elsa/api/user-tasks/$($a.id)/complete" $decision @(200,202) 'Duplicate decision replay' | Out-Null
    Approve $workflow 'B' 'bob'
    if($amount -gt 5000){Approve $workflow 'C' 'clara'}
    Approve $workflow 'D' 'dan'; Finish $workflow
    Call 'alice' GET "/elsa/api/user-tasks/$($a.id)/events" $null @(200) 'Task event history' | Out-Null
    Write-Host "PASS approval $amount"
}
$workflow=StartClaim 6000 'reject-A-correct'
$a=Own (Active $workflow 'A:') 'alice'; Decision $a 'alice' 'Reject' @{reason='Please correct the claim amount'} | Out-Null
$correction=Own (Active $workflow 'Correct') 'requester'
Decision $correction 'requester' 'Resubmit' @{attributes=@{firstName='Pranav';lastName='Shah';claimAmount=4000}} | Out-Null
Approve $workflow 'A' 'amy'; Approve $workflow 'B' 'bella'; Approve $workflow 'D' 'dan'; Finish $workflow
Write-Host 'PASS A rejection -> requester -> corrected A-B-D'
$workflow=StartClaim 4000 'reject-B-back-to-A'; Approve $workflow 'A' 'alice'
$b=Own (Active $workflow 'B:') 'bob'; Decision $b 'bob' 'Reject' @{reason='Initial checks need another review'} | Out-Null
Approve $workflow 'A' 'amy'; Approve $workflow 'B' 'bella'; Approve $workflow 'D' 'dan'; Finish $workflow
Write-Host 'PASS B rejection -> A'
$workflow=StartClaim 6000 'reject-C-terminal'; Approve $workflow 'A' 'alice'; Approve $workflow 'B' 'bob'
$c=Own (Active $workflow 'C:') 'clara'; Decision $c 'clara' 'Reject' @{reason='High-value claim declined'} | Out-Null; Finish $workflow 'Rejected'
Write-Host 'PASS C rejection -> Rejected'
$workflow=StartClaim 4000 'reject-D-correct'; Approve $workflow 'A' 'alice'; Approve $workflow 'B' 'bob'
$d=Own (Active $workflow 'D:') 'dan'; Decision $d 'dan' 'Reject' @{reason='Final details need correction'} | Out-Null
$correction=Own (Active $workflow 'Correct') 'requester'; Decision $correction 'requester' 'Resubmit' @{attributes=@{firstName='Pranav';lastName='Shah';claimAmount=6000}} | Out-Null
$dAgain=Own (Active $workflow 'D:') 'dan'; Decision $dAgain 'dan' 'Approve' @{} | Out-Null; Finish $workflow
Write-Host 'PASS D rejection -> requester -> D (same rejected stage)'
$workflow=StartClaim 4000 'claim-conflict'; $a=Active $workflow 'A:'
$oldRevision=$a.revision; $a=Own $a 'alice'
Call 'amy' POST "/elsa/api/user-tasks/$($a.id)/claim" @{expectedRevision=$oldRevision;operationId=[guid]::NewGuid().ToString()} @(403,404,409) 'Competing/stale claim rejected' | Out-Null
New-Item -ItemType Directory -Force "$PSScriptRoot/../docs" | Out-Null
$captures | ConvertTo-Json -Depth 100 | Set-Content "$PSScriptRoot/../docs/api-captures.json"
Write-Host "PASS: $($captures.Count) API responses captured; tokens redacted"

