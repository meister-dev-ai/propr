# Azure Infrastructure (Bicep)

Deploys the full ProPR stack to Azure Container Apps.

Note: This is only one way of many to host ProPR in Azure (or somewhere else).

## Structure

```
example/azure/.azure/
  main.bicep                        # Entry point — all parameters, derived names, module wiring
  main.bicepparam                   # Parameter file (fill in before deploying, never commit secrets)
  deploy.ps1                        # End-to-end deployment script (public images → infra → apps)
  modules/
    observability.bicep             # Log Analytics workspace
    network.bicep                   # VNet + infrastructure subnet (delegated to Container Apps)
    keyvault.bicep                  # Key Vault (RBAC mode)
    kvSecrets.bicep                 # Key Vault secret values
    storage.bicep                   # Storage account + postgres-data file share (Azure Files)
    containerEnvironment.bicep      # Container Apps managed environment + VNet + storage mount
    containerApps.bicep             # All container apps + Key Vault access role assignments
  nginx/
    nginx-aca.conf
```

## Deployed resources

| Resource | Name pattern | Notes |
|---|---|---|
| Log Analytics workspace | `workspace-{projectName}` | |
| Virtual network | `{projectName}-vnet` | Subnet `10.0.0.0/23` delegated to `Microsoft.App/environments` |
| Key Vault | `kv{projectName-no-hyphens}{suffix}` | RBAC mode; suffix is deterministic from resource group ID |
| Storage account | `{projectName-no-hyphens}sg` | Azure Files share `postgres-data` for PostgreSQL volume |
| Container Apps environment | `managedEnvironment-{projectName}` | System-assigned identity; VNet-integrated |
| Container App: reverse proxy | `{projectName}-reverse-proxy` | External ingress; official nginx image from Docker Hub |
| Container App: backend | `{projectName}-backend` | Internal ingress; secrets from Key Vault |
| Container App: admin-ui | `{projectName}-admin-ui` | Internal ingress |
| Container App: db | `{projectName}-db` | Internal ingress; TCP on 5432; pgvector image from Docker Hub; Azure Files volume |

## Parameters

| Parameter | Required | Default | Description |
|---|---|---|---|
| `projectName` | | `meister-propr` | Drives all resource names |
| `location` | | `switzerlandnorth` | Azure region |
| `imageTag` | | auto-resolved by `deploy.ps1` | Published tag shared by the backend and admin-ui images |
| `deployApps` | | `true` | Set to `false` if you want infrastructure only |
| `dbConnectionString` | | | Optional PostgreSQL connection string override (**secure**, stored in Key Vault). If omitted, the deployment derives the internal database app connection string. |
| `jwtSecret` | Yes | | Backend JWT signing secret (**secure**, stored in Key Vault). Must be at least 32 characters. |
| `bootstrapAdminUser` | | `admin` | Username for the initial admin account seeded on first startup |
| `bootstrapAdminPassword` | Yes | | Password for the initial admin account (**secure**, stored in Key Vault) |
| `dbUser` | Yes | | PostgreSQL username (**secure**, stored in Key Vault) |
| `dbPassword` | Yes | | PostgreSQL password (**secure**, stored in Key Vault) |

Fill in non-secret values in `main.bicepparam`. Pass all `@secure()` parameters on the command line — never commit them.

## Deployment

### Prerequisites

- PowerShell 7+
- Azure CLI (`az bicep install` for Bicep support)
- An existing resource group

### Using the deploy script (recommended)

`deploy.ps1` handles the full lifecycle in one pass: deploy infrastructure and container apps using public images from GHCR and Docker Hub. No local image build or registry push is required.

If you omit `ImageTag`, the script queries GHCR and picks the newest published tag that exists for both backend and admin-ui. If you pass `ImageTag`, the script verifies it exists before deploying.

#### Interactive — prompts for anything not supplied

```powershell
./example/azure/.azure/deploy.ps1 -ResourceGroup my-rg
```

#### Fully scripted via config object

```powershell
$cfg = @{
    ResourceGroup      = 'my-rg'
    Location           = 'westeurope'
  BootstrapAdminUser = 'admin'
  BootstrapAdminPassword = '...'
  JwtSecret          = 'a-32-character-or-longer-random-secret'
    DbUser             = 'postgres'
    DbPassword         = '...'
}
./example/azure/.azure/deploy.ps1 -Config $cfg
```

Individual parameters always take precedence over `Config` values, so you can mix both:

```powershell
./example/azure/.azure/deploy.ps1 -Config $cfg -ImageTag v2.1
```

### Manual deployment (Bicep only)

If you prefer to run the Bicep steps yourself, the public images can be pulled directly. No registry prep is needed.

**Step 1 — provision everything in one pass:**

```bash
az deployment group create \
  --resource-group <your-rg> \
  --template-file example/azure/.azure/main.bicep \
  --parameters example/azure/.azure/main.bicepparam \
  --parameters imageTag=<published-tag> \
  --parameters jwtSecret="...32+ chars..." \
               bootstrapAdminPassword="..." \
               dbUser="..." \
               dbPassword="..."
```

`bootstrapAdminUser` defaults to `admin`. Reuse the same `jwtSecret` on subsequent deployments if you want existing sessions and tokens to remain valid.

If you want to use an external PostgreSQL instance instead of the deployed internal db app, pass `dbConnectionString="Host=...;..."` explicitly.

If you want infrastructure only, pass `deployApps=false` instead and rerun later with `deployApps=true`.

The reverse proxy URL is a deployment output:

```bash
az deployment group show \
  --resource-group <your-rg> --name main \
  --query properties.outputs.reverseProxyUrl.value -o tsv
```

Public routes follow the local docker-compose example:

- `/` serves the admin UI
- `/api/` proxies to the backend and strips the `/api` prefix before forwarding

### Subsequent deployments

Re-run the deploy script with a published `ImageTag` to roll out updated backend and admin-ui images, or omit `ImageTag` to use the newest common published tag. The nginx and pgvector images stay on their fixed public tags.

## Image sources

The deployment pulls these public images directly:

- Backend: `ghcr.io/meister-dev-ai/propr:<tag>`
- Admin UI: `ghcr.io/meister-dev-ai/propr/admin-ui:<tag>`
- Reverse proxy: `nginx:alpine`
- Database: `pgvector/pgvector:pg17`

No Azure Container Registry is provisioned and no image build or push step is required. If the GHCR images are private in your environment, add registry credentials to the container app configuration before deploying.

## Secret management

All runtime secrets are stored in Key Vault and referenced by the container apps via Key Vault secret references. The backend and db container apps use dedicated user-assigned managed identities with `Key Vault Secrets User` on the vault. The backend reads `DB-CONNECTIONSTRING`, `MEISTER-JWT-SECRET`, `MEISTER-BOOTSTRAP-ADMIN-USER`, and `MEISTER-BOOTSTRAP-ADMIN-PASSWORD`; the db reads `DB-USER` and `DB-PASSWORD`.
