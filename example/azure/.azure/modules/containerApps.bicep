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
envsubst '$FRONTEND_HOST $BACKEND_HOST' </tmp/default.conf.template >/etc/nginx/conf.d/default.conf
exec /docker-entrypoint.sh nginx -g 'daemon off;'
''', '@@TEMPLATE@@', nginxTemplate)

var normalizedKvUri = endsWith(kvUri, '/') ? kvUri : '${kvUri}/'
var frontendHost = '${projectName}-frontend.internal.${env.properties.defaultDomain}'
var backendHost = '${projectName}-backend.internal.${env.properties.defaultDomain}'
var proCursorHost = '${projectName}-procursor.internal.${env.properties.defaultDomain}'

// Built-in role definition IDs
var kvSecretsUserRoleId    = '4633458b-17de-408a-b874-0445c86b69e6'

var dbSecretIdentityName = '${projectName}-db-secrets'
var backendSecretIdentityName = '${projectName}-backend-secrets'
var proCursorSecretIdentityName = '${projectName}-procursor-secrets'

resource env 'Microsoft.App/managedEnvironments@2025-10-02-preview' existing = {
  name: envName
}

resource kv 'Microsoft.KeyVault/vaults@2025-05-01' existing = {
  name: kvName
}

resource dbSecretIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: dbSecretIdentityName
  location: location
}

resource backendSecretIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: backendSecretIdentityName
  location: location
}

resource proCursorSecretIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: proCursorSecretIdentityName
  location: location
}

resource dbKvAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kv
  name: guid(dbSecretIdentity.id, kv.id, kvSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: dbSecretIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource backendKvAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kv
  name: guid(backendSecretIdentity.id, kv.id, kvSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: backendSecretIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource proCursorKvAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: kv
  name: guid(proCursorSecretIdentity.id, kv.id, kvSecretsUserRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvSecretsUserRoleId)
    principalId: proCursorSecretIdentity.properties.principalId
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
  dependsOn: [dbKvAccess]
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
  dependsOn: [backendKvAccess]
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
        { name: 'procursor-shared-key', keyVaultUrl: '${normalizedKvUri}secrets/PROCURSOR-SHARED-KEY', identity: backendSecretIdentity.id }
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
            { name: 'PROCURSOR_REMOTE_MODE', value: 'proprManagedRemote' }
            { name: 'PROCURSOR_SERVICE_BASE_URL', value: 'http://${proCursorHost}' }
            { name: 'PROCURSOR_SHARED_KEY', secretRef: 'procursor-shared-key' }
            { name: 'MEISTER_DATA_PROTECTION_KEYS_PATH', value: '/app/.data-protection-keys' }
          ]
          resources: { cpu: json('0.5'), memory: '1Gi' }
          probes: [
            { type: 'Liveness',  failureThreshold: 3,   periodSeconds: 10, successThreshold: 1, tcpSocket: { port: 8080 }, timeoutSeconds: 5 }
            { type: 'Readiness', failureThreshold: 48,  periodSeconds: 5,  successThreshold: 1, tcpSocket: { port: 8080 }, timeoutSeconds: 5 }
            { type: 'Startup',   failureThreshold: 240, periodSeconds: 1,  successThreshold: 1, initialDelaySeconds: 1, tcpSocket: { port: 8080 }, timeoutSeconds: 3 }
          ]
          volumeMounts: [{ volumeName: 'data-protection-keys', mountPath: '/app/.data-protection-keys' }]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
      volumes: [
        {
          name: 'data-protection-keys'
          storageType: 'AzureFile'
          storageName: '${storageAccountName}-data-protection'
          mountOptions: 'uid=999,gid=999,dir_mode=0700,file_mode=0600'
        }
      ]
    }
  }
}

resource proCursor 'Microsoft.App/containerapps@2025-10-02-preview' = {
  name: '${projectName}-procursor'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${proCursorSecretIdentity.id}': {}
    }
  }
  dependsOn: [proCursorKvAccess, backend]
  properties: {
    managedEnvironmentId: env.id
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      secrets: [
        { name: 'db-connectionstring', keyVaultUrl: '${normalizedKvUri}secrets/DB-CONNECTIONSTRING', identity: proCursorSecretIdentity.id }
        { name: 'procursor-db-connectionstring', keyVaultUrl: '${normalizedKvUri}secrets/PROCURSOR-DB-CONNECTIONSTRING', identity: proCursorSecretIdentity.id }
        { name: 'jwt-secret', keyVaultUrl: '${normalizedKvUri}secrets/MEISTER-JWT-SECRET', identity: proCursorSecretIdentity.id }
        { name: 'procursor-shared-key', keyVaultUrl: '${normalizedKvUri}secrets/PROCURSOR-SHARED-KEY', identity: proCursorSecretIdentity.id }
      ]
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 8081
        transport: 'Auto'
        traffic: [{ weight: 100, latestRevision: true }]
        allowInsecure: true
      }
    }
    template: {
      containers: [
        {
          image: 'ghcr.io/meister-dev-ai/propr/procursor:${imageTag}'
          name: '${projectName}-procursor'
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'MEISTER_JWT_SECRET', secretRef: 'jwt-secret' }
            { name: 'PROCURSOR_PROPR_BASE_URL', value: 'http://${backendHost}' }
            { name: 'PROCURSOR_DB_CONNECTION_STRING', secretRef: 'procursor-db-connectionstring' }
            { name: 'PROCURSOR_SHARED_KEY', secretRef: 'procursor-shared-key' }
            { name: 'MEISTER_DATA_PROTECTION_KEYS_PATH', value: '/app/.data-protection-keys' }
          ]
          resources: { cpu: json('0.5'), memory: '1Gi' }
          probes: [
            { type: 'Liveness',  failureThreshold: 3,   periodSeconds: 10, successThreshold: 1, tcpSocket: { port: 8081 }, timeoutSeconds: 5 }
            { type: 'Readiness', failureThreshold: 48,  periodSeconds: 5,  successThreshold: 1, tcpSocket: { port: 8081 }, timeoutSeconds: 5 }
            { type: 'Startup',   failureThreshold: 240, periodSeconds: 1,  successThreshold: 1, initialDelaySeconds: 1, tcpSocket: { port: 8081 }, timeoutSeconds: 3 }
          ]
          volumeMounts: [{ volumeName: 'data-protection-keys', mountPath: '/app/.data-protection-keys' }]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
      volumes: [
        {
          name: 'data-protection-keys'
          storageType: 'AzureFile'
          storageName: '${storageAccountName}-data-protection'
          mountOptions: 'uid=999,gid=999,dir_mode=0700,file_mode=0600'
        }
      ]
    }
  }
}

// ── Frontend ──────────────────────────────────────────────────────────────────
resource frontend 'Microsoft.App/containerapps@2025-10-02-preview' = {
  name: '${projectName}-frontend'
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
        targetPort: 8080
        transport: 'Auto'
        traffic: [{ weight: 100, latestRevision: true }]
        allowInsecure: true
      }
    }
    template: {
      containers: [
        {
          // Frontend image is pulled from GHCR (published by the team)
          image: 'ghcr.io/meister-dev-ai/propr/frontend:${imageTag}'
          name: '${projectName}-frontend'
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
        targetPort: 8080
        transport: 'Auto'
        traffic: [{ weight: 100, latestRevision: true }]
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          // Unprivileged nginx variant: runs as UID 101 (non-root) and listens on 8080
          // (nginx-aca.conf is set to match). The runtime env-substitution start command
          // writes the rendered config into /etc/nginx/conf.d as that user.
          image: 'nginxinc/nginx-unprivileged:alpine'
          name: '${projectName}-reverse-proxy'
          command: ['/bin/sh', '-c']
          args: [reverseProxyStartCommand]
          env: [
            { name: 'FRONTEND_HOST', value: frontendHost }
            { name: 'BACKEND_HOST',  value: backendHost }
          ]
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          probes: [
            { type: 'Liveness',  failureThreshold: 3,   periodSeconds: 10, successThreshold: 1, tcpSocket: { port: 8080 }, timeoutSeconds: 5 }
            { type: 'Readiness', failureThreshold: 48,  periodSeconds: 5,  successThreshold: 1, tcpSocket: { port: 8080 }, timeoutSeconds: 5 }
            { type: 'Startup',   failureThreshold: 240, periodSeconds: 1,  successThreshold: 1, initialDelaySeconds: 1, tcpSocket: { port: 8080 }, timeoutSeconds: 3 }
          ]
        }
      ]
      scale: { minReplicas: 0, maxReplicas: 10 }
    }
  }
}

output reverseProxyFqdn string = reverseProxy.properties.configuration.ingress.fqdn
