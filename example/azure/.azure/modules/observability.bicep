param location string
param workspaceName string

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: workspaceName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: -1 }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

output workspaceId string = workspace.properties.customerId
#disable-next-line outputs-should-not-contain-secrets
output workspaceKey string = listKeys(workspace.id, workspace.apiVersion).primarySharedKey
