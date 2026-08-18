# ProPR documentation

ProPR is a self-hosted AI code reviewer for Azure DevOps, GitHub, GitLab and Forgejo. You run it, you
bring your own model provider, and
[your code goes only where you point it](reference/security.md#where-your-code-goes).

Every page below has one job. Each fact lives on exactly one page, so if a page names something without
explaining it, the link next to it goes where it is explained. Words this product invented are defined
once in the [glossary](glossary.md).

## Start here

Read these five in order to get from nothing to a deployment you would keep.

1. [Quickstart](quickstart.md) - clone the example stack, sign in, configure one client, get one review
   posted on a real pull request.
2. [How it works](concepts/how-it-works.md) - what you just deployed, how a review gets triggered, and
   what ProCursor adds.
3. [SCM platforms](platforms/index.md) - the support matrix, then the page for the host you actually
   use, then [webhooks](platforms/webhooks.md) so reviews start by themselves.
4. [AI providers](ai/index.md) - which model providers ProPR can call, then
   [credentials](ai/credentials.md), [models](ai/models-and-catalog.md) and [purposes](ai/purposes.md).
5. [Deploying](operate/deploy.md) - the evaluation stack is not a deployment. This is what to change.

## Common questions

Questions, not symptoms. If something is broken rather than unclear, start at
[troubleshooting](operate/troubleshooting.md), which routes by symptom.

| Question | Page |
|---|---|
| Is my SCM host supported? | [platforms/index.md](platforms/index.md#support-matrix) |
| Can ProPR use the model provider I already pay for? | [ai/index.md](ai/index.md) |
| Which model does the actual reviewing, and how do I change it? | [ai/purposes.md](ai/purposes.md) |
| Why does the review configuration never name a provider? | [concepts/models.md](concepts/models.md) |
| Why was a finding not posted on the pull request? | [concepts/reviews.md](concepts/reviews.md#why-a-finding-did-not-get-posted) |
| What can I change about how strict a review is? | [concepts/reviews.md](concepts/reviews.md) |
| How do I keep ProPR out of generated files, or tell it our conventions? | [concepts/repository-configuration.md](concepts/repository-configuration.md) |
| How do I make reviews cheaper? | [guides/control-cost.md](guides/control-cost.md) |
| Can I run reviews without posting anything? | [guides/review-without-posting.md](guides/review-without-posting.md) |
| Where does our code actually go, and how do I constrain it? | [guides/restrict-where-code-goes.md](guides/restrict-where-code-goes.md) |
| Can I install this with no route to the internet? | [guides/air-gapped.md](guides/air-gapped.md) |
| What does this environment variable do? | [operate/configuration.md](operate/configuration.md) |
| What do I back up, and how do I upgrade? | [operate/upgrades-and-backups.md](operate/upgrades-and-backups.md) |
| Which probe do I point my orchestrator at? | [operate/observability.md](operate/observability.md) |
| How do I automate all of this? | [reference/api.md](reference/api.md) |
| Does this need a commercial license? | [reference/editions.md](reference/editions.md) |
| What does my installation report about itself, and how do I switch it off? | [reference/usage-statistics.md](reference/usage-statistics.md) |
| What does *increment*, *lens* or *logical model* mean? | [glossary.md](glossary.md) |

## Every page

### Getting started

| Page | Reach for it when |
|---|---|
| [quickstart.md](quickstart.md) | You want ProPR running on one machine, with one review posted on a real pull request |

### Concepts

| Page | Reach for it when |
|---|---|
| [concepts/how-it-works.md](concepts/how-it-works.md) | You want the component inventory, the ways a review is triggered, and what ProCursor is for |
| [concepts/reviews.md](concepts/reviews.md) | You want what happens inside one review, and the settings that change what it publishes |
| [concepts/repository-configuration.md](concepts/repository-configuration.md) | You want to exclude files from review, or hand the reviewer conventions that live with the code |
| [concepts/models.md](concepts/models.md) | You want the one idea behind AI purposes and logical models before you configure them |

### Platforms

| Page | Reach for it when |
|---|---|
| [platforms/index.md](platforms/index.md) | You need the support matrix, how to enable a provider family, and the fields every connection shares |
| [platforms/azure-devops.md](platforms/azure-devops.md) | You are connecting Azure DevOps Services or a self-hosted Azure DevOps Server |
| [platforms/github.md](platforms/github.md) | You are connecting GitHub Cloud or Enterprise, by personal access token or App installation |
| [platforms/gitlab.md](platforms/gitlab.md) | You are connecting GitLab.com or a self-managed GitLab |
| [platforms/forgejo.md](platforms/forgejo.md) | You are connecting a Forgejo host |
| [platforms/webhooks.md](platforms/webhooks.md) | You want your SCM host to start reviews, or a delivery arrived and produced no review |

### AI providers and models

| Page | Reach for it when |
|---|---|
| [ai/index.md](ai/index.md) | You are choosing a provider family, or deciding between a native family and a gateway |
| [ai/credentials.md](ai/credentials.md) | You are creating one AI connection and need to know exactly what goes in the credential field |
| [ai/models-and-catalog.md](ai/models-and-catalog.md) | You are attaching models, or a model has no context window or the wrong price |
| [ai/purposes.md](ai/purposes.md) | You are mapping purposes to logical models, or setting reasoning effort and protocol mode |
| [ai/compliance.md](ai/compliance.md) | You must restrict which AI provider families and endpoint hosts a tenant may reach |

### Operating

| Page | Reach for it when |
|---|---|
| [operate/deploy.md](operate/deploy.md) | You are past evaluation and need ingress, published images, worker sizing and a persistent workspace |
| [operate/configuration.md](operate/configuration.md) | You want a variable's name, default, accepted range, and whether the example stack forwards it |
| [operate/upgrades-and-backups.md](operate/upgrades-and-backups.md) | You are rolling forward to a new release, or deciding what a restorable backup contains |
| [operate/observability.md](operate/observability.md) | You are wiring probes, metrics, traces and logs, or reading what a health check means |
| [operate/troubleshooting.md](operate/troubleshooting.md) | You have a symptom and want the page that diagnoses it |

### Guides

Each guide is an ordered path through settings that already have their own pages, plus the judgement
about which one to reach for first.

| Page | Reach for it when |
|---|---|
| [guides/control-cost.md](guides/control-cost.md) | Reviews work but cost more than you want them to |
| [guides/review-without-posting.md](guides/review-without-posting.md) | You want to trial ProPR on live pull requests without anything reaching them |
| [guides/restrict-where-code-goes.md](guides/restrict-where-code-goes.md) | You have to constrain where code may be sent, and prove it |
| [guides/air-gapped.md](guides/air-gapped.md) | You are installing on an isolated network and need to know what still crosses a boundary |

### Reference

| Page | Reach for it when |
|---|---|
| [reference/api.md](reference/api.md) | You are scripting setup, or triggering reviews from CI |
| [reference/security.md](reference/security.md) | You are reviewing the boundaries: where code goes, what is stored, secrets, sessions, access control |
| [reference/editions.md](reference/editions.md) | Something is refused and you suspect it needs a commercial license |
| [reference/usage-statistics.md](reference/usage-statistics.md) | You are reviewing the daily anonymous snapshot an installation sends, field by field |
| [reference/source-license-map.md](reference/source-license-map.md) | You need the generated list of commercial-only source files |
| [glossary.md](glossary.md) | A page used a term this product invented |

## Outside these docs

- [LICENSE](../LICENSE), [LICENSING.md](../LICENSING.md) and [COMMERCIAL.md](../COMMERCIAL.md) - the
  legal terms; [reference/editions.md](reference/editions.md) describes runtime behaviour only.
- [SECURITY.md](../SECURITY.md) - reporting a vulnerability.
- `openapi.json` at the repository root - the committed API contract.
- `example/docker-compose/` and `example/azure/.azure/` - the two deployment examples that ship.
