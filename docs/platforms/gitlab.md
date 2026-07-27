# GitLab

Everything needed to connect ProPR to GitLab.com or a self-managed GitLab: what to enter, where to get
it, which token scope publication needs, and how to register the webhook. The fields themselves are
described once in [common provider connection fields](index.md#common-provider-connection-fields).

GitLab supports only `personalAccessToken`.

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | `https://gitlab.com` or your GitLab base URL | Use the root GitLab URL |
| `authenticationKind` | `personalAccessToken` | The only mode GitLab accepts |
| `secret` | GitLab personal access token | GitLab -> User Settings -> Access Tokens |
| `oAuthTenantId`, `oAuthClientId` | leave empty | Not used |

The token must be able to authenticate the `/api/v4/user` endpoint and access the projects, merge
requests, discussions, and users ProPR needs. `read_api` is sufficient for verification and read-side
discovery, but review publication posts merge request discussions through the REST API and therefore
requires the broader `api` scope.

## Provider scope

Every connection needs at least one enabled scope before it counts as ready, GitLab included. On GitLab
the four fields are stored as you enter them - only Azure DevOps interprets `scopeType` - so what matters
is that they name the group your projects live under, on the same host as the connection.

| ProPR field | Value | Example |
|---|---|---|
| `scopeType` | `organization` | `organization` |
| `externalScopeId` | The group or namespace path that owns the projects | `my-group` |
| `scopePath` | That group's URL | `https://gitlab.com/my-group` |
| `displayName` | Any label you want in the UI | `My Group` |

For a self-managed GitLab, use your own host in `scopePath`. For a subgroup, use the full path -
`my-group/platform` and `https://gitlab.com/my-group/platform`.

## Webhook registration

Create the configuration in ProPR first; see
[create the webhook configuration](webhooks.md#create-the-webhook-configuration).

Register the listener under **Project → Settings → Webhooks**, pasting the generated secret into the
**Secret token** field. GitLab sends it verbatim as `X-Gitlab-Token`.

Event mapping:

| ProPR event | GitLab event |
|---|---|
| PR Created | Merge request events, action `open` |
| PR Updated | Merge request events, any other action |
| PR Commented | not supported |

GitLab deliveries are only accepted for merge-request hooks; a comment hook is refused with `400`.
Enabling **PR Commented** on a GitLab configuration therefore has no effect.

### Testing the listener

The GitLab invocation of the [synthetic-event helpers](webhooks.md#testing-with-synthetic-events):

```bash
# -p is the project id, -P the full path with namespace
bash scripts/send-gitlab-webhook.sh \
  -u "https://propr.example.com/webhooks/v1/providers/gitlab/<pathKey>" \
  -s "<generated-secret>" -p 101 -P "acme/platform/propr" -i 24
```

## Troubleshooting

If the connection verifies but comments never appear, check the token scope above before anything else.
For anything else - reviewer identity, deliveries, reviews - start at
[troubleshooting](../operate/troubleshooting.md).
