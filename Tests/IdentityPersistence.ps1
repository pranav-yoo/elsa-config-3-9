# Run once, restart the server, then run with -Verify. Creates one research user/role.
param([string]$BaseUrl = 'http://localhost:5000', [switch]$Verify)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$statePath = Join-Path $projectRoot 'App_Data/identity-persistence-check.json'
$secrets = (& dotnet user-secrets list --json --project (Join-Path $projectRoot 'IlWorkflow.Api.csproj') | ConvertFrom-Json)
$adminPassword = $secrets.'Identity:Admin:Password'
if ([string]::IsNullOrWhiteSpace($adminPassword)) { $adminPassword = 'password' }
$body = @{ username='admin'; password=$adminPassword } | ConvertTo-Json
$login = Invoke-RestMethod "$BaseUrl/elsa/api/identity/login" -Method Post -ContentType 'application/json' -Body $body
if (-not $login.accessToken) { throw 'Admin sign-in failed.' }
$headers = @{ Authorization="Bearer $($login.accessToken)" }
if (-not $Verify) {
    $suffix = [Guid]::NewGuid().ToString('N').Substring(0,12)
    $roleId = "PersistenceCheck-$suffix"
    $roleBody = @{ id=$roleId; name=$roleId; permissions=@('user-tasks:view') } | ConvertTo-Json
    $role = Invoke-RestMethod "$BaseUrl/elsa/api/identity/roles" -Method Post -Headers $headers -ContentType 'application/json' -Body $roleBody
    $userName = "persistence-check-$suffix"
    $userBody = @{ name=$userName; password='ResearchOnly123!'; roles=@($roleId) } | ConvertTo-Json
    $user = Invoke-RestMethod "$BaseUrl/elsa/api/identity/users" -Method Post -Headers $headers -ContentType 'application/json' -Body $userBody
    New-Item -ItemType Directory -Path (Split-Path $statePath -Parent) -Force | Out-Null
    @{ userId=$user.id; username=$userName; roleId=$role.id } | ConvertTo-Json | Set-Content -LiteralPath $statePath
    Write-Output 'PASS: native user and role created in PostgreSQL. Restart server, then rerun with -Verify.'
} else {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $users = Invoke-RestMethod "$BaseUrl/elsa/api/identity/users?page=0&pageSize=1000" -Headers $headers
    $roles = Invoke-RestMethod "$BaseUrl/elsa/api/identity/roles?page=0&pageSize=1000" -Headers $headers
    $user = $users.users | Where-Object { $_.id -eq $state.userId }
    $role = $roles.roles | Where-Object { $_.id -eq $state.roleId }
    if ($user.id -ne $state.userId -or $role.id -ne $state.roleId -or $user.roles -notcontains $state.roleId) { throw 'Persisted identity mismatch.' }
    $body = @{ username=$state.username; password='ResearchOnly123!' } | ConvertTo-Json
    $login = Invoke-RestMethod "$BaseUrl/elsa/api/identity/login" -Method Post -ContentType 'application/json' -Body $body
    if (-not $login.accessToken) { throw 'Persisted user cannot sign in.' }
    Write-Output 'PASS: user ID, role ID, role membership and password login survived restart.'
}
