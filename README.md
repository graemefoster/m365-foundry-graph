# People Agent

A minimal .NET 10 Microsoft Foundry hosted agent built with the Microsoft Agent Framework and Microsoft 365 Agents SDK.

It exposes two standard Foundry hosted-agent protocols from a single host (`src/PeopleAgent/Program.cs`):

- **Activity (`/activity/messages`)** — Teams and Microsoft 365 Copilot, with delegated Microsoft Graph connector search via Teams SSO (or bundled sample HR documents when `Graph__Enabled=false`) and optional chart cards.
- **Responses (`/responses`)** — Foundry evaluations and OpenAI Responses clients, always grounded on the bundled synthetic HR documents (`Data/knowledge.json`).

> **Note:** The bundled HR documents are fictional Contoso examples. No ServiceNow, HCM, Dataverse, or real employee data is connected.

## Solution layout

```text
src/PeopleAgent                  Hosted agent entry point (Program.cs), config, and Data/
src/PeopleAgent.ActivityProtocol Teams / M365 Copilot Activity adapter, SSO, streaming, cards
src/PeopleAgent.Core             Agent Framework factory, Graph/local search, topics, telemetry
src/CustomGraphConnector         Standalone sample Graph connector CLI and tests
infra/                           Bicep (Entra SSO app + FIC + Bot + Teams + OAuth) and Teams manifest
scripts/                         Foundry code deploy and Teams packaging shell scripts
tests/                           Unit tests (.NET)
```

## Build and test

```bash
dotnet test PeopleAgent.slnx
dotnet format PeopleAgent.slnx --verify-no-changes
az bicep build --file infra/m365.bicep --stdout > /dev/null
```

The agent host is designed to run inside Microsoft Foundry (where Foundry provides the hosting environment, `/readiness` probe, and `FOUNDRY_PROJECT_ENDPOINT`).

## Configuration

| Variable | Purpose |
|---|---|
| `FOUNDRY_PROJECT_ENDPOINT` | Injected by Foundry; project endpoint used by `AIProjectClient` with `DefaultAzureCredential` |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | Model deployment name (e.g. `gpt-5.4-mini`) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Enables Application Insights export via the Foundry host's OpenTelemetry distro |
| `Graph__Enabled` | `true` for delegated Graph Search in Teams; `false` for bundled sample HR documents |
| `Graph__OAuthConnectionName` | Bot Service OAuth connection name for Teams SSO (e.g. `graph-connector`) |
| `Graph__TenantId`, `Graph__ClientId`, `Graph__ConnectionId` | Entra tenant, retrieval app client ID, and Graph external connection ID |
| `Graph__ContentIsSynthetic` | Marks indexed sample records as synthetic |
| `Demo__StorageDirectory` | Directory for local ephemeral `telemetry.jsonl` / `feedback.jsonl` (e.g. `/tmp/people-agent`) |

See `src/PeopleAgent/appsettings.json` for default topic/session settings.

## Deploy to Foundry and Teams

Copy `infra/customer.example.json` to `.local/customer.json`, fill in your project, resource group, and Graph connector values, and run:

```bash
# 1. Publish and deploy the hosted agent to Foundry (prints agentClientId)
bash scripts/deploy-agent.sh --config .local/customer.json

# 2. Provision Entra SSO app, Federated Credential, Bot Service (F0), Teams channel, and OAuth via Bicep
#    (requires federationIdentifier and Bot Service federatedSubject for your tenant)
az deployment group create \
  --resource-group <resource-group> \
  --template-file infra/m365.bicep \
  --parameters botName=<bot-name> displayName="People Agent" \
    agentClientId=<agentClientId> projectEndpoint=<project-endpoint> \
    agentName=<agent-name> federationIdentifier=<federation-id> \
    federatedSubject=<federated-subject>

# 3. Record authAppId (from Bicep ssoAppId output) and agentClientId in .local/customer.json,
#    then re-run deploy-agent.sh so the agent picks up Graph__ClientId.
bash scripts/deploy-agent.sh --config .local/customer.json

# 4. Build the Teams app ZIP for upload
bash scripts/package-teams.sh --config .local/customer.json --output .local/teams.zip
```

**Required administrator / RBAC steps (not auto-granted by scripts):**

- Grant tenant admin consent for delegated `ExternalItem.Read.All` on the SSO app (using the `adminConsentUrl` output from Bicep).
- Grant the hosted agent's managed identity model access (e.g. `Cognitive Services OpenAI User`) on the Foundry account.
- Grant end users permission to interact with the Foundry agent.

**Updating code:** re-run `bash scripts/deploy-agent.sh --config .local/customer.json`. Existing Foundry sessions can stay pinned to an older agent version; delete old sessions via `DELETE {projectEndpoint}/agents/{name}/endpoint/sessions/{sessionId}?api-version=2025-11-15-preview` to force fresh chats onto the new version.

## Notes

- **Conversation state:** Uses the Agents SDK's default in-memory `MemoryStorage` (`AddActivity<PeopleAgentApplication>()`). State is lost on host restart or replica recycle.
- **Delegated Graph Search:** Uses the current turn's Teams SSO token (`UserAuthorization.GetTurnTokenAsync`) to call `/v1.0/search/query` scoped to the configured connection. Tokens are never persisted or shared across turns. Use `sign out` in chat to revoke the token or `start over` to reset history.
- **SDK version pins:** Do not manually upgrade `Microsoft.Agents.*` (resolved to `1.6.150` by `Azure.AI.AgentServer.Activity`) or `Microsoft.OpenTelemetry` (resolved to `1.0.0-beta.1` by `Azure.AI.AgentServer.Core`). Newer versions have breaking binary incompatibilities with the current Foundry hosted-agent packages.
- **Telemetry:** Traces, logs, and metrics are exported by the Foundry host when `APPLICATIONINSIGHTS_CONNECTION_STRING` is set. The app adds a `knowledge_search` span, `agent.events` / `agent.turn.duration` metrics, and structured events (`model.completed`, `graph.search.completed`, `turn.completed`, `feedback.submitted`) without logging prompts, answers, or tokens.

