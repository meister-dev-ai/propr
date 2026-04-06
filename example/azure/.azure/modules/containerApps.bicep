param location string
param projectName string
param envName string
param imageTag string
param kvName string
param kvUri string
param storageAccountName string

var nginxTemplate = loadTextContent('../nginx/nginx-aca.conf')
var reverseProxyStartCommand = replace('''
set -eu
cat >/tmp/default.conf.template <<'EOF'
@@TEMPLATE@@
EOF
envsubst '$ADMIN_UI_HOST $BACKEND_HOST' </tmp/default.conf.template >/etc/nginx/conf.d/default.conf
exec /docker-entrypoint.sh nginx -g 'daemon off;'
''', '@@TEMPLATE@@', nginxTemplate)

var normalizedKvUri = endsWith(kvUri, '/') ? kvUri : '${kvUri}/'
var adminUiHost = '${projectName}-admin-ui.internal.${env.properties.defaultDomain}'
var backendHost = '${projectName}-backend.internal.${env.properties.defaultDomain}'

// Built-in role definition IDs
var kvSecretsUserRoleId    = '4633458b-17de-408a-b874-0445c86b69e6'

resource env 'Microsoft.App/managedEnvironments@2025-10-02-preview' existing = {
  name: envName
}

resource kv 'Microsoft.KeyVault/vaults@2025-05-01' existing = {
  name: kvName
}

resource dbSecretIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${projectName}-db-secrets'
  location: location
}

resource backendSecretIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${projectName}-backend-secrets'
  location: location
}

resource dbKvAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dbSecretIdentity.id, kv.id, kvSecretsUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: dbSecretIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource backendKvAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(backendSecretIdentity.id, kv.id, kvSecretsUserRoleId)
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: backendSecretIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── PostgreSQL ────────────────────────────────────────────────────────────────
resource db 'Microsoft.App/containerapps@2025-10-02-preview' = {
  name: '${projectName}-db'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${dbSecretIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: env.id
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      secrets: [
        { name: 'db-password', keyVaultUrl: '${normalizedKvUri}secrets/DB-PASSWORD', identity: dbSecretIdentity.id }
        { name: 'db-user',     keyVaultUrl: '${normalizedKvUri}secrets/DB-USER',     identity: dbSecretIdentity.id }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 5432
        exposedPort: 5432
        transport: 'Tcp'
        traffic: [{ weight: 100, latestRevision: true }]
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          image: 'pgvector/pgvector:pg17'
          name: '${projectName}-db'
          env: [
            { name: 'POSTGRES_DB',       value: 'meisterpropr' }
            { name: 'POSTGRES_USER',     secretRef: 'db-user' }
            { name: 'POSTGRES_PASSWORD', secretRef: 'db-password' }
            { name: 'PGDATA',            value: '/mnt/db-storage/pgdata' }
          ]
          resources: { cpu: json('0.5'), memory: '1Gi' }
          probes: [
            { type: 'Liveness',  failureThreshold: 3,   periodSeconds: 10, successThreshold: 1, tcpSocket: { port: 5432 }, timeoutSeconds: 5 }
            { type: 'Readiness', failureThreshold: 48,  periodSeconds: 5,  successThreshold: 1, tcpSocket: { port: 5432 }, timeoutSeconds: 5 }
            { type: 'Startup',   failureThreshold: 240, periodSeconds: 1,  successThreshold: 1, initialDelaySeconds: 1, tcpSocket: { port: 5432 }, timeoutSeconds: 3 }
          ]
          volumeMounts: [{ volumeName: 'db-storage', mountPath: '/mnt/db-storage' }]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
      volumes: [
        {
          name: 'db-storage'
          storageType: 'AzureFile'
          storageName: storageAccountName
          mountOptions: 'uid=999,gid=999,dir_mode=0700,file_mode=0600'
        }
      ]
    }
  }
  dependsOn: [dbKvAccess]
}

// ── Backend ───────────────────────────────────────────────────────────────────
resource backend 'Microsoft.App/containerapps@2025-10-02-preview' = {
  name: '${projectName}-backend'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${backendSecretIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: env.id
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      secrets: [
        { name: 'db-connectionstring', keyVaultUrl: '${normalizedKvUri}secrets/DB-CONNECTIONSTRING', identity: backendSecretIdentity.id }
        { name: 'jwt-secret', keyVaultUrl: '${normalizedKvUri}secrets/MEISTER-JWT-SECRET', identity: backendSecretIdentity.id }
        { name: 'bootstrap-admin-user', keyVaultUrl: '${normalizedKvUri}secrets/MEISTER-BOOTSTRAP-ADMIN-USER', identity: backendSecretIdentity.id }
        { name: 'bootstrap-admin-password', keyVaultUrl: '${normalizedKvUri}secrets/MEISTER-BOOTSTRAP-ADMIN-PASSWORD', identity: backendSecretIdentity.id }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 8080
        transport: 'Auto'
        traffic: [{ weight: 100, latestRevision: true }]
        allowInsecure: true
      }
    }
    template: {
      containers: [
        {
          // Backend image is pulled from GHCR (published by the team)
          image: 'ghcr.io/meister-dev-ai/propr:${imageTag}'
          name: '${projectName}-backend'
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT',     value: 'Production' }
            { name: 'ADO_SKIP_TOKEN_VALIDATION',  value: 'false' }
            { name: 'DB_CONNECTION_STRING',       secretRef: 'db-connectionstring' }
            { name: 'MEISTER_JWT_SECRET',         secretRef: 'jwt-secret' }
            { name: 'MEISTER_BOOTSTRAP_ADMIN_USER', secretRef: 'bootstrap-admin-user' }
            { name: 'MEISTER_BOOTSTRAP_ADMIN_PASSWORD', secretRef: 'bootstrap-admin-password' }
          ]
          resources: { cpu: json('0.5'), memory: '1Gi' }
          probes: [
            { type: 'Liveness',  failureThreshold: 3,   periodSeconds: 10, successThreshold: 1, tcpSocket: { port: 8080 }, timeoutSeconds: 5 }
            { type: 'Readiness', failureThreshold: 48,  periodSeconds: 5,  successThreshold: 1, tcpSocket: { port: 8080 }, timeoutSeconds: 5 }
            { type: 'Startup',   failureThreshold: 240, periodSeconds: 1,  successThreshold: 1, initialDelaySeconds: 1, tcpSocket: { port: 8080 }, timeoutSeconds: 3 }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [backendKvAccess]
}

// ── Admin UI ──────────────────────────────────────────────────────────────────
resource adminUi 'Microsoft.App/containerapps@2025-10-02-preview' = {
  name: '${projectName}-admin-ui'
  location: location
  identity: { type: 'None' }
  properties: {
    managedEnvironmentId: env.id
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 80
        transport: 'Auto'
        traffic: [{ weight: 100, latestRevision: true }]
        allowInsecure: true
      }
    }
    template: {
      containers: [
        {
          // Admin UI image is pulled from GHCR (published by the team)
          image: 'ghcr.io/meister-dev-ai/propr/admin-ui:${imageTag}'
          name: '${projectName}-admin-ui'
          resources: { cpu: json('0.5'), memory: '1Gi' }
        }
      ]
      scale: { minReplicas: 0, maxReplicas: 10 }
    }
  }
}

// ── Reverse proxy ─────────────────────────────────────────────────────────────
resource reverseProxy 'Microsoft.App/containerapps@2025-10-02-preview' = {
  name: '${projectName}-reverse-proxy'
  location: location
  identity: { type: 'None' }
  properties: {
    managedEnvironmentId: env.id
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 80
        transport: 'Auto'
        traffic: [{ weight: 100, latestRevision: true }]
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          image: 'nginx:alpine'
          name: '${projectName}-reverse-proxy'
          command: ['/bin/sh', '-c']
          args: [reverseProxyStartCommand]
          env: [
            { name: 'ADMIN_UI_HOST', value: adminUiHost }
            { name: 'BACKEND_HOST',  value: backendHost }
          ]
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          probes: [
            { type: 'Liveness',  failureThreshold: 3,   periodSeconds: 10, successThreshold: 1, tcpSocket: { port: 80 }, timeoutSeconds: 5 }
            { type: 'Readiness', failureThreshold: 48,  periodSeconds: 5,  successThreshold: 1, tcpSocket: { port: 80 }, timeoutSeconds: 5 }
            { type: 'Startup',   failureThreshold: 240, periodSeconds: 1,  successThreshold: 1, initialDelaySeconds: 1, tcpSocket: { port: 80 }, timeoutSeconds: 3 }
          ]
        }
      ]
      scale: { minReplicas: 0, maxReplicas: 10 }
    }
  }
}

output reverseProxyFqdn string = reverseProxy.properties.configuration.ingress.fqdn
