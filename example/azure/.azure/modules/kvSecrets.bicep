param kvName string

@secure()
param dbConnectionString string
@secure()
param jwtSecret string
param bootstrapAdminUser string
@secure()
param bootstrapAdminPassword string
@secure()
param dbUser string
@secure()
param dbPassword string

resource kv 'Microsoft.KeyVault/vaults@2025-05-01' existing = {
  name: kvName
}

resource secretDbConnectionString 'Microsoft.KeyVault/vaults/secrets@2025-05-01' = {
  parent: kv
  name: 'DB-CONNECTIONSTRING'
  properties: { value: dbConnectionString }
}

resource secretJwtSecret 'Microsoft.KeyVault/vaults/secrets@2025-05-01' = {
  parent: kv
  name: 'MEISTER-JWT-SECRET'
  properties: { value: jwtSecret }
}

resource secretBootstrapAdminUser 'Microsoft.KeyVault/vaults/secrets@2025-05-01' = {
  parent: kv
  name: 'MEISTER-BOOTSTRAP-ADMIN-USER'
  properties: { value: bootstrapAdminUser }
}

resource secretBootstrapAdminPassword 'Microsoft.KeyVault/vaults/secrets@2025-05-01' = {
  parent: kv
  name: 'MEISTER-BOOTSTRAP-ADMIN-PASSWORD'
  properties: { value: bootstrapAdminPassword }
}

resource secretDbUser 'Microsoft.KeyVault/vaults/secrets@2025-05-01' = {
  parent: kv
  name: 'DB-USER'
  properties: { value: dbUser }
}

resource secretDbPassword 'Microsoft.KeyVault/vaults/secrets@2025-05-01' = {
  parent: kv
  name: 'DB-PASSWORD'
  properties: { value: dbPassword }
}
