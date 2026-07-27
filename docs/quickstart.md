# Quickstart

Deploy ProPR on one machine, sign in for the first time, configure your first client, and get a review
posted on a real pull request.

This is the evaluation path. The example stack it uses is not a deployment you would keep - for ingress,
published images, worker sizing and persistence, see [deploying ProPR](operate/deploy.md). Terms this
product invented are defined once in the [glossary](glossary.md).

## What you need

| Requirement | Notes |
|---|---|
| A container runtime with Compose support | The example stack is a Docker Compose file |
| PostgreSQL with the `vector` extension | The example stack runs the `pgvector/pgvector:pg17` image, and that is the version ProPR is tested against. No minimum server version is checked at startup. On a managed service, enable the `vector` extension before the first start or the initial migration fails. |
| A second database for ProCursor | The example creates `meisterpropr_procursor` on the same server. It needs the `vector` extension too. Skip it if you deploy without ProCursor. |
| A writable directory for review workspaces | ProPR clones the repositories it reviews onto local disk - see [review workspace](operate/deploy.md#review-workspace) |
| An AI provider account | Any supported family - see [AI providers](ai/index.md) |
| At least one SCM provider account | Azure DevOps, GitHub, GitLab, or Forgejo |

## Quick start

The example stack lives in the ProPR repository, so start by cloning it.

```bash
git clone https://github.com/meister-dev-ai/propr.git
cd propr
```

1. Copy the example environment file and fill in the required values.

```bash
cp example/docker-compose/.env.example example/docker-compose/.env
```

Four values are required and the compose file supplies no defaults: `MEISTER_JWT_SECRET`,
`PROCURSOR_SHARED_KEY`, `MEISTER_BOOTSTRAP_ADMIN_USER` and `MEISTER_BOOTSTRAP_ADMIN_PASSWORD`. What each
one does, what it accepts, and how it fails when it is missing:
[required values](operate/configuration.md#required-values).

2. Start the stack. Compose reads the `.env` file next to `docker-compose.yml` automatically.

```bash
cd example/docker-compose
docker compose up --build
```

3. Open `https://localhost:5443/` and sign in with the bootstrap admin credentials. The bundled proxy
   generates a self-signed certificate on first run, so your browser will warn about it.

Keep the compose default `MEISTER_PUBLIC_BASE_URL=https://localhost:5443/api`; what it is used for is in
[deployment topology](operate/deploy.md#deployment-topology).

To run published release images instead of building from the checkout, see
[running published images](operate/deploy.md#running-published-images).

The compose stack is for evaluation on one machine. For an Azure-hosted variant of the same stack -
Container Apps, Key Vault, VNet integration and Azure Files - see `example/azure/.azure/`. It is one way
of many to host ProPR, not a recommended architecture.

> The example stack also runs Grafana and Loki for log browsing, reachable at
> `https://localhost:5443/grafana/`. Anonymous access is enabled with the admin role, which is fine on a
> laptop and not fine anywhere reachable by others.

Before you put anything else in `.env`, read
[does the example stack forward it?](operate/configuration.md#does-the-example-stack-forward-it).

## Configure your first client

Use this order when setting up a new client.

1. **Create users.** The first admin is seeded from the bootstrap variables. Add any further operator
   users from the frontend.

2. **Enable your provider family.** GitHub and Forgejo are off in a fresh installation, so do this
   before you try to connect anything - see
   [enabling a provider family](platforms/index.md#enabling-a-provider-family).

3. **Create a client.** Each client owns its own SCM provider connections, scopes, reviewer-trigger
   identity, AI connections, ProCursor sources, and crawl or webhook configuration.

4. **Add one or more provider connections.** Open the client, then **SCM Providers**. Go straight to the
   page for your host - each one lists its authentication modes, what to enter, and where to get it:
   [Azure DevOps](platforms/azure-devops.md), [GitHub](platforms/github.md),
   [GitLab](platforms/gitlab.md), [Forgejo](platforms/forgejo.md). The
   [support matrix](platforms/index.md#support-matrix) compares all four if you have not chosen yet.

5. **Add provider scopes.** Every connection needs at least one enabled scope before it counts as
   ready. What to put in it is on your host's page above; the fields themselves are in
   [provider scope fields](platforms/index.md#provider-scope-fields).

6. **Configure the reviewer identity when needed.** It is optional: a trigger and filter for automatic
   pull-request selection, and the identity you @-mention to ask a question. Writes still use the
   provider connection's own identity. The fields are described under
   [reviewer identity fields](platforms/index.md#reviewer-identity-fields).

7. **Configure ProCursor sources.** Use guided discovery to pick repositories or wikis, then create
   sources from the selected scope - the per-source options are in
   [configuring a source](concepts/how-it-works.md#configuring-a-source). Skip this if you run without
   ProCursor.

8. **Configure AI connections.** Open the client, then **AI Providers**. Add and verify the connection
   ([AI connection credentials](ai/credentials.md)), attach the models it should use
   ([models, the catalog and prices](ai/models-and-catalog.md)), define the **Logical models** the client
   refers to by name ([defining a logical model](ai/purposes.md#defining-a-logical-model)), then map the
   purposes ([purposes, effort and protocol](ai/purposes.md#ai-purposes)). Do this
   before you configure automation: a review whose **Review default** resolves to nothing fails instead
   of running.

9. **Configure crawl jobs or webhooks.** Nothing in the management UI starts a review, so the client
   needs at least one of the [three triggers](concepts/how-it-works.md#how-a-review-gets-triggered).
   Choose whether a crawl uses all client sources or only selected ones. Both crawl and webhook
   configurations accept an optional [review temperature](concepts/reviews.md#what-you-can-tune), so
   webhook-triggered reviews can differ from crawl-triggered ones for the same client. For registering
   the webhook on your SCM host, see [webhooks](platforms/webhooks.md). Scheduled crawling needs a
   commercial license; without one, use webhooks or the API - see [editions](reference/editions.md).

10. **Trigger your first review and confirm it worked.** See [First review](#first-review) below.

> Azure DevOps is the guided-discovery provider for projects, branches, crawl filters, and ProCursor
> source selection. On the other hosts you enter the same values directly.

## First review

Setup is not finished until a review has actually run. Do one deliberately rather than waiting for one to
happen.

**Start it.** If you registered a webhook, open a pull request - or push a commit to an existing one - in
a repository the configuration covers. Otherwise submit one pull request over the API: it needs neither a
webhook nor a commercial license. See [Trigger a review](reference/api.md#trigger-a-review).

**Watch it.** Open **Reviews** in the top navigation, or **Review History** inside the client. The run
appears in the list with its status; while it runs, the row shows how many of the changed files are done.
**Protocol ↗** on the row opens the run's [job protocol](glossary.md).

**What success looks like.** The row reaches **Completed** and carries a result summary, and the pull
request itself has ProPR's summary comment plus any inline comments on changed lines. A completed review
with nothing posted is a normal outcome, not a failure: findings are filtered before publication.

**When nothing happens.**

| Symptom | Where to look |
|---|---|
| No review row appears at all | Check that the delivery reached ProPR: **Webhooks →** the configuration **→ Delivery History**, then [webhook troubleshooting](platforms/webhooks.md#troubleshooting) for what its outcome means. |
| The row fails immediately | Open **Protocol ↗**; the row's summary column also shows the error. An unresolvable **Review default** purpose names that purpose in the message - go back to **AI Providers → Purposes**. |
| The row stays pending | Another review is still running and the installation is Community edition, which runs one at a time - see [editions](reference/editions.md). Otherwise check `GET /healthz`: the `worker` check must be healthy - see [what the health checks mean](operate/observability.md#what-the-health-checks-mean). |
| The review completes but the pull request has no comments | Check **System → Post review comments to SCM** on the client, then read the protocol - see [why a finding did not get posted](concepts/reviews.md#why-a-finding-did-not-get-posted). |
| Fewer inline comments than the summary suggests | The gate and the per-client minimum severity to post both hold findings back - see [why a finding did not get posted](concepts/reviews.md#why-a-finding-did-not-get-posted). |

For a symptom that is not in this table, [troubleshooting](operate/troubleshooting.md) routes by symptom
across the whole documentation.

## Where to go next

- [Reviews](concepts/reviews.md) - what happened inside that review, and what you can tune.
- [Deploying ProPR](operate/deploy.md) - turn the evaluation stack into a deployment you keep.
- [Control what a review costs](guides/control-cost.md) - before you point it at every repository.
- [The documentation index](index.md) - the rest of the documentation, by question.
