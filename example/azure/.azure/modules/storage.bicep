param location string
param storageName string

resource sa 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

resource fileServices 'Microsoft.Storage/storageAccounts/fileServices@2025-06-01' = {
  parent: sa
  name: 'default'
}

resource fileShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2025-06-01' = {
  parent: fileServices
  name: 'postgres-data'
  properties: { shareQuota: 512 }
}
