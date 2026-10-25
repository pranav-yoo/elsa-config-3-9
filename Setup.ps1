param([string]$DatabaseUser = 'postgres')
$ErrorActionPreference = 'Stop'
$dbPassword = Read-Host 'PostgreSQL password' -AsSecureString
$adminPassword = Read-Host 'Choose the Elsa admin password' -AsSecureString
$dbPlain = [System.Net.NetworkCredential]::new('', $dbPassword).Password
$adminPlain = [System.Net.NetworkCredential]::new('', $adminPassword).Password
if ([string]::IsNullOrWhiteSpace($adminPlain)) { throw 'An admin password is required.' }
# Quote connection-string values, including embedded semicolons and quotes.
$quotedUser = '"' + $DatabaseUser.Replace('"', '""') + '"'
$quotedPassword = '"' + $dbPlain.Replace('"', '""') + '"'
$secrets = @{
    'ConnectionStrings:Elsa' = "Host=localhost;Port=5432;Database=ilworkflow;Username=$quotedUser;Password=$quotedPassword"
    'Identity:Admin:Password' = $adminPlain
    'Identity:Tokens:SigningKey' = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
}
$secrets | ConvertTo-Json -Compress | dotnet user-secrets set --project "$PSScriptRoot/IlWorkflow.Api.csproj"
if ($LASTEXITCODE -ne 0) { throw 'Could not save user secrets.' }
Write-Host 'Configuration saved. Run: dotnet run --launch-profile http'

