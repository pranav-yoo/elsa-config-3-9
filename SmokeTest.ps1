param([string]$BaseUrl = 'http://localhost:5000')
$ErrorActionPreference = 'Stop'
$document = Invoke-RestMethod "$BaseUrl/swagger/v3/swagger.json"
if (-not $document.paths.'/elsa/api/workflow-definitions') { throw 'Elsa workflow APIs are missing from Swagger.' }
if (-not $document.components.securitySchemes) { throw 'Swagger authentication is missing.' }
$rejected = $false
try { Invoke-WebRequest "$BaseUrl/elsa/api/workflow-definitions" | Out-Null }
catch { if ([int]$_.Exception.Response.StatusCode -in 401,403) { $rejected = $true } else { throw } }
if (-not $rejected) { throw 'Unauthenticated workflow listing was allowed.' }
$invalid = Invoke-WebRequest "$BaseUrl/elsa/api/workflow-definitions" -Headers @{ Authorization = 'Bearer invalid-token' } -SkipHttpErrorCheck
if ($invalid.StatusCode -ne 401) { throw 'Invalid bearer token was not rejected with 401.' }
if (-not $invalid.Headers['X-Trace-Id']) { throw 'Diagnostic trace ID is missing.' }
$password = Read-Host 'Elsa admin password' -AsSecureString
$body = @{ username = 'admin'; password = [System.Net.NetworkCredential]::new('', $password).Password } | ConvertTo-Json
$login = Invoke-RestMethod "$BaseUrl/elsa/api/identity/login" -Method Post -ContentType 'application/json' -Body $body
if (-not $login.accessToken) { throw 'Login failed.' }
$headers = @{ Authorization = "Bearer $($login.accessToken)" }
Invoke-RestMethod "$BaseUrl/elsa/api/workflow-definitions" -Headers $headers | Out-Null
$definition = Get-Content "$PSScriptRoot/Samples/Definitions/expense-claim.definition.json" -Raw
Invoke-RestMethod "$BaseUrl/elsa/api/workflow-definitions" -Headers $headers -Method Post -ContentType 'application/json' -Body $definition | Out-Null
$submission = @{ correlationId = "SMOKE-$([guid]::NewGuid())"; input = @{ requesterId = 'requester'; firstName = 'Pranav'; lastName = 'Shah'; claimAmount = 6000 } } | ConvertTo-Json
$result = Invoke-RestMethod "$BaseUrl/elsa/api/workflow-definitions/expense-claim-v1/execute" -Headers $headers -Method Post -ContentType 'application/json' -Body $submission
if (-not $result.workflowState.id) { throw 'Workflow execution did not return an instance ID.' }
Write-Host "PASS: Swagger, API authentication, workflow listing and execution. Instance: $($result.workflowState.id)"

