param([Parameter(Mandatory)][string]$PublicBaseUrl)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Set-Location (Split-Path $PSScriptRoot -Parent)
$url = [Uri]$PublicBaseUrl
if (!$url.IsAbsoluteUri -or $url.Scheme -ne "https" -or $url.Query -or $url.Fragment) {
    throw "The source base URL must be an absolute HTTPS URL without a query or fragment."
}
& python3 data/semantic-demo/generate.py
if ($LASTEXITCODE -ne 0) { throw "Synthetic data generation failed." }
$settings = Get-Content connector.settings.json -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($settings.tenantId)) {
    throw "connector.settings.json must define tenantId."
}
$settings.connectionId = "caldovasemanticdemo"
$settings.connectionName = "Caldova Synthetic Appliance Service Notes"
$settings.csvPath = "data/semantic-demo/generated/SemanticApplianceParts.csv"
$settings.sourceUrl = $PublicBaseUrl.TrimEnd("/") + "/parts/{id}.txt"
$settings | Add-Member -NotePropertyName "retrievalCasesPath" -NotePropertyValue "data/semantic-demo/generated/retrieval-cases.json" -Force
$settings | Add-Member -NotePropertyName "connectionDescription" -NotePropertyValue "SYNTHETIC DEMO DATA: eight fictional appliance-service reference articles for testing semantic retrieval. Not real manufacturer policies, repair instructions, warranties, prices or stock." -Force
$settings | Add-Member -NotePropertyName "contentCategory" -NotePropertyValue "knowledgeBase" -Force
$settings | ConvertTo-Json | Set-Content connector.semantic.settings.json
Write-Host "Prepared caldovasemanticdemo. The original connector.settings.json and data remain unchanged."
