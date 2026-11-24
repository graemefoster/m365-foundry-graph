$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Set-Location (Split-Path $PSScriptRoot -Parent)
$settings = Get-Content "connector.settings.json" -Raw | ConvertFrom-Json
$tenantId = $settings.tenantId

function Invoke-AzJson {
    param([string[]]$Arguments)
    $output = & az @Arguments --only-show-errors --output json
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: $($Arguments[0..1] -join ' ')" }
    if ($output) { return ($output -join "`n" | ConvertFrom-Json) }
}

$account = Invoke-AzJson @("account", "show")
if ($account.tenantId -ne $tenantId) {
    throw "Select the Azure account matching connector.settings.json before configuring retrieval."
}
$user = Invoke-AzJson @("rest", "--method", "GET", "--url", "https://graph.microsoft.com/v1.0/me?`$select=id")
$graph = @(Invoke-AzJson @("ad", "sp", "list", "--filter", "appId eq '00000003-0000-0000-c000-000000000000'"))
if ($graph.Count -ne 1) { throw "Expected one Microsoft Graph service principal." }
$scope = @($graph[0].oauth2PermissionScopes | Where-Object { $_.value -eq "ExternalItem.Read.All" -and $_.isEnabled })
if ($scope.Count -ne 1) { throw "Delegated ExternalItem.Read.All was not found." }
New-Item -ItemType Directory -Force ".local" | Out-Null
$permissions = @(@{
    resourceAppId = "00000003-0000-0000-c000-000000000000"
    resourceAccess = @(@{ id = $scope[0].id; type = "Scope" })
})
ConvertTo-Json -InputObject $permissions -Depth 8 | Set-Content ".local/retrieval-permissions.json"
$appName = "Caldova Appliance Parts Retrieval Test"
if (Test-Path ".local/retrieval-application.json") {
    $saved = Get-Content ".local/retrieval-application.json" -Raw | ConvertFrom-Json
    $app = Invoke-AzJson @("ad", "app", "show", "--id", $saved.appId)
}
else {
    $existing = @(Invoke-AzJson @("ad", "app", "list", "--filter", "displayName eq '$appName'"))
    if ($existing.Count -ne 0) { throw "A retrieval app already exists but local state is missing. Resolve the identity before creating another app." }
    $app = Invoke-AzJson @("ad", "app", "create", "--display-name", $appName, "--sign-in-audience", "AzureADMyOrg", "--is-fallback-public-client", "true", "--public-client-redirect-uris", "http://localhost", "--required-resource-access", "@.local/retrieval-permissions.json")
    $app | ConvertTo-Json -Depth 15 | Set-Content ".local/retrieval-application.json"
}
if ($app.displayName -ne $appName) { throw "Saved app is not the expected retrieval identity." }
$principals = @(Invoke-AzJson @("ad", "sp", "list", "--filter", "appId eq '$($app.appId)'"))
if ($principals.Count -eq 0) { $principal = Invoke-AzJson @("ad", "sp", "create", "--id", $app.appId) }
elseif ($principals.Count -eq 1) { $principal = $principals[0] }
else { throw "Multiple retrieval service principals were found." }

$grants = Invoke-AzJson @("rest", "--method", "GET", "--url", "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?`$filter=clientId eq '$($principal.id)'")
$matching = @($grants.value | Where-Object {
    $_.resourceId -eq $graph[0].id -and $_.consentType -eq "Principal" -and $_.principalId -eq $user.id
})
if ($matching.Count -gt 1) { throw "Multiple user consent grants found." }
if ($matching.Count -eq 0) {
    @{
        clientId = $principal.id
        consentType = "Principal"
        principalId = $user.id
        resourceId = $graph[0].id
        scope = "ExternalItem.Read.All"
    } | ConvertTo-Json | Set-Content ".local/retrieval-consent.json"
    $null = Invoke-AzJson @("rest", "--method", "POST", "--url", "https://graph.microsoft.com/v1.0/oauth2PermissionGrants", "--body", "@.local/retrieval-consent.json")
}
elseif ($matching[0].scope -ne "ExternalItem.Read.All") {
    throw "Existing user consent has unexpected scopes; refusing to change it."
}
if ($settings.PSObject.Properties.Name -contains "retrievalClientId" -and $settings.retrievalClientId -ne $app.appId) {
    throw "Settings reference a different retrieval identity; refusing to overwrite it."
}
$settings | Add-Member -NotePropertyName "retrievalClientId" -NotePropertyValue $app.appId -Force
$settings | ConvertTo-Json | Set-Content "connector.settings.json"
Write-Host "Ready: read-only retrieval app $($app.appId), consent restricted to the signed-in user."
