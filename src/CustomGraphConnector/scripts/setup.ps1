param(
    [Parameter(Mandatory)]
    [string]$TenantId
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Invoke-AzJson {
    param([string[]]$Arguments)
    $output = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: $($Arguments[0..1] -join ' ')" }
    if ($output) { return ($output -join "`n" | ConvertFrom-Json) }
}

$account = Invoke-AzJson @("account", "show")
if ($account.tenantId -ne $TenantId) {
    throw "The selected Azure account is not in Caldova. Select the Caldova account before running setup."
}
$organization = Invoke-AzJson @("rest", "--method", "GET", "--url", "https://graph.microsoft.com/v1.0/organization?`$select=id,displayName")
if ($organization.value.Count -ne 1 -or $organization.value[0].id -ne $TenantId -or $organization.value[0].displayName -ne "Caldova") {
    throw "The signed-in Graph organization is not Caldova."
}

New-Item -ItemType Directory -Force ".local", ".secrets" | Out-Null
& chmod 700 .secrets
if ($LASTEXITCODE -ne 0) { throw "Could not restrict the local credential directory." }
$graph = @(Invoke-AzJson @("ad", "sp", "list", "--filter", "appId eq '00000003-0000-0000-c000-000000000000'"))
if ($graph.Count -ne 1) { throw "Expected one Microsoft Graph service principal." }
$roleNames = @("ExternalConnection.ReadWrite.OwnedBy", "ExternalItem.ReadWrite.OwnedBy")
$roles = @($graph[0].appRoles | Where-Object { $_.value -in $roleNames -and $_.isEnabled -and "Application" -in $_.allowedMemberTypes })
if ($roles.Count -ne 2) { throw "The required Microsoft Graph app roles were not found." }
$permissions = @(@{
    resourceAppId = "00000003-0000-0000-c000-000000000000"
    resourceAccess = @($roles | ForEach-Object { @{ id = $_.id; type = "Role" } })
})
ConvertTo-Json -InputObject $permissions -Depth 8 | Set-Content ".local/permissions.json"

$appName = "Caldova Appliance Parts Local Connector"
if (Test-Path ".local/application.json") {
    $saved = Get-Content ".local/application.json" -Raw | ConvertFrom-Json
    $app = Invoke-AzJson @("ad", "app", "show", "--id", $saved.appId)
}
else {
    $existing = @(Invoke-AzJson @("ad", "app", "list", "--filter", "displayName eq '$appName'"))
    if ($existing.Count -ne 0) { throw "An app with this name already exists, but local setup state is missing. Resolve the identity before creating another app." }
    $app = Invoke-AzJson @("ad", "app", "create", "--display-name", $appName, "--sign-in-audience", "AzureADMyOrg", "--required-resource-access", "@.local/permissions.json")
    $app | ConvertTo-Json -Depth 15 | Set-Content ".local/application.json"
}
if ($app.displayName -ne $appName) { throw "Saved app does not match the expected connector identity." }

if (!(Test-Path ".secrets/connector.pem") -and !(Test-Path ".secrets/connector.key")) {
    & openssl req -x509 -newkey rsa:2048 -sha256 -days 365 -nodes -keyout ".secrets/connector.key" -out ".secrets/connector.pem" -subj "/CN=Caldova Appliance Parts Local Connector" 2> ".local/certificate-generation.log"
    if ($LASTEXITCODE -ne 0) { throw "Certificate generation failed. See .local/certificate-generation.log." }
}
if (!(Test-Path ".secrets/connector.pem") -or !(Test-Path ".secrets/connector.key")) {
    throw "The local certificate or private key is missing. Restore the matching pair before continuing."
}
& chmod 600 .secrets/connector.key .secrets/connector.pem
if ($LASTEXITCODE -ne 0) { throw "Could not restrict the local certificate permissions." }

$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPemFile(
    (Join-Path $root ".secrets/connector.pem"), (Join-Path $root ".secrets/connector.key"))
try {
    if ($certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) { throw "The local certificate has expired." }
    $customKeyIdentifier = $certificate.Thumbprint
    if (!@($app.keyCredentials | Where-Object { $_.customKeyIdentifier -eq $customKeyIdentifier }).Count) {
        $null = Invoke-AzJson @("ad", "app", "credential", "reset", "--id", $app.appId, "--append", "--cert", "@.secrets/connector.pem", "--display-name", "local-connector-certificate")
    }
}
finally { $certificate.Dispose() }

$principals = @(Invoke-AzJson @("ad", "sp", "list", "--filter", "appId eq '$($app.appId)'"))
if ($principals.Count -eq 0) {
    $principal = Invoke-AzJson @("ad", "sp", "create", "--id", $app.appId)
}
elseif ($principals.Count -eq 1) { $principal = $principals[0] }
else { throw "Multiple connector service principals found." }

$assignmentUrl = "https://graph.microsoft.com/v1.0/servicePrincipals/$($principal.id)/appRoleAssignments"
$assignments = Invoke-AzJson @("rest", "--method", "GET", "--url", $assignmentUrl)
foreach ($role in $roles) {
    if (!@($assignments.value | Where-Object { $_.appRoleId -eq $role.id -and $_.resourceId -eq $graph[0].id }).Count) {
        @{
            principalId = $principal.id
            resourceId = $graph[0].id
            appRoleId = $role.id
        } | ConvertTo-Json | Set-Content ".local/role-assignment.json"
        $null = Invoke-AzJson @("rest", "--method", "POST", "--url", $assignmentUrl, "--body", "@.local/role-assignment.json")
    }
    Write-Host "Admin consent verified: $($role.value)"
}
$settings = Get-Content "connector.settings.example.json" -Raw | ConvertFrom-Json
$settings.tenantId = $TenantId
$settings.clientId = $app.appId
if (Test-Path "connector.settings.json") {
    $existingSettings = Get-Content "connector.settings.json" -Raw | ConvertFrom-Json
    if ($existingSettings.tenantId -ne $TenantId -or $existingSettings.clientId -ne $app.appId) {
        throw "Existing settings reference a different identity; refusing to overwrite them."
    }
}
else { $settings | ConvertTo-Json | Set-Content "connector.settings.json" }
Write-Host "Ready: tenant Caldova, app $($app.appId). Private certificate remains in .secrets."
