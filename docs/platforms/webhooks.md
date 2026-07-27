# Webhooks

How to make your SCM host tell ProPR about pull requests, and how to check that it worked. This page
covers the ProPR half - what a webhook configuration is, how to create one, and how to read its
deliveries. The provider-side registration differs per platform and lives with that platform:
[Azure DevOps](azure-devops.md#webhook-registration), [GitHub](github.md#webhook-registration),
[GitLab](gitlab.md#webhook-registration), [Forgejo](forgejo.md#webhook-registration). The same
configurations can be created and inspected through the
[API](../reference/api.md#webhook-configurations).

## Overview

A working webhook is two halves:

- A **webhook configuration** in ProPR. It sets the scope (host, project, repositories), which events
  it accepts, and produces a listener URL plus a one-time secret.
- A **webhook or service hook on the provider side** that calls that listener URL when events occur.

Every listener has the same shape: `/webhooks/v1/providers/{provider}/{pathKey}`, where `{provider}`
is `ado`, `github`, `gitlab`, or `forgejo`.

A delivery is verified, recorded, and queued. The review itself runs asynchronously, so the provider
receives its acknowledgement long before any comment is posted.

## Before you start

- Your ProPR instance is reachable from your SCM host over the network. Use HTTPS.
- You are a platform administrator, or a client administrator on the target client.
- The provider family is [enabled installation-wide](index.md#enabling-a-provider-family) - a disabled
  one refuses this configuration and its deliveries.
- The client already has an active provider connection for that host and an enabled scope - see
  [SCM platforms](index.md).

## Create the webhook configuration

1. Sign in, open **Clients**, choose your client, then **Webhooks** in the left sidebar.
2. Click **New Webhook**.
3. Pick the **Automation Provider**. Azure DevOps uses guided organization and project discovery; the
   other families ask for the host base URL and the owning group or namespace directly.
4. Choose the project (Azure DevOps), or type the owner, group, or namespace (everything else).
5. Add repository filters. Each filter can also be narrowed to target branch patterns. How best to
   identify a repository can depend on the platform - see its page.
6. Select the events this listener accepts: **PR Created**, **PR Updated**, **PR Commented**.
7. Optionally set a **Review Temperature** for reviews this webhook starts, overriding the model
   default - see [what you can tune](../concepts/reviews.md#what-you-can-tune). Leave it blank for
   default model behaviour.
8. Save. ProPR shows the **listener URL** and the **generated secret**. Copy the secret now: it is
   displayed once and cannot be re-issued. Rotating it means deleting the configuration and creating
   a new one.

If the listener URL names an internal backend host, the API's public base URL is not set - see
[deployment topology](../operate/deploy.md#deployment-topology). Set it, then create the
configuration again so the generated URL is externally reachable.

## Register the webhook on your provider

Point the provider at the listener URL and give it the generated secret. Where to register it and how
the secret travels are on the platform page - [Azure DevOps](azure-devops.md#webhook-registration),
[GitHub](github.md#webhook-registration), [GitLab](gitlab.md#webhook-registration),
[Forgejo](forgejo.md#webhook-registration) - along with the provider-side events that correspond to
**PR Created**, **PR Updated** and **PR Commented** on that host.

Three rules hold everywhere:

- A delivery that does not carry the secret the way that provider requires is rejected with `401`.
- Send JSON where the provider offers a content-type choice.
- Enable only the provider-side events you want. Anything else shows up as a failed delivery.

## Testing with synthetic events

If you have a source checkout, `scripts/` contains helpers that post provider-shaped deliveries at a
listener URL: `send-ado-webhook`, `send-github-webhook`, `send-gitlab-webhook` and
`send-forgejo-webhook`, each as a `.sh` and a `.ps1` taking the same flags. Run one with `-h` for its
full option list; each platform page shows the invocation for its own host. Without a checkout, use
your provider's own webhook test button instead.

## Troubleshooting

Start at the delivery log: **Clients → your client → Webhooks →** select the configuration **→
Delivery History**. Each row shows the event, the outcome, the HTTP status, the pull request, and
either an action summary or a failure reason.

Nothing in the log at all means the delivery never arrived: check that the listener URL is reachable
from the SCM host - right host, right port, not firewalled - and read the provider's own delivery log
for the connection error. If the URL names your backend host instead of the proxy, fix the public base
URL and recreate the configuration as described above.

Otherwise the row's outcome tells you where it stopped:

| Outcome | What it means |
|---|---|
| `rejected`, HTTP 401 | The secret did not verify. Check that the provider-side secret matches the one ProPR generated and travels the way that provider's page requires. A lost secret cannot be re-issued - recreate the configuration. |
| `rejected`, HTTP 400 | The provider sent an event ProPR does not classify for that family - a comment hook is the usual case, and each platform page says which of its hooks are accepted - or the payload was malformed or carried no resolvable repository or pull request. |
| `rejected`, HTTP 404 | The pull request is outside the configuration's repository filters or their target-branch patterns. Widen the filters, or add the repository. A `404` also means the path key is unknown, belongs to a different provider family, or that family is disabled. |
| `ignored`, HTTP 200 | Deliberate: the event type is not one you enabled on this configuration. Enable it, or ignore the row. |
| `accepted`, but no review queued | Read the action summaries. A pull request whose reviewer is not the configured trigger identity is accepted and then skipped, and the summary says so. |
| `failed` | The delivery passed validation but the work behind it did not. Check [the health endpoint](../operate/observability.md#what-the-health-checks-mean), then the failure reason on the row. |

A failure reason of "A project name is required…" is an Azure DevOps identifier mismatch; see
[repository and project identifiers](azure-devops.md#repository-and-project-identifiers).

When asking for help, capture a delivery row with its failure reason and action summaries, plus the
provider-side test response.

If the delivery itself looks fine and the problem is further along, start at
[troubleshooting](../operate/troubleshooting.md).
