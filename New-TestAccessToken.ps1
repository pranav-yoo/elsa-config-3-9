# Development only. Creates the signing key once in user secrets; never stores employee accounts.
# Username: ./New-TestAccessToken.ps1 -Provider Username -Identity john
# Email:    ./New-TestAccessToken.ps1 -Provider Email -Identity john@example.test
param(
    [ValidateSet('Username', 'Email')][string]$Provider = 'Username',
    [string]$Identity,
    [string]$Subject = 'demo-subject',
    [string]$ClaimName
)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'IlWorkflow.Api.csproj'
$providerSettings = Get-Content (Join-Path $PSScriptRoot 'Samples/external-providers.json') -Raw | ConvertFrom-Json
$profile = @($providerSettings.ExternalAuth.Providers | Where-Object { $_.master -eq $Provider })
if ($profile.Count -ne 1) { throw 'Expected one configured demo profile for this master.' }
$profile = $profile[0]
if ([string]::IsNullOrWhiteSpace($Identity)) { $Identity = if ($Provider -eq 'Email') { 'john@example.test' } else { 'john' } }
if ([string]::IsNullOrWhiteSpace($ClaimName)) { $ClaimName = $profile.matchClaim }
$secrets = (& dotnet user-secrets list --json --project $projectPath | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'Unable to read development user secrets.' }
$signingKey = $secrets.'ExternalAuth:DevelopmentTokens:SigningKey'
if ([string]::IsNullOrWhiteSpace($signingKey)) {
    $signingKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    & dotnet user-secrets set 'ExternalAuth:DevelopmentTokens:SigningKey' $signingKey --project $projectPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to save the development signing key.' }
    Write-Warning 'Created the development signing key. Start/restart the application before using the token.'
}
function ConvertTo-Base64Url([byte[]]$Bytes) {
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"HS256","typ":"JWT"}'))
# sub is deliberately independent of the configured login claim. The server resolves that claim via the master.
$payload = @{ sub=$Subject; iss=$profile.issuer; aud=$profile.audience; iat=$now; nbf=$now; exp=($now+1800) }
$payload[$ClaimName] = $Identity
$body = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress)))
$unsigned = "$header.$body"
$hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($signingKey))
try { $signature = ConvertTo-Base64Url ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($unsigned))) }
finally { $hmac.Dispose() }
Write-Output "$unsigned.$signature"
