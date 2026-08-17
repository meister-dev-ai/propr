# Forgejo

Everything needed to connect ProPR to a Forgejo host: what to enter, where to get it, and how to
register the webhook. The fields themselves are described once in
[common provider connection fields](index.md#common-provider-connection-fields).

Forgejo supports only `personalAccessToken`.

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | Your Forgejo host base URL | Example: `https://codeberg.org` |
| `authenticationKind` | `personalAccessToken` | The only mode Forgejo accepts |
| `secret` | Forgejo access token | Forgejo -> user settings -> applications or access-token page |
| `oAuthTenantId`, `oAuthClientId` | leave empty | Not used |

The token must be able to authenticate the `/api/v1/user` endpoint and access the repositories and
pull requests ProPR needs.

The Forgejo family has to be [enabled](index.md#enabling-a-provider-family) before a connection can be
created.

## Provider scope

Every connection needs at least one enabled scope before it counts as ready, Forgejo included. The four
fields are stored as you enter them - only Azure DevOps interprets `scopeType` - so what matters is that
they name the owner your repositories live under, on the same host as the connection.

| ProPR field | Value | Example |
|---|---|---|
| `scopeType` | `organization` | `organization` |
| `externalScopeId` | The organization or user that owns the repositories | `my-org` |
| `scopePath` | That owner's URL on your instance | `https://forgejo.example.com/my-org` |
| `displayName` | Any label you want in the UI | `My Org` |

## Mention answering

On Forgejo, ProPR answers an @-mention with a new comment on the pull request that opens with a markdown
blockquote of the comment it answers. This is the same form Forgejo's own **quote reply** button produces,
and blockquotes nest, so a follow-up question that quotes the answer keeps the sequence readable.

Answers to questions asked on a line of code are posted the same way. Forgejo's API has no review thread to
reply into: a review comment carries a file path, a position and the review it was submitted under, but no
identifier for the conversation it belongs to. The blockquote identifies the comment being answered.

The answer is submitted as a review with a body and no inline comments, the same API route used to publish
findings, so it needs no permission beyond the repository write access reviewing already requires. The
issue-comment route is not used: Forgejo scopes tokens by unit, so it would require write access to the
repository's issues, which a repository with the issues unit disabled cannot grant.

## Webhook registration

Create the configuration in ProPR first; see
[create the webhook configuration](webhooks.md#create-the-webhook-configuration).

Register the listener under **Repository → Settings → Webhooks** with the Forgejo or Gitea webhook
type, pasting the generated secret into the **Secret** field. The host signs the body and sends
`X-Gitea-Signature`.

Event mapping:

| ProPR event | Forgejo or Gitea event |
|---|---|
| PR Created | `pull_request`, action `opened` |
| PR Updated | `pull_request`, any other action |
| PR Commented | pull request comment events |

### Testing the listener

The Forgejo invocation of the [synthetic-event helpers](webhooks.md#testing-with-synthetic-events):

```bash
# -r is the repository id, -O the owner
bash scripts/send-forgejo-webhook.sh \
  -u "https://propr.example.com/webhooks/v1/providers/forgejo/<pathKey>" \
  -s "<generated-secret>" -r "101" -O "acme" -i 24
```

## Troubleshooting

Nothing here is Forgejo-specific. Connection and reviewer-identity problems are in
[SCM platforms](index.md#troubleshooting), and every other symptom routes from
[troubleshooting](../operate/troubleshooting.md).
