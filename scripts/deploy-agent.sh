#!/usr/bin/env bash
# Publishes src/PeopleAgent and deploys it to a Microsoft Foundry hosted agent
# via the Foundry data-plane REST API, enabling the M365 Activity public route.
#
# Usage:
#   bash scripts/deploy-agent.sh --config .local/customer.json
set -euo pipefail

CONFIG=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --config) CONFIG="$2"; shift 2 ;;
    *) echo "Usage: $0 --config <path-to-config.json>" >&2; exit 1 ;;
  esac
done
[[ -n "$CONFIG" && -f "$CONFIG" ]] || { echo "Config file not found: $CONFIG" >&2; exit 1; }

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

jq_get() { jq -er "$1" "$CONFIG"; }

PROJECT_ENDPOINT="$(jq_get '.projectEndpoint')"
AGENT_NAME="$(jq_get '.agentName')"
MODEL_DEPLOYMENT="$(jq_get '.modelDeployment')"
TENANT_ID="$(jq_get '.tenantId')"
AUTH_APP_ID="$(jq -r '.authAppId // empty' "$CONFIG")"
OAUTH_NAME="$(jq_get '.oauthConnectionName')"
GRAPH_CONN="$(jq_get '.graphConnectionId')"
SYNTHETIC="$(jq_get '.contentIsSynthetic | tostring')"

echo "Publishing src/PeopleAgent (linux-x64)..."
dotnet publish "$ROOT/src/PeopleAgent" -c Release -r linux-x64 --self-contained false \
  -o "$WORK/publish" --nologo -v q
rm -f "$WORK/publish/appsettings.Local.json"
(cd "$WORK/publish" && zip -qr "$WORK/code.zip" .)
SHA256="$(shasum -a 256 "$WORK/code.zip" | awk '{print $1}')"

TOKEN="$(az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv)"
AGENT_URL="${PROJECT_ENDPOINT}/agents/${AGENT_NAME}"

# Build hosted-agent version metadata JSON.
jq -n \
  --arg model "$MODEL_DEPLOYMENT" \
  --arg tenant "$TENANT_ID" \
  --arg client "$AUTH_APP_ID" \
  --arg oauth "$OAUTH_NAME" \
  --arg conn "$GRAPH_CONN" \
  --arg synth "$SYNTHETIC" \
  --arg appi "${APPLICATIONINSIGHTS_CONNECTION_STRING:-}" \
  --slurpfile cfg "$CONFIG" '
  ($cfg[0].graphFields // [] | to_entries | map({("Graph__Fields__" + (.key | tostring)): .value}) | add // {}) as $fields |
  {
    description: "People Agent hosted on Microsoft Foundry",
    definition: {
      kind: "hosted",
      cpu: "0.5",
      memory: "1Gi",
      code_configuration: {
        runtime: "dotnet_10",
        dependency_resolution: "bundled",
        entry_point: ["dotnet", "PeopleAgent.dll"]
      },
      protocol_versions: [
        { protocol: "activity", version: "2.0.0" },
        { protocol: "responses", version: "2.0.0" }
      ],
      environment_variables: ({
        Demo__StorageDirectory: "/tmp/people-agent",
        AZURE_AI_MODEL_DEPLOYMENT_NAME: $model,
        Graph__Enabled: "true",
        Graph__OAuthConnectionName: $oauth,
        Graph__TenantId: $tenant,
        Graph__ClientId: $client,
        Graph__ConnectionId: $conn,
        Graph__ContentIsSynthetic: $synth
      } + $fields + (if $appi != "" then { APPLICATIONINSIGHTS_CONNECTION_STRING: $appi } else {} end))
    }
  }' > "$WORK/metadata.json"

echo "Uploading version to ${AGENT_URL} (SHA256: ${SHA256})..."
VERSION_JSON="$(curl -fsS -X POST "${AGENT_URL}/versions?api-version=v1" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "x-ms-code-zip-sha256: ${SHA256}" \
  -F "metadata=@${WORK}/metadata.json;type=application/json;filename=metadata.json" \
  -F "code=@${WORK}/code.zip;type=application/zip;filename=code.zip")"
VERSION="$(echo "$VERSION_JSON" | jq -r '.version // .id')"
echo "Uploaded version ${VERSION}; waiting for active status..."

for _ in $(seq 1 60); do
  AGENT_JSON="$(curl -fsS -H "Authorization: Bearer ${TOKEN}" "${AGENT_URL}?api-version=v1")"
  STATUS="$(echo "$AGENT_JSON" | jq -r '.versions.latest.status')"
  HASH="$(echo "$AGENT_JSON" | jq -r '.versions.latest.definition.code_configuration.content_hash // empty')"
  if [[ "$HASH" == "$SHA256" && "$STATUS" == "active" ]]; then
    break
  fi
  if [[ "$HASH" == "$SHA256" && ( "$STATUS" == "failed" || "$STATUS" == "error" ) ]]; then
    echo "Deployment failed with status: $STATUS" >&2
    exit 1
  fi
  sleep 5
done

# Enable M365 Activity public route, BotServiceRbac, and route 100% traffic to the new version.
PATCH_BODY="$(jq -n --arg ver "$VERSION" '{
  agent_endpoint: {
    protocol_configuration: {
      activity: { enable_m365_public_endpoint: true },
      responses: {}
    },
    authorization_schemes: [
      { type: "AzureRbac" },
      { type: "BotServiceRbac" }
    ],
    version_selector: {
      version_selection_rules: [
        { type: "FixedRatio", agent_version: $ver, traffic_percentage: 100 }
      ]
    }
  }
}')"

curl -fsS -X PATCH "${AGENT_URL}?api-version=v1" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Content-Type: application/merge-patch+json" \
  -d "$PATCH_BODY" > /dev/null

AGENT_CLIENT_ID="$(echo "$AGENT_JSON" | jq -r '.instance_identity.client_id')"
echo "Active version: ${VERSION}"
echo "Agent client ID (agentClientId): ${AGENT_CLIENT_ID}"
