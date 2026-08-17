# SCM platforms

ProPR reviews pull requests on four source-control platforms. This page is the shared part: which hosts
and authentication modes are supported, how a provider family is switched on, and what every provider
connection, scope and reviewer identity is made of. Each platform then has its own page with the values
to enter, where to obtain them, its webhook registration and its troubleshooting.

| Page | Covers |
|---|---|
| [Azure DevOps](azure-devops.md) | Azure DevOps Services and self-hosted Azure DevOps Server |
| [GitHub](github.md) | GitHub Cloud and GitHub Enterprise, by token or App installation |
| [GitLab](gitlab.md) | GitLab.com and self-managed GitLab |
| [Forgejo](forgejo.md) | Any Forgejo host |
| [Webhooks](webhooks.md) | The webhook configuration every platform registers against |

Use these pages when configuring providers in the frontend, or when automating the same setup through
[the API](../reference/api.md).

## Tenant sign-in providers vs SCM provider connections

This section covers SCM provider connections: the credentials ProPR uses for repository discovery,
crawling, and review publication.

Tenant sign-in providers are something else - they only decide how people log in to ProPR. A tenant
can configure `EntraId`, `Google`, or `GitHub` as an identity provider over `Oidc` or `Oauth2`; see
[sign-in and sessions](../reference/security.md#sign-in-and-sessions). Configuring one does not give
ProPR any access to a repository.

## Support matrix

| Provider | Host Base URL | Authentication kind | Extra required fields | Secret field expects |
|---|---|---|---|---|
| [Azure DevOps](azure-devops.md) | `https://dev.azure.com` or `https://{org}.visualstudio.com` | `oauthClientCredentials` | `oAuthTenantId`, `oAuthClientId` | Azure app registration client secret value |
| [Azure DevOps](azure-devops.md) | self-hosted Azure DevOps Server host, for example `https://ado-server.example.com/tfs` | `personalAccessToken` | none | Azure DevOps Server PAT |
| [Azure DevOps](azure-devops.md) | self-hosted Azure DevOps Server host, for example `https://ado-server.example.com/tfs` | `windowsUserAccount` | `userName` | Windows account password |
| [GitHub](github.md) | `https://github.com` or your GitHub Enterprise base URL | `personalAccessToken` | none | GitHub PAT |
| [GitHub](github.md) | `https://github.com` or your GitHub Enterprise base URL | `appInstallation` | `gitHubAppId`, `gitHubAppInstallationId` | GitHub App private key PEM |
| [GitLab](gitlab.md) | `https://gitlab.com` or your self-managed base URL | `personalAccessToken` | none | GitLab PAT |
| [Forgejo](forgejo.md) | your Forgejo base URL, for example `https://codeberg.org` | `personalAccessToken` | none | Forgejo access token |

Any combination not in the table is refused when you save the connection, with an error naming the
modes that provider does accept - so you find out at save time rather than at the first connection
attempt. `appInstallation` in particular is GitHub-only.

### Mention answering

[Answering an @-mention](../concepts/how-it-works.md#asking-propr-a-question) needs two things from a
provider: a way to find the open pull requests to read, and a way to answer where the question was asked.
All four providers support both, so a mention configuration can be created for any of them. A provider
supporting only one is refused when the configuration is saved, with a message naming the missing half.

Where the answer is posted depends on the provider:

| Provider | A question on a line of code | A question in the pull request conversation |
|---|---|---|
| Azure DevOps | replied to in the thread | replied to in the thread |
| GitHub | replied to in the review thread | a new comment quoting the question |
| GitLab | replied to in the discussion | replied to in the discussion |
| Forgejo | a review carrying the answer, quoting the question | the same |

A quoting answer is used where the provider has no thread to reply into. It is the form Forgejo's and
GitHub's own **quote reply** buttons produce, and blockquotes nest, so a follow-up that quotes an answer
keeps the sequence readable.

A mention inside a blockquote is treated as a repetition, not a question, so an answer that quotes the
original mention does not trigger another answer. Quote an earlier message and
mention the reviewer outside the quote and it is a new question, whoever wrote either comment - including
an installation where the reviewer identity is an account people also post from.

## Enabling a provider family

Only **Azure DevOps** and **GitLab** are enabled by default installation-wide. **GitHub** and
**Forgejo** stay disabled until a platform administrator enables them under **Administration → SCM
Providers**. While a family is disabled, creating a provider connection or a webhook configuration for
it is refused with `The selected provider family is currently disabled by system administration.`, and
inbound webhook deliveries for it are rejected.

## Readiness labels

Once a connection verifies and has at least one enabled scope, ProPR labels it **Workflow Complete**
or **Onboarding Ready**. Only Azure DevOps Services and GitHub Cloud reach **Workflow Complete**; every
other host variant - self-hosted Azure DevOps Server, GitHub Enterprise, GitLab, Forgejo - stays
**Onboarding Ready**. The label describes how much operational coverage that host variant has, not
whether reviews work: discovery, review, and comment publication run either way.

## Common provider connection fields

Every provider connection uses the same set of fields; which ones apply depends on the authentication
kind.

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
| `secret` | Protected credential material | See [secrets at rest](../reference/security.md#secrets-at-rest) |
| `isActive` | Whether this connection is operational | Review, discovery, and webhook flows use active connections only |
| `storeThreads` | Archive this connection's pull-request comment threads | Opt-in; see data retention below |
| `storeDiffs` | Archive the per-file diffs of each reviewed increment | Opt-in; see data retention below |
| `retentionDays` | How long archived data is kept, `1`–`3650` | See data retention below |

`hostBaseUrl` must be an HTTPS URL. Plain HTTP is accepted only for loopback, `localhost`, or
private-network addresses, and never for Azure DevOps Server.

The three retention fields appear on the connection form as **Data retention**. Their defaults, what
each one archives, and what the purge sweep does and does not delete are in
[what ProPR stores](../reference/security.md#what-propr-stores).

## Provider scope fields

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

## Reviewer identity fields

Reviewer identity is configured separately from the provider connection, and belongs to exactly one
connection. Resolve it only after that connection and at least one enabled scope verify successfully.

| Field | Meaning |
|---|---|
| `externalUserId` | Provider-native reviewer identifier |
| `login` | Normalized login or unique-name field |
| `displayName` | Human-readable identity name |
| `isBot` | Whether the identity represents a bot or service account |

The easiest way to obtain these values is the reviewer-identity resolve action in the frontend: search
for the display name of the user or service account ProPR should act as, then save the returned
identity. For Azure DevOps, the stored `externalUserId` is the VSS identity GUID.

## Troubleshooting

Problems specific to one platform are on that platform's page. These two are shared.

### HTTP 400 when saving a connection

| Rule | Applies to |
|---|---|
| Hosted Azure DevOps Services accepts only `oauthClientCredentials`, and `hostBaseUrl` stays `https://dev.azure.com` | Azure DevOps |
| Self-hosted Azure DevOps Server accepts only `personalAccessToken` or `windowsUserAccount`, over HTTPS | Azure DevOps |
| `oAuthTenantId` and `oAuthClientId` are both required for `oauthClientCredentials`, and rejected on an Azure DevOps Server `windowsUserAccount` connection | Azure DevOps |
| `userName` is required for `windowsUserAccount` and must be empty for every other authentication kind | Azure DevOps |
| Switching between Azure DevOps authentication modes requires a replacement `secret` for the new mode | Azure DevOps |
| `gitHubAppId` and `gitHubAppInstallationId` are both required for `appInstallation`, and rejected on any other connection or authentication kind | GitHub |
| Switching between PAT and App mode means `secret` must carry the other credential type - a PAT one way, a private key PEM the other | GitHub |
| `retentionDays` must be between `1` and `3650` when set | All |

### Connection verifies but reviewer identity resolution fails

Check these in order:

1. The connection is active.
2. The connection uses a supported authentication kind for that provider.
3. At least one provider scope exists and is enabled.
4. The target identity exists in the scope you are searching.
5. The resolved identity was actually saved, not just looked up.
6. For Azure DevOps Server, the connection host and the scope URL both use HTTPS, are still reachable
   from the ProPR runtime, and the certificate is trusted there.

For a symptom that is not about connecting to a host, start at
[troubleshooting](../operate/troubleshooting.md).
