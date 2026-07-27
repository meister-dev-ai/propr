# GitHub

Everything needed to connect ProPR to GitHub Cloud or GitHub Enterprise: what to enter for each
authentication mode, where to get it, and how to register the webhook. The fields themselves are
described once in [common provider connection fields](index.md#common-provider-connection-fields).

GitHub supports both `personalAccessToken` and `appInstallation`. PAT-backed and App-backed
connections can coexist as separate GitHub host connections.

The GitHub family has to be [enabled](index.md#enabling-a-provider-family) before a connection can be
created.

## Personal access token

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | `https://github.com` or your GitHub Enterprise base URL | GitHub Cloud uses `https://github.com`; GitHub Enterprise uses the root host URL |
| `authenticationKind` | `personalAccessToken` | Select PAT mode for GitHub |
| `secret` | GitHub personal access token | GitHub -> Settings -> Developer settings -> Personal access tokens |
| `oAuthTenantId`, `oAuthClientId`, `gitHubAppId`, `gitHubAppInstallationId` | leave empty | Not used in this mode |

The token must be able to authenticate the `/user` endpoint and access the repositories and pull
request data ProPR needs for review and publication.

## GitHub App installation

Use `appInstallation` when you want ProPR to operate through an installed GitHub App instead of a
user PAT.

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | `https://github.com` or your GitHub Enterprise base URL | GitHub Cloud uses `https://github.com`; GitHub Enterprise uses the root host URL |
| `authenticationKind` | `appInstallation` | Select GitHub App mode |
| `gitHubAppId` | Numeric GitHub App ID | GitHub -> Settings -> Developer settings -> GitHub Apps -> your app -> App ID |
| `gitHubAppInstallationId` | Numeric installation ID | GitHub App installation URL, GitHub App settings, or installation API metadata |
| `secret` | GitHub App private key PEM | GitHub -> Settings -> Developer settings -> GitHub Apps -> your app -> Generate private key |
| `oAuthTenantId`, `oAuthClientId` | leave empty | Not used in this mode |

Important GitHub App notes:

1. ProPR stores the private key encrypted at rest but never returns it from the API.
2. ProPR does not persist installation access tokens; it mints them on demand and reuses them only
   in a bounded in-memory cache until shortly before expiry.
3. Discovery, reviewer lookup, review fetch, and review publication run through the repositories and
   collaborators visible to the configured installation.

## Provider scope

Every connection needs at least one enabled scope before it counts as ready, GitHub included. On GitHub
the four fields are stored as you enter them - only Azure DevOps interprets `scopeType` - so what matters
is that they name the account your repositories live under, on the same host as the connection.

| ProPR field | Value | Example |
|---|---|---|
| `scopeType` | `organization` | `organization` |
| `externalScopeId` | The organization or user login that owns the repositories | `my-org` |
| `scopePath` | That account's URL | `https://github.com/my-org` |
| `displayName` | Any label you want in the UI | `My Org` |

For GitHub Enterprise Server, use your own host in `scopePath` - `https://github.example.com/my-org`.

## Webhook registration

Create the configuration in ProPR first; see
[create the webhook configuration](webhooks.md#create-the-webhook-configuration).

Register the listener in the repository or organization webhook settings, pasting the generated secret
into the **Secret** field. GitHub signs the body and sends `X-Hub-Signature-256`.

Event mapping:

| ProPR event | GitHub event |
|---|---|
| PR Created | `pull_request`, action `opened` |
| PR Updated | `pull_request`, any other action |
| PR Commented | not supported |

GitHub deliveries are only accepted for pull-request hooks; a comment hook is refused with `400`.
Enabling **PR Commented** on a GitHub configuration therefore has no effect.

### Testing the listener

The GitHub invocation of the [synthetic-event helpers](webhooks.md#testing-with-synthetic-events):

```bash
# -O is the owner, -S the real head branch
bash scripts/send-github-webhook.sh \
  -u "https://propr.example.com/webhooks/v1/providers/github/<pathKey>" \
  -s "<generated-secret>" -r "propr" -O "acme" -S "feature/providers" -i 24
```

## Troubleshooting

A `400` when saving the connection is usually one of the App-versus-PAT field rules in
[HTTP 400 when saving a connection](index.md#http-400-when-saving-a-connection). For anything else -
reviewer identity, deliveries, reviews - start at [troubleshooting](../operate/troubleshooting.md).
