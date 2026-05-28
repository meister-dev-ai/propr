# Provider Connections And Authentication

This guide is the canonical reference for configuring source-control provider connections in ProPR.
It explains:

- which providers are supported,
- which authentication modes are accepted,
- what each field means,
- where to obtain the required values, and
- how provider connections relate to scopes and reviewer identities.

Use this document when configuring providers in the frontend or when automating the same setup
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
| Azure DevOps | `https://dev.azure.com` or `https://{org}.visualstudio.com` | `oauthClientCredentials` | `oAuthTenantId`, `oAuthClientId` | Azure app registration client secret value | Supported |
| Azure DevOps | self-hosted Azure DevOps Server host, for example `https://ado-server.example.com/tfs` | `personalAccessToken` | none | Azure DevOps Server PAT | Supported |
| Azure DevOps | self-hosted Azure DevOps Server host, for example `https://ado-server.example.com/tfs` | `windowsUserAccount` | `userName` | Windows account password | Supported |
| GitHub | `https://github.com` or your GitHub Enterprise base URL | `personalAccessToken` | none | GitHub PAT | Supported |
| GitHub | `https://github.com` or your GitHub Enterprise base URL | `appInstallation` | `gitHubAppId`, `gitHubAppInstallationId` | GitHub App private key PEM | Supported |
| GitLab | `https://gitlab.com` or your self-managed base URL | `personalAccessToken` | none | GitLab PAT | Supported |
| Forgejo | your Forgejo base URL, for example `https://codeberg.org` | `personalAccessToken` | none | Forgejo access token | Supported |
| Any provider except GitHub | n/a | `appInstallation` | n/a | n/a | Not implemented; rejected by the server |

## Common Provider Connection Fields

Every provider connection stores the same normalized shape.

| Field | Meaning | Notes |
|---|---|---|
| `providerFamily` | Provider type | One of `azureDevOps`, `github`, `gitLab`, `forgejo` |
| `hostBaseUrl` | Provider host root | This is the provider host, not a repository URL |
| `authenticationKind` | Credential model | Must match the support matrix above |
| `userName` | Non-secret Windows account login | Only used for Azure DevOps Server `windowsUserAccount` |
| `oAuthTenantId` | Tenant or directory identifier | Only used for Azure DevOps `oauthClientCredentials` |
| `oAuthClientId` | OAuth or app client identifier | Only used for Azure DevOps `oauthClientCredentials` |
| `gitHubAppId` | GitHub App numeric identifier | Only used for GitHub `appInstallation` |
| `gitHubAppInstallationId` | GitHub App installation numeric identifier | Only used for GitHub `appInstallation` |
| `displayName` | Friendly label in the frontend | Any descriptive name |
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
the reviewer-identity resolution flow in the frontend after the provider connection and scope are in
place.

## Azure DevOps

Azure DevOps authentication depends on the host variant:

- Azure DevOps Services hosted on `https://dev.azure.com` or `*.visualstudio.com` uses `oauthClientCredentials`.
- Self-hosted Azure DevOps Server uses `personalAccessToken` or `windowsUserAccount`.
- `appInstallation` is not implemented for Azure DevOps in ProPR.

### Azure DevOps Services: What Goes Into Each Field

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

### Azure DevOps Server: PAT

Use `personalAccessToken` for self-hosted Azure DevOps Server when PATs are enabled on the server.

`hostBaseUrl` must use `https://` for Azure DevOps Server to function correctly. HTTP endpoints are rejected during validation.

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | Your Azure DevOps Server base URL | Example: `https://ado-server.example.com/tfs` |
| `authenticationKind` | `personalAccessToken` | Select PAT mode for the self-hosted host |
| `secret` | Azure DevOps Server PAT | Create it from the Azure DevOps Server user settings area if PATs are enabled |
| `userName` | leave empty | Not used for PAT mode |
| `oAuthTenantId` | leave empty | Not used |
| `oAuthClientId` | leave empty | Not used |

### Azure DevOps Server: Windows User Account

Use `windowsUserAccount` for self-hosted Azure DevOps Server when the server accepts explicit Windows credentials.

`hostBaseUrl` must use `https://` for Azure DevOps Server to function correctly. HTTP endpoints are rejected during validation.

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | Your Azure DevOps Server base URL | Example: `https://ado-server.example.com/tfs` |
| `authenticationKind` | `windowsUserAccount` | Select Windows user-account mode for the self-hosted host |
| `userName` | Windows login recognized by the server | Example: `CONTOSO\ado-user` |
| `secret` | Password for that Windows account | Enter the account password directly |
| `oAuthTenantId` | leave empty | Not used |
| `oAuthClientId` | leave empty | Not used |

Important Azure DevOps Server notes:

1. `userName` is stored separately from the password and is returned in API responses for edit flows.
1. The password remains protected secret material and is never returned in API responses.
1. ProPR uses explicit stored credentials only; host-integrated Windows authentication is out of scope.
1. If ProPR runs on Linux, WSL, or inside containers, the Azure DevOps Server certificate chain must be trusted inside that runtime environment. This is currently not managed by ProPR.
1. For `windowsUserAccount`, prefer the server-recognized Windows login shape such as `CONTOSO\ado-user` so the runtime can split domain and account name correctly for NTLM or Negotiate authentication.
1. ProPR enables managed NTLM on Linux and WSL runtimes by default.
1. Self-hosted Azure DevOps readiness may remain onboarding-ready after verification until lifecycle and observability proof are complete.

### Azure DevOps Server Onboarding Checklist

Use this sequence for a clean Azure DevOps Server rollout:

1. Expose Azure DevOps Server over HTTPS.
1. Verify the HTTPS endpoint is reachable from the machine or container where ProPR runs, not only from the local browser.
1. Trust the Azure DevOps Server certificate chain inside the ProPR runtime environment.
1. Create one Azure DevOps provider connection with the Azure DevOps Server host root, for example `https://ado-server.example.com/tfs`.
1. Choose either `personalAccessToken` or `windowsUserAccount`.
1. For `windowsUserAccount`, enter `userName` in the format accepted by the server, for example `CONTOSO\ado-user`.
  * Note: In any case, the user must have at least Basic access level and permissions to read the target collection, repositories, and pull requests.
1. Save the connection.
1. Add one enabled `organization` scope that points to the actual Azure DevOps Server collection or organization URL, for example `https://ado-server.example.com/tfs/DefaultCollection`.
1. Run connection verification.
1. Resolve and save the reviewer trigger identity only after the connection and at least one enabled scope verify successfully.

### Azure DevOps Server On-Prem Onboarding Walkthrough

Use this when bringing an existing on-prem Azure DevOps Server into ProPR for the first time.

#### 1. Prepare the Azure DevOps Server endpoint

Before creating anything in ProPR:

1. Choose the exact host name or IP address ProPR will use.
1. Make sure the Azure DevOps Server certificate subject alternative names cover that host or IP.
1. Confirm the server root and collection URLs both use `https://`.
1. Confirm the ProPR runtime can reach the server over the intended port.

Typical values:

- Connection `hostBaseUrl`: `https://ado-server.example.com/tfs`
- Scope `scopePath`: `https://ado-server.example.com/tfs/DefaultCollection`

#### 2. Trust the certificate in the ProPR runtime

If ProPR runs on Linux, WSL, or in containers, certificate trust must be installed there directly.

1. Test the target endpoint from the same runtime with `curl https://<ado-server-host>/`.
2. If the server uses a private CA or self-signed cert, install that certificate or issuing CA into the runtime trust store.
3. Re-test without `-k` or any insecure override.

For Debian or Ubuntu-based runtimes:

```bash
sudo cp <your-cert>.crt /usr/local/share/ca-certificates/
sudo update-ca-certificates
curl https://<ado-server-host>/
```

The expected pre-onboarding result is that HTTPS reaches the server and either returns the normal Azure DevOps page or an authentication challenge such as `401`, not a timeout or certificate error.

#### 3. Create the provider connection in ProPR

In the client provider-connections UI:

1. Choose `Azure DevOps`.
2. Set `hostBaseUrl` to the Azure DevOps Server root, for example `https://ado-server.example.com/tfs`.
3. Pick the auth mode:
   - `personalAccessToken` when the server supports PATs
   - `windowsUserAccount` when the server accepts explicit Windows credentials
4. For `windowsUserAccount`, enter `userName` in the format your server accepts, preferably `DOMAIN\user`, for example `CONTOSO\ado-user`.
5. Save the connection.

Do not use `https://dev.azure.com` for on-prem Azure DevOps Server. That host is only for Azure DevOps Services.

#### 4. Add the collection scope

After the connection exists:

1. Add an enabled `organization` scope.
2. Set the scope path to the actual collection URL ProPR should operate against.
3. Use a friendly display name so the collection is obvious in the admin UI.

Example:

| ProPR field | Example value |
|---|---|
| `scopeType` | `organization` |
| `externalScopeId` | `DefaultCollection` |
| `scopePath` | `https://ado-server.example.com/tfs/DefaultCollection` |
| `displayName` | `Default Collection` |

#### 5. Verify the connection and scope

Run verification only after the connection and scope point at the final HTTPS host.

Successful verification means:

1. The ProPR runtime can reach the server.
1. The runtime trusts the certificate chain.
1. The chosen credentials can authenticate.
1. The configured scope can be discovered.

If verification fails, check the exact error message before changing credentials. On-prem onboarding failures are commonly caused by:

1. HTTP instead of HTTPS
1. an untrusted certificate inside WSL/Linux/container runtime
1. a scope still pointing at an old host or old collection URL
1. Windows user name format mismatch
1. PATs disabled on the Azure DevOps Server instance

#### 6. Resolve the reviewer identity

After verification succeeds:

1. Open reviewer identity resolution for that same connection.
1. Search for the user or service account ProPR should use as the trigger identity.
1. Save the resolved identity.

This step is connection-scoped. Resolve the identity only after the correct on-prem host and scope verify successfully.

#### 7. Enable review automation

After the connection, scope, and reviewer identity are in place, continue with the normal crawl, webhook, and review flows. ProPR still treats Azure DevOps Server as the shared `azureDevOps` provider family, so the remaining workflow is the same shared path once onboarding is complete.

### Azure DevOps Server In WSL Or Containers

If ProPR runs in WSL, Linux, or a container:

1. Test reachability from that environment directly with `curl https://<ado-server-host>/`.
1. If TLS fails with a self-signed or unknown-issuer error, install the Azure DevOps Server certificate or issuing CA into the Linux trust store used by that runtime.
1. For Debian or Ubuntu-based environments, place the certificate under `/usr/local/share/ca-certificates/*.crt` and run `update-ca-certificates`.
1. Re-test `curl` without `-k` before retrying ProPR verification.
1. For Azure DevOps Server `windowsUserAccount`, use a domain-qualified login such as `CONTOSO\ado-user` when the server expects domain credentials.
1. ProPR enables managed NTLM by default on Linux and WSL. If Windows-auth verification still fails in a specific runtime, install `gss-ntlmssp` as a fallback and restart the app.
1. If TCP connect itself times out, fix the Windows Firewall, Hyper-V firewall, or network routing issue before troubleshooting authentication.

### Azure DevOps Organization Scope Example

| ProPR field | Example value | How to derive it |
|---|---|---|
| `scopeType` | `organization` | Fixed value |
| `externalScopeId` | `my-org` | The `{org}` part of `https://dev.azure.com/my-org` |
| `scopePath` | `https://dev.azure.com/my-org` | Full organization URL |
| `displayName` | `My Org` | Friendly label |

### Azure DevOps Reviewer Identity

After the connection and at least one enabled organization scope are configured:

1. Use the frontend reviewer-identity resolve action.
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

GitHub supports both `personalAccessToken` and `appInstallation`.

### GitHub Personal Access Token

### What Goes Into Each Field

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | `https://github.com` or your GitHub Enterprise base URL | GitHub Cloud uses `https://github.com`; GitHub Enterprise uses the root host URL |
| `authenticationKind` | `personalAccessToken` | Select PAT mode for GitHub |
| `secret` | GitHub personal access token | GitHub -> Settings -> Developer settings -> Personal access tokens |
| `oAuthTenantId` | leave empty | Not used |
| `oAuthClientId` | leave empty | Not used |
| `gitHubAppId` | leave empty | Not used |
| `gitHubAppInstallationId` | leave empty | Not used |

The token must be able to authenticate the `/user` endpoint and access the repositories and pull
request data ProPR needs for review and publication.

### GitHub App Installation

Use `appInstallation` when you want ProPR to operate through an installed GitHub App instead of a
user PAT.

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | `https://github.com` or your GitHub Enterprise base URL | GitHub Cloud uses `https://github.com`; GitHub Enterprise uses the root host URL |
| `authenticationKind` | `appInstallation` | Select GitHub App mode |
| `gitHubAppId` | Numeric GitHub App ID | GitHub -> Settings -> Developer settings -> GitHub Apps -> your app -> App ID |
| `gitHubAppInstallationId` | Numeric installation ID | GitHub App installation URL, GitHub App settings, or installation API metadata |
| `secret` | GitHub App private key PEM | GitHub -> Settings -> Developer settings -> GitHub Apps -> your app -> Generate private key |
| `oAuthTenantId` | leave empty | Not used |
| `oAuthClientId` | leave empty | Not used |

Important GitHub App notes:

1. ProPR stores the private key encrypted at rest but never returns it from the API.
2. ProPR does not persist installation access tokens; it mints them on demand and reuses them only
   in a bounded in-memory cache until shortly before expiry.
3. Discovery, reviewer lookup, review fetch, and review publication run through the repositories and
   collaborators visible to the configured installation.
4. PAT-backed and GitHub App-backed connections can coexist as separate GitHub host connections.

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

`appInstallation` remains unsupported for Azure DevOps, GitLab, and Forgejo. The shared schema keeps
the enum value so provider-specific validation can reject unsupported combinations explicitly.

## Troubleshooting

### HTTP 400 when switching Azure DevOps authentication

Check these first:

1. Hosted Azure DevOps Services accepts only `oauthClientCredentials`.
2. Self-hosted Azure DevOps Server accepts only `personalAccessToken` or `windowsUserAccount`.
3. `userName` is required for `windowsUserAccount` and must be empty for other Azure DevOps auth kinds.
4. `oAuthTenantId` and `oAuthClientId` must both be present only for hosted Azure DevOps OAuth.
5. Azure DevOps does not accept `appInstallation`.
6. For Azure DevOps Services, `hostBaseUrl` stays `https://dev.azure.com`; the organization belongs in the scope.
7. If you rebuilt the code locally but not the containers, restart the stack so the frontend and API
   use the updated code.

### HTTP 400 when switching GitHub authentication modes

Check these first:

1. `appInstallation` requires both `gitHubAppId` and `gitHubAppInstallationId`.
2. Switching from PAT to GitHub App requires a GitHub App private key in `secret`.
3. Switching from GitHub App back to `personalAccessToken` requires a PAT in `secret`.
4. GitHub App mode is valid only for GitHub provider connections.

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
6. For Azure DevOps Server, the connection host and the scope URL both use HTTPS and are still reachable from the ProPR runtime.
7. For Linux, WSL, or container deployments, the Azure DevOps Server certificate is trusted inside the runtime trust store.

### Azure DevOps Server verify or reviewer resolution fails with a secure-connection error

If you see `Basic authentication requires a secure connection to the server.`:

1. Check that the Azure DevOps Server connection uses `https://`, not `http://`.
2. Check that the configured provider scope also uses `https://`.
3. If the connection was first created against an old HTTP or different-host endpoint, re-save the connection and confirm the scope now points at the current HTTPS host.

### Azure DevOps Server verify or reviewer resolution fails with an SSL or certificate error

If you see a trust or certificate-validation error:

1. Confirm the server certificate subject alternative names include the host or IP address that ProPR is calling.
2. Trust the issuing CA or server certificate inside the ProPR runtime environment.
3. Re-test the target URL from the same runtime with `curl https://<ado-server-host>/` without `-k`.

### Azure DevOps Server Windows authentication still fails on Linux or WSL

If PAT works but `windowsUserAccount` still fails:

1. Confirm the saved `userName` uses the server-accepted Windows login format, preferably `DOMAIN\user`.
2. Restart ProPR after any runtime update so managed NTLM configuration is active.
3. Retry verification.
4. If the runtime still behaves as unauthenticated, install the Linux NTLM support package and restart ProPR:

```bash
sudo apt update
sudo apt install -y gss-ntlmssp
```

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

### GitHub App provider connection

```json
{
  "providerFamily": "github",
  "hostBaseUrl": "https://github.com",
  "authenticationKind": "appInstallation",
  "gitHubAppId": 123456,
  "gitHubAppInstallationId": 789012,
  "displayName": "GitHub App",
  "secret": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----",
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
| `displayName` | Friendly profile label | Shown in the frontend profile list |
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

- `proRvPrefilter`
- `reviewLowEffort`
- `reviewMediumEffort`
- `reviewHighEffort`

If one of those optional override bindings is missing or disabled, ProPR falls back to `reviewDefault`
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
