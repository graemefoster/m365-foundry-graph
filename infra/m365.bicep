targetScope = 'resourceGroup'
extension microsoftGraphV1

@description('Globally unique Azure Bot resource name.')
param botName string
param displayName string
param tenantId string = tenant().tenantId
@description('Client ID of the deployed Foundry hosted agent instance (not the SSO app).')
param agentClientId string
param projectEndpoint string
param agentName string
param oauthConnectionName string = 'graph-connector'
@description('Stable federation identifier shared by the Entra FIC and Bot OAuth connection.')
param federationIdentifier string
@description('Bot Service federated subject: /eid1/c/pub/t/<base64url(tenantId.bytes_le)>/a/9ExAW52n_ky4ZiS_jhpJIQ/<federationIdentifier>.')
param federatedSubject string
@description('Microsoft Graph delegated ExternalItem.Read.All permission ID.')
param graphExternalItemReadAllScopeId string = 'c529cfca-c91b-489c-af2b-d92990b66ce6'

var loginEndpoint = environment().authentication.loginEndpoint
var loginUri = endsWith(loginEndpoint, '/') ? take(loginEndpoint, length(loginEndpoint) - 1) : loginEndpoint
var issuer = '${loginEndpoint}${tenantId}/v2.0'
var scopes = 'https://graph.microsoft.com/ExternalItem.Read.All offline_access'
var ssoResource = 'api://botid-${agentClientId}'
var ssoUniqueName = '${botName}-sso'
var accessAsUserScopeId = guid(subscription().id, resourceGroup().id, ssoUniqueName, 'access_as_user')

// First-party Teams and Microsoft 365 clients pre-authorized for Teams SSO token exchange.
var teamsClients = [
  '1fec8e78-bce4-4aaf-ab1b-5451cc387264' // Teams mobile/desktop
  '5e3ce6c0-2b1f-4285-8d4b-75ee78787346' // Teams web
  '4765445b-32c6-49b0-83e6-1d93765276ca' // Microsoft 365 web
  '0ec893e0-5785-4de6-99da-4ed124e5296c' // Microsoft 365 desktop
  'd3590ed6-52b3-4102-aeff-aad2292ab01c' // Microsoft Office
]

resource ssoApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: ssoUniqueName
  displayName: '${displayName} SSO'
  signInAudience: 'AzureADMyOrg'
  identifierUris: [ssoResource]
  isFallbackPublicClient: true
  web: {
    redirectUris: ['https://token.botframework.com/.auth/web/redirect']
  }
  publicClient: {
    redirectUris: ['http://localhost']
  }
  api: {
    requestedAccessTokenVersion: 2
    oauth2PermissionScopes: [
      {
        id: accessAsUserScopeId
        value: 'access_as_user'
        type: 'Admin'
        isEnabled: true
        adminConsentDisplayName: 'Access the agent as the signed-in user'
        adminConsentDescription: 'Allow trusted Microsoft 365 clients to sign in to this agent.'
      }
    ]
    preAuthorizedApplications: [
      for clientId in teamsClients: {
        appId: clientId
        delegatedPermissionIds: [accessAsUserScopeId]
      }
    ]
  }
  requiredResourceAccess: [
    {
      resourceAppId: '00000003-0000-0000-c000-000000000000'
      resourceAccess: [
        {
          id: graphExternalItemReadAllScopeId
          type: 'Scope'
        }
      ]
    }
  ]

  resource fic 'federatedIdentityCredentials@v1.0' = {
    name: federationIdentifier
    issuer: issuer
    subject: federatedSubject
    audiences: ['api://AzureADTokenExchange']
    description: 'Secretless Bot Service OAuth federation for ${botName}.'
  }
}

resource ssoSp 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: ssoApp.appId
}

resource bot 'Microsoft.BotService/botServices@2022-09-15' = {
  name: botName
  location: 'global'
  kind: 'azurebot'
  sku: {
    name: 'F0'
  }
  properties: {
    displayName: displayName
    endpoint: '${projectEndpoint}/agents/${agentName}/endpoint/protocols/activityProtocol?api-version=2025-05-15-preview'
    msaAppId: agentClientId
    msaAppTenantId: tenantId
    msaAppType: 'SingleTenant'
    publicNetworkAccess: 'Enabled'
  }
}

resource teams 'Microsoft.BotService/botServices/channels@2022-09-15' = {
  parent: bot
  name: 'MsTeamsChannel'
  location: 'global'
  properties: {
    channelName: 'MsTeamsChannel'
    properties: {
      isEnabled: true
    }
  }
}

resource oauth 'Microsoft.BotService/botServices/connections@2022-09-15' = {
  parent: bot
  name: oauthConnectionName
  location: 'global'
  properties: {
    clientId: ssoApp.appId
    serviceProviderId: 'c00b44ab-5e16-c44c-af26-2fd5bc55eb18'
    scopes: scopes
    parameters: [
      { key: 'clientId', value: ssoApp.appId }
      { key: 'tenantId', value: tenantId }
      { key: 'uniqueIdentifier', value: federationIdentifier }
      { key: 'tokenExchangeUrl', value: ssoResource }
      { key: 'loginUri', value: loginUri }
      { key: 'isMultiTenantFlowSupported', value: 'false' }
      { key: 'scopes', value: scopes }
      { key: 'federatedCredentialIssuer', value: issuer }
      { key: 'federatedCredentialValue', value: federatedSubject }
    ]
  }
  dependsOn: [ssoSp]
}

output ssoAppId string = ssoApp.appId
output adminConsentUrl string = '${loginUri}/${tenantId}/adminconsent?client_id=${ssoApp.appId}'
output botResourceId string = bot.id
output botClientId string = agentClientId
output oauthResourceId string = oauth.id
output teamsChannelId string = teams.id
