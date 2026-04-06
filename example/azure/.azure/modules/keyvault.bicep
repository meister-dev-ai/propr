param location string
param kvName string
param subnetId string

resource kv 'Microsoft.KeyVault/vaults@2025-05-01' = {
  name: kvName
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      virtualNetworkRules: [
        { id: subnetId, ignoreMissingVnetServiceEndpoint: false }
      ]
      ipRules: []
    }
  }
}

output kvUri string = kv.properties.vaultUri
output kvName string = kv.name
