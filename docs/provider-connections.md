# Provider Connections And Authentication

This guide is the canonical reference for configuring source-control provider connections in ProPR.
It explains:

- which providers are supported,
- which authentication modes are accepted,
- what each field means,
- where to obtain the required values, and
- how provider connections relate to scopes and reviewer identities.

Use this document when configuring providers in the Admin UI or when automating the same setup
through the API.

## Tenant Sign-In Providers vs SCM Provider Connections

This page covers SCM provider connections used for repository discovery, crawling, and review publication. Tenant-scoped sign-in providers are a separate configuration surface used only for user authentication.

Tenant sign-in providers are managed under `/api/admin/tenants/{tenantId}/sso-providers` and support the launch identity-provider set:

- `EntraId` over `Oidc`
- `Google` over `Oidc`
- `GitHub` over `Oauth2`

Those records store the display name, protected client secret, allowed email domains, enabled state, and auto-provisioning policy for tenant login. They do not replace SCM provider connections, reviewer identities, or webhook configuration.

## Support Matrix

| Provider | Host Base URL | Supported authentication kind | Extra required fields | Secret field expects | Status |
|---|---|---|---|---|---|
| Azure DevOps | `https://dev.azure.com` | `oauthClientCredentials` | `oAuthTenantId`, `oAuthClientId` | Azure app registration client secret value | Supported |
| GitHub | `https://github.com` or your GitHub Enterprise base URL | `personalAccessToken` | none | GitHub PAT | Supported |
| GitLab | `https://gitlab.com` or your self-managed base URL | `personalAccessToken` | none | GitLab PAT | Supported |
| Forgejo | your Forgejo base URL, for example `https://codeberg.org` | `personalAccessToken` | none | Forgejo access token | Supported |
| Any provider | n/a | `appInstallation` | n/a | n/a | Not implemented; rejected by the server |

## Common Provider Connection Fields

Every provider connection stores the same normalized shape.

| Field | Meaning | Notes |
|---|---|---|
| `providerFamily` | Provider type | One of `azureDevOps`, `github`, `gitLab`, `forgejo` |
| `hostBaseUrl` | Provider host root | This is the provider host, not a repository URL |
| `authenticationKind` | Credential model | Must match the support matrix above |
| `oAuthTenantId` | Tenant or directory identifier | Only used for Azure DevOps `oauthClientCredentials` |
| `oAuthClientId` | OAuth or app client identifier | Only used for Azure DevOps `oauthClientCredentials` |
| `displayName` | Friendly label in the Admin UI | Any descriptive name |
| `secret` | Protected credential material | Stored encrypted; never returned in API responses |
| `isActive` | Whether this connection is operational | Review, discovery, and webhook flows use active connections only |

## Provider Scope Fields

Provider connections define how ProPR authenticates. Provider scopes define what part of the
provider the client is allowed to use.

| Field | Meaning | Azure DevOps example |
|---|---|---|
| `scopeType` | Logical scope category | `organization` |
| `externalScopeId` | Provider-native short identifier | `my-org` |
| `scopePath` | Canonical provider URL or path | `https://dev.azure.com/my-org` |
| `displayName` | Friendly label | `My Org` |
| `isEnabled` | Whether ProPR may use this scope | `true` |

For Azure DevOps, the connection host is `https://dev.azure.com`, while the organization itself is
stored as a scope such as `https://dev.azure.com/my-org`.

## Reviewer Identity Fields

Reviewer identity is configured separately from the provider connection.

| Field | Meaning |
|---|---|
| `externalUserId` | Provider-native reviewer identifier |
| `login` | Normalized login or unique-name field |
| `displayName` | Human-readable identity name |
| `isBot` | Whether the identity represents a bot or service account |

For Azure DevOps, `externalUserId` is the VSS identity GUID. The easiest way to obtain it is to use
the reviewer-identity resolution flow in the Admin UI after the provider connection and scope are in
place.

## Azure DevOps

Azure DevOps supports only `oauthClientCredentials`.

That means the provider connection is backed by a Microsoft Entra application and client secret.
`appInstallation` is not implemented for Azure DevOps in ProPR.

### What Goes Into Each Field

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | `https://dev.azure.com` | Fixed value for Azure DevOps Services |
| `authenticationKind` | `oauthClientCredentials` | Fixed supported mode for Azure DevOps |
| `oAuthTenantId` | Microsoft Entra tenant ID (directory ID) | Azure Portal -> Microsoft Entra ID -> Overview -> Tenant ID, or App registrations -> your app -> Overview -> Directory (tenant) ID |
| `oAuthClientId` | Application (client) ID | Azure Portal -> App registrations -> your app -> Overview -> Application (client) ID |
| `secret` | Client secret value | Azure Portal -> App registrations -> your app -> Certificates & secrets -> Client secrets -> Value |
| `displayName` | Friendly label | Any descriptive value |

### Important Azure DevOps Notes

1. Use the client secret value, not the secret ID.
2. Use the tenant ID, not a subscription ID and not the service principal object ID.
3. The organization does not go into `hostBaseUrl`; it goes into the provider scope.
4. The service principal must be usable against the target Azure DevOps organization and project.
5. The reviewer identity is a separate configuration step after the connection and scope exist.

### Azure DevOps Organization Scope Example

| ProPR field | Example value | How to derive it |
|---|---|---|
| `scopeType` | `organization` | Fixed value |
| `externalScopeId` | `my-org` | The `{org}` part of `https://dev.azure.com/my-org` |
| `scopePath` | `https://dev.azure.com/my-org` | Full organization URL |
| `displayName` | `My Org` | Friendly label |

### Azure DevOps Reviewer Identity

After the connection and at least one enabled organization scope are configured:

1. Use the Admin UI reviewer-identity resolve action.
2. Search for the display name of the user or service principal you want ProPR to act as.
3. Save the returned identity.

ProPR stores the Azure DevOps reviewer identity as a provider-scoped normalized record. For Azure
DevOps, the key value is the VSS identity GUID.

### Global Azure Fallback

If a client does not have its own Azure DevOps provider connection, some Azure DevOps operations can
fall back to the global Azure credential configured for the backend process.

The expected environment variables are:

```env
AZURE_TENANT_ID=<tenant-id>
AZURE_CLIENT_ID=<application-client-id>
AZURE_CLIENT_SECRET=<client-secret-value>
```

If you use an env file for container startup, do not put spaces around the equals sign.

## GitHub

GitHub supports only `personalAccessToken`.

### What Goes Into Each Field

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | `https://github.com` or your GitHub Enterprise base URL | GitHub Cloud uses `https://github.com`; GitHub Enterprise uses the root host URL |
| `authenticationKind` | `personalAccessToken` | Fixed supported mode for GitHub |
| `secret` | GitHub personal access token | GitHub -> Settings -> Developer settings -> Personal access tokens |
| `oAuthTenantId` | leave empty | Not used |
| `oAuthClientId` | leave empty | Not used |

The token must be able to authenticate the `/user` endpoint and access the repositories and pull
request data ProPR needs for review and publication.

## GitLab

GitLab supports only `personalAccessToken`.

### What Goes Into Each Field

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | `https://gitlab.com` or your GitLab base URL | Use the root GitLab URL |
| `authenticationKind` | `personalAccessToken` | Fixed supported mode for GitLab |
| `secret` | GitLab personal access token | GitLab -> User Settings -> Access Tokens |
| `oAuthTenantId` | leave empty | Not used |
| `oAuthClientId` | leave empty | Not used |

The token must be able to authenticate the `/api/v4/user` endpoint and access the projects, merge
requests, discussions, and users ProPR needs. `read_api` is sufficient for verification and read-side
discovery, but review publication posts merge request discussions through the REST API and therefore
requires the legacy `api` scope.

## Forgejo

Forgejo supports only `personalAccessToken`.

### What Goes Into Each Field

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | Your Forgejo host base URL | Example: `https://codeberg.org` |
| `authenticationKind` | `personalAccessToken` | Fixed supported mode for Forgejo |
| `secret` | Forgejo access token | Forgejo -> user settings -> applications or access-token page |
| `oAuthTenantId` | leave empty | Not used |
| `oAuthClientId` | leave empty | Not used |

The token must be able to authenticate the `/api/v1/user` endpoint and access the repositories and
pull requests ProPR needs.

## Unsupported Authentication Modes

`appInstallation` exists in the shared schema as a reserved authentication mode. The server rejects
this value for the supported providers instead of silently accepting unusable configuration.

## Troubleshooting

### HTTP 400 when switching Azure DevOps to OAuth

Check these first:

1. `authenticationKind` must be exactly `oauthClientCredentials`.
2. `oAuthTenantId` and `oAuthClientId` must both be present for Azure DevOps.
3. Azure DevOps does not accept `appInstallation`.
4. `hostBaseUrl` must stay `https://dev.azure.com`; the organization belongs in the scope.
5. If you rebuilt the code locally but not the containers, restart the stack so the Admin UI and API
   use the updated code.

### Which Azure ID is the tenant ID?

Use the Microsoft Entra tenant or directory ID.

Do not use:

- the subscription ID,
- the service principal object ID,
- the secret ID.

### Connection verifies but reviewer identity resolution fails

Check these in order:

1. The connection is active.
2. The connection uses the supported auth kind for that provider.
3. At least one provider scope exists and is enabled.
4. The target identity exists in the provider scope you are searching.
5. A reviewer identity row was actually saved after resolution.

## Examples

### Azure DevOps provider connection

```json
{
  "providerFamily": "azureDevOps",
  "hostBaseUrl": "https://dev.azure.com",
  "authenticationKind": "oauthClientCredentials",
  "oAuthTenantId": "<tenant-id>",
  "oAuthClientId": "<application-client-id>",
  "displayName": "Contoso Azure DevOps",
  "secret": "<client-secret-value>",
  "isActive": true
}
```

### GitHub provider connection

```json
{
  "providerFamily": "github",
  "hostBaseUrl": "https://github.com",
  "authenticationKind": "personalAccessToken",
  "displayName": "GitHub Cloud",
  "secret": "<github-pat>",
  "isActive": true
}
```

### Azure DevOps organization scope

```json
{
  "scopeType": "organization",
  "externalScopeId": "my-org",
  "scopePath": "https://dev.azure.com/my-org",
  "displayName": "My Org",
  "isEnabled": true
}
```

## AI Provider Profiles

AI profiles are configured separately from source-control provider connections. They control which AI
provider ProPR uses for review, memory, and embedding workloads for a client.

### Common AI Profile Fields

| Field | Meaning | Notes |
|---|---|---|
| `displayName` | Friendly profile label | Shown in the Admin UI profile list |
| `providerKind` | AI provider family | Supported values include `azureOpenAi`, `openAi`, and `liteLlm` |
| `baseUrl` | Exact provider endpoint or gateway URL | Preserved exactly as entered |
| `auth.mode` | Authentication mode | Usually `apiKey`; Azure-hosted profiles can also use `azureIdentity` |
| `configuredModels` | Models available under this profile | Chat and embedding models are stored together |
| `purposeBindings` | Runtime purpose-to-model mapping | Drives review, memory, and embedding resolution |
| `discoveryMode` | Model onboarding mode | `providerCatalog` or `manualOnly` |
| `defaultHeaders` | Optional request header overrides | Advanced setting for gateway or proxy-specific requirements |
| `defaultQueryParams` | Optional query-string overrides | Advanced setting for fixed provider or gateway parameters such as `api-version` |

### Which AI Bindings Are Required?

An AI profile is activation-ready when these bindings are valid and enabled:

1. `reviewDefault`
2. `memoryReconsideration`
3. `embeddingDefault`

The effort-specific review bindings are optional overrides:

- `reviewLowEffort`
- `reviewMediumEffort`
- `reviewHighEffort`

If one of those effort-specific bindings is missing or disabled, ProPR falls back to `reviewDefault`
at runtime instead of forcing duplicate configuration.

### When Are Default Headers Or Query Parameters Needed?

Most standard Azure OpenAI, OpenAI, and LiteLLM profiles do not need either field.

Use them only when the provider endpoint or gateway requires request-shaping overrides such as:

1. A fixed query parameter like `api-version=2024-10-21`
2. A custom proxy or gateway header
3. A self-hosted compatibility layer that expects additional static request metadata

Treat both fields as advanced settings, not mandatory setup steps.

### Azure-Hosted Endpoint Rule

Use `providerKind: "azureOpenAi"` for all Azure-hosted AI endpoints, including:

1. `*.openai.azure.com`
2. `*.services.ai.azure.com`
3. Azure AI Foundry endpoints that the portal labels as an "OpenAI endpoint"

Use `providerKind: "openAi"` only for true OpenAI-hosted or OpenAI-compatible endpoints, not Azure-hosted ones.
