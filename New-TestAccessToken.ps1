# Development only. First run stores a random signing key in .NET user secrets.
# Restart the app after creating/changing the key. Tokens last 30 minutes.
param([string]$Subject = '1234567890')
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'IlWorkflow.Api.csproj'
$secrets = (& dotnet user-secrets list --json --project $projectPath | ConvertFrom-Json)
$signingKey = $secrets.'ExternalAuth:DevelopmentTokens:SigningKey'
if ([string]::IsNullOrWhiteSpace($signingKey)) {
    $signingKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    & dotnet user-secrets set 'ExternalAuth:DevelopmentTokens:SigningKey' $signingKey --project $projectPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to save the development signing key.' }
    Write-Warning 'Created the development signing key. Start or restart the application before using this token.'
}
function ConvertTo-Base64Url([byte[]]$Bytes) {
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$header = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes('{"alg":"HS256","typ":"JWT"}'))
$payload = @{ sub=$Subject; name='John Doe'; admin=$true; iss='ilworkflow-development'; aud='ilworkflow-api'; iat=$now; nbf=$now; exp=($now+1800) } | ConvertTo-Json -Compress
$body = ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($payload))
$unsigned = "$header.$body"
$hmac = [Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($signingKey))
try { $signature = ConvertTo-Base64Url ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($unsigned))) }
finally { $hmac.Dispose() }
Write-Output "$unsigned.$signature"
