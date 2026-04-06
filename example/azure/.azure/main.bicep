@description('Base name for all resources. Drives every resource name in the deployment.')
param projectName string = 'meister-propr'

@description('Azure region for all resources.')
param location string = 'switzerlandnorth'

@description('Container image tag for the backend and admin-ui images.')
param imageTag string = 'latest'

@description('Set to false if you want to provision infrastructure without creating the app resources.')
param deployApps bool = true

// ── Key Vault secret values ───────────────────────────────────────────────────
@description('Optional PostgreSQL connection string override stored in Key Vault. Leave empty to use the internal database app.')
@secure()
param dbConnectionString string = ''

@description('JWT signing secret for backend auth. Must be at least 32 characters.')
@secure()
param jwtSecret string

@description('Bootstrap admin username seeded on first startup when no admin exists.')
param bootstrapAdminUser string = 'admin'

@description('Bootstrap admin password seeded on first startup when no admin exists.')
@secure()
param bootstrapAdminPassword string

@description('PostgreSQL username stored in Key Vault.')
@secure()
param dbUser string

@description('PostgreSQL password stored in Key Vault.')
@secure()
param dbPassword string

// ── Derived names ─────────────────────────────────────────────────────────────
// Storage account names must be alphanumeric only
var safeName    = replace(projectName, '-', '')
var storageName = '${safeName}sg'
var kvName      = 'kv${safeName}${take(uniqueString(resourceGroup().id), 4)}'

// ── Modules ───────────────────────────────────────────────────────────────────

module observability 'modules/observability.bicep' = {
  name: 'observability'
  params: {
    location: location
    workspaceName: 'workspace-${projectName}'
  }
}

module network 'modules/network.bicep' = {
  name: 'network'
  params: {
    location: location
    vnetName: '${projectName}-vnet'
  }
}

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    kvName: kvName
    subnetId: network.outputs.subnetId
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    storageName: storageName
  }
}

module containerEnvironment 'modules/containerEnvironment.bicep' = {
  name: 'containerEnvironment'
  params: {
    location: location
    envName: 'managedEnvironment-${projectName}'
    workspaceId: observability.outputs.workspaceId
    workspaceKey: observability.outputs.workspaceKey
    storageAccountName: storageName
    infrastructureSubnetId: network.outputs.subnetId
  }
  dependsOn: [storage]
}

var internalDbConnectionString = 'Host=${projectName}-db;Port=5432;Database=meisterpropr;Username=${dbUser};Password=${dbPassword};Ssl Mode=Disable'
var effectiveDbConnectionString = empty(dbConnectionString) ? internalDbConnectionString : dbConnectionString

module kvSecrets 'modules/kvSecrets.bicep' = {
  name: 'kvSecrets'
  params: {
    kvName: kvName
    dbConnectionString: effectiveDbConnectionString
    jwtSecret: jwtSecret
    bootstrapAdminUser: bootstrapAdminUser
    bootstrapAdminPassword: bootstrapAdminPassword
    dbUser: dbUser
    dbPassword: dbPassword
  }
  dependsOn: [keyvault]
}

module containerApps 'modules/containerApps.bicep' = if (deployApps) {
  name: 'containerApps'
  params: {
    location: location
    projectName: projectName
    envName: 'managedEnvironment-${projectName}'
    imageTag: imageTag
    kvName: kvName
    kvUri: keyvault.outputs.kvUri
    storageAccountName: storageName
  }
  dependsOn: [kvSecrets]
}

// ── Outputs ───────────────────────────────────────────────────────────────────
var reverseProxyFqdn = containerApps.?outputs.reverseProxyFqdn

output reverseProxyUrl string = reverseProxyFqdn != null ? 'https://${reverseProxyFqdn}' : 'not deployed'
output kvUri string = keyvault.outputs.kvUri
