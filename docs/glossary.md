# Glossary

ProPR invents vocabulary, and a few of its words mean something narrower here than they do elsewhere.
Each entry below is only enough to tell one term from its neighbours; the page in the last column is
where it is defined in full and kept up to date.

## Who owns what

| Term | What it means | Covered in |
|---|---|---|
| Tenant | The isolation unit above a client. Anything decided once for a group of clients — memberships, login policy, identity providers, AI compliance, the shared logical-model catalog — is decided here. | [Access control](reference/security.md#access-control) |
| Client | The configuration unit a review belongs to, and the scope most settings in ProPR have. | [Quickstart](quickstart.md#configure-your-first-client) |
| Edition | Community or Commercial. One setting for the whole installation, not something two tenants or two clients can differ on. | [Editions](reference/editions.md) |
| Capability key | The identifier of one licensed capability, such as `parallel-review-execution`. It is how a capability is named when one is licensed or overridden on its own. | [Editions](reference/editions.md) |

## Source control

| Term | What it means | Covered in |
|---|---|---|
| SCM provider connection | One set of stored credentials for one source-control host, belonging to one client. It is what ProPR authenticates with to discover repositories, read pull requests and publish comments. | [Common provider connection fields](platforms/index.md#common-provider-connection-fields) |
| Provider scope | Which part of that host the client may act on — for Azure DevOps, an organization. The *where*, as against the connection's *how*. | [Provider scope fields](platforms/index.md#provider-scope-fields) |
| Reviewer identity | The account ProPR is taken to be on a pull request: what you @-mention, and optionally what decides which pull requests get picked up. Not what ProPR writes with. | [Reviewer identity fields](platforms/index.md#reviewer-identity-fields) |
| Readiness label | The **Workflow Complete** or **Onboarding Ready** badge a verified connection carries. It rates operational coverage for that host variant, not whether reviews work. | [Readiness labels](platforms/index.md#readiness-labels) |
| Guided discovery | The Azure DevOps flow that lists projects, repositories, branches and wikis to pick from instead of asking you to type identifiers. | [Quickstart](quickstart.md#configure-your-first-client) |

## Webhooks

| Term | What it means | Covered in |
|---|---|---|
| Webhook configuration | The ProPR half of a webhook — the provider half being the hook you register on your host. It is what produces the listener URL and its secret. | [Webhooks](platforms/webhooks.md) |
| Listener URL | The URL your provider posts deliveries to, generated when a webhook configuration is saved. | [Webhooks](platforms/webhooks.md#overview) |
| Path key | The trailing segment of a listener URL, which is what tells ProPR whose configuration a delivery belongs to. | [Webhooks](platforms/webhooks.md#overview) |

## Reviews

| Term | What it means | Covered in |
|---|---|---|
| Review increment | One revision of a pull request as ProPR reviewed it. A new push makes a new increment, which is why budget caps and diff archiving are counted per increment rather than per pull request. | [Reviews](concepts/reviews.md) |
| Review pass | One sweep of a review model over the change. Every changed file gets one; a client's pass list can add more. | [Review passes](concepts/reviews.md#review-passes) |
| Lens | The `None`, `Security` or `ProRV` setting on an extra pass: the choice of prompt, and with it the set of files that pass covers. | [Review passes](concepts/reviews.md#review-passes) |
| ProRV | The knowledge lens — the one that consults a catalog of per-language checks bundled with the product instead of working from the diff alone. | [Review passes](concepts/reviews.md#review-passes) |
| Shadow pass | A pass whose findings never leave the protocol, so a candidate model or lens can be trialled on real pull requests with nothing reaching the team. The tokens are billed anyway. | [Review passes](concepts/reviews.md#review-passes) |
| Multi-pass union | The per-client switch without which an extra per-file pass does not fan out across the harder files. | [What you can tune](concepts/reviews.md#what-you-can-tune) |
| Triage | The cheap per-file model call that classifies how complex a changed file is. | [AI purposes](ai/purposes.md#ai-purposes) |
| Complexity tier | Low, Medium or High, assigned to a changed file by triage. Which purpose reviews the file, and which extra passes apply to it, follow from it. | [Reviews](concepts/reviews.md) |
| Thread memory | The record of how a comment thread was settled last time, kept so a point you already rejected is not raised again. | [Reviews](concepts/reviews.md) |
| Publication gate | The deterministic last step before publication, also called the final gate. No finding reaches a pull request without passing it, and it records its reasoning. | [Why a finding did not get posted](concepts/reviews.md#why-a-finding-did-not-get-posted) |
| Summary-only | A gate outcome: the finding is mentioned in the review summary but not posted as an inline comment. | [Why a finding did not get posted](concepts/reviews.md#why-a-finding-did-not-get-posted) |
| Review aggressiveness | Per client, `Calm`, `Balanced` or `Assertive` — how much of what the model produced survives screening. | [What you can tune](concepts/reviews.md#what-you-can-tune) |
| Review temperature | An override of the model default, set per crawl or webhook configuration rather than per client, so two automation paths can behave differently. | [What you can tune](concepts/reviews.md#what-you-can-tune) |
| Job protocol | The stored execution trace of one review, opened with **Protocol ↗** on a review row. | [Reviews](concepts/reviews.md) |

## AI providers and models

| Term | What it means | Covered in |
|---|---|---|
| AI connection | One configured AI endpoint: family, base URL, credential, and the models attached to it. The UI button says **Add Profile** and the API says profile — same object. | [AI connection credentials](ai/credentials.md) |
| AI purpose | A named slot of AI work — review generation, triage, verification, embeddings. The list is closed: you map the slots that exist, you cannot invent one. | [AI purposes](ai/purposes.md#ai-purposes) |
| Purpose binding | A purpose-to-model mapping stored on an AI connection rather than over logical-model names — the second place a purpose looks. | [AI purposes](ai/purposes.md#ai-purposes) |
| Logical model | The indirection between a review configuration and a real model: a name you invent, pointing at one model on one connection, carrying its effort and protocol settings. | [Purposes and logical models](concepts/models.md) |
| Tenant catalog | Logical-model names held on the tenant, which any client in it can use without defining its own. | [Two layers](ai/purposes.md#two-layers) |
| Client override | A logical model one client defines under a name, shadowing the tenant-catalog entry of the same name. | [Two layers](ai/purposes.md#two-layers) |
| Reasoning effort | How hard the model is asked to think — `none`, `low`, `medium` or `high` — set on a logical model and expressed in whatever form each provider takes. | [Reasoning effort](ai/purposes.md#reasoning-effort) |
| Protocol mode | Which wire format ProPR calls a model with, carried by the logical model alongside effort. | [Protocol mode](ai/purposes.md#protocol-mode) |
| Model catalog | What ProPR already knows about public models: what each can do, how much context it has, what it lists at. Bundled with the product, not fetched. | [The model catalog](ai/models-and-catalog.md#the-model-catalog) |
| Hand-defined model | A model whose metadata you type in yourself because no public catalog has it. Once saved it is indistinguishable from a catalog entry. | [Defining a model the catalog does not list](ai/models-and-catalog.md#defining-a-model-the-catalog-does-not-list) |
| Pricing override | Your negotiated rate for a model, replacing the catalog list price for a whole tenant. Spend figures and budgets are only as right as these are. | [Pricing overrides](ai/models-and-catalog.md#pricing-overrides) |

## Code knowledge and disk

| Term | What it means | Covered in |
|---|---|---|
| ProCursor | The optional service that gives a review knowledge of code outside the diff, by indexing repositories and wikis you nominate. | [ProCursor](concepts/how-it-works.md#procursor) |
| Source | One indexed thing: a repository, or an Azure DevOps wiki. Added per client. | [Configuring a source](concepts/how-it-works.md#configuring-a-source) |
| Symbol mode | Whether a source is indexed with code-aware symbol extraction (`Auto`) or as text alone (`Text only`). | [Configuring a source](concepts/how-it-works.md#configuring-a-source) |
| Mini-index overlay | The smaller review-time index ProCursor can build for a tracked branch. | [Configuring a source](concepts/how-it-works.md#configuring-a-source) |
| Review workspace | Local disk on the API host where the repositories under review are cloned. Disposable: replacing it costs clone time, not data. | [Review workspace](operate/deploy.md#review-workspace) |
