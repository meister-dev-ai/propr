# Runtime architecture

The API composes application modules and provider adapters. Application ports carry provider-neutral
review and crawl evidence; Domain owns durable state and evidence enums; Infrastructure owns PostgreSQL
mapping, migrations and SCM adapters. Review execution remains separate from analytics collection and
queries. ProCursor is accessed through its bounded gateway contract.

The Reviewing diagnostics summary reader projects the PR view's paged jobs, token totals, finding counts,
memory contribution samples, thread-pass counts and revision watermarks. PostgreSQL aggregates counts and
token fallbacks without materializing complete review results, protocol events or scan-thread collections.
The detail readers retain their complete execution-trace and review-result contracts.

Domain defines impact, occurrence likelihood, correctness confidence and the versioned risk rubric independently of finding
classification. Reviewing requests assessment evidence in existing generation calls and retains it
through verification, recovery and result transport. Provider adapters render severity and assessment badges above the finding description, with finding types
last and evidence in a closed Assessment details section. Clients select Shields.io images (the default) or
plain inline-code badges. The style is captured once in the publication context and does not alter persisted findings.
Application supplies safe text and assessment table primitives, including a plain Markdown fallback.
Azure DevOps uses a single-line HTML details and table section; the other providers use a Markdown table.
Summary presentation orders findings by assessed risk without changing their persisted order or publication
ordinals. A shared **Fix first** lead before the conclusion identifies the first confirmed defect at the highest
assessed defect risk. The lead requires scored risk and preserves the recorded order among tied defects.
Review verification distinguishes confirmed defects from verified improvements through an explicit judge field;
aggregate investigation confidence cannot reclassify a defect as an improvement.

Domain owns the canonical finding-type taxonomy and structured classification metadata. Application captures
active custom vocabulary once per review through a Code Insights contract; the runner manifest carries the same
snapshot without exposing persistence. Finding producers assign types, code level, qualifier and numeric type
confidence within their existing generation calls. The numeric value describes the classification and is
separate from four-level correctness confidence. Source tags survive filtering, synthesis, verification and
carry-forward; publication adds escaped readable labels without changing the raw claim or identity.

Optional suggested fixes carry an inclusive source range, exact original text and replacement text. Application
binds them only when the immutable revision used to fetch that content matches the queued revision. Publication checks the current provider
head and uses matching native anchors on Azure DevOps and GitHub, or exact relative offsets on GitLab. Publication
renders ordinary code blocks under **Suggested change** for Forgejo and for replacements with unverifiable or
stale source. The shared renderer maps supported source languages to fixed fence identifiers for syntax
highlighting. Fix validation does not suppress findings.
Raw suggestion fences cannot bypass this validation. Fix metadata persists in existing review-result JSON
and offline evaluation exports. Thread-memory reconsideration retains source metadata through stable finding
identities or an unambiguous original anchor; unreliable identities retain the draft result.
Code Insights stores protected reasons and queryable dimensions; its joint projections
apply the same risk scope to aggregates and finding details.

Shared review execution retains bounded model-opportunity proof in existing checkpoints before publication.
Independent human-concern collection qualifies model recall and F1 from compatible original source and
revision evidence. Analytics failures preserve review results; queries reuse retained proof without
provider or model calls. [Code Insights](architecture/code-insights.md) defines coverage and recovery.

| Ownership | Reference |
|---|---|
| Review intake, execution, publication and diagnostics | [Reviews](concepts/reviews.md), [runner architecture](reference/runner-architecture.md) |
| Repository targets, guarded review admission and shared PostgreSQL discovery observations | [Repository targets](reference/api.md#repository-review-targets), [pull request discovery](reference/api.md#pull-request-discovery) |
| Code Insights evidence, projections, score queries and reports | [Code Insights](architecture/code-insights.md) |
| SCM contracts, local declarations and native capability ownership | [SCM provider boundaries](architecture/scm-provider-boundaries.md) |
| Credentials and tenant isolation | [Security](reference/security.md) |
| Durable deployment state and backups | [Upgrades and backups](operate/upgrades-and-backups.md) |

## Installation authentication

The Identity and Access module owns the installation sign-in policy. Its options disable password sign-in
and tenant identity providers across authentication and account administration endpoints. Authentication
options expose the allowed methods to the frontend. Startup validation requires a configured, licensed
platform administrator provider when password sign-in is disabled. Administrators create personal access tokens
through the authenticated token management endpoints, including after SSO sign-in.

Provisioning emits a `user.provisioned` application audit event with the acting user ID, the internal account ID
and the configured issuer. The provisioning audit interface accepts these identifiers without the email-derived
username or the external identity subject. Identity and Access keeps those identity values in its account and
provisioning records.

The user token repository applies the configured lifetime to creation, listing and authentication. It identifies
provisioned accounts from durable provisioning records and exempts stored platform administrator roles. Existing
tokens use their creation time and the current policy without rewriting stored expiry dates. The token policy
endpoint exposes the current user's limit to the account settings page.

## SCM configuration

The API owns request bounds, egress checks, caller authorization and licensing guards.
Application services coordinate connection validation, verification and review-source selection
through existing provider capabilities. Native authentication requirements belong to each
provider's `IScmConnectionConfigurationPolicy`; policy inputs contain metadata and credential
availability, not credential values. Application and installation identifiers are typed neutral values,
and native policies prepare candidate and persisted usernames. The Clients module registers the required configuration
coordinator, and controllers receive it through its Application interface.

The API owns management identifiers, public defaults, aliases and HTTP compatibility shapes.
Infrastructure owns native enum mappings, recorded-provider decoding and historical source projections.
Application consumes resolved identifiers and normalized evidence through neutral contracts. These mappings do not require credentials, provider activation or
runtime adapter resolution. Saved targets remain readable when their provider is unavailable.
Guided discovery addresses an active client-owned connection and requires an explicit configuration
purpose. Native capabilities supply hierarchy descriptors and persistent selection coordinates.
Native calls use credentials for the selected connection. Identity lookup preserves configured scope
selection and credential ownership.

Native modules register credential-free source, configuration, identity, review-preparation and
webhook-ingress declarations independently of database and live-adapter registration. Local policy
availability does not establish runtime capability or provider activation. Persistence and author
counting consume only the required pure declarations, without constructing live SCM services.
Native account facts are inputs to the fixed licensing classifier; they do not configure exclusions
or change author-count enforcement.

Native source policies prepare captured namespace, repository and symbol-query coordinates.
Unknown saved identities use explicit compatibility projections without enabling provider operations.
Infrastructure's credential-free compatibility codec preserves saved native identifiers and providerless
status evidence. Explicit job construction receives captured source coordinates; historical missing-context
construction is prepared at the native boundary.
The API composes native browser-origin declarations with configured origins; its existing CORS and
redirect checks retain control of browser access.

## Review preparation and bounded context

Native review preparation supplies stored revision aliases, comparison context, anchors and thread
intent before publication policy evaluation and result persistence. Suppressed SCM publication still
records the prepared internal review result and diagnostics. Live revision refresh retains its
existing operation and cancellation boundaries.

Evidence verification uses one reasoning-inclusive output-budget retry within the existing claim
deadline and repository-lookup limits. Withheld candidates remain separate from publishable comments.
Fresh correctness confidence uses independent bounded witness, consequence and counterevidence fields with exact
quotes covering complete substantive source lines. Fragments and punctuation-only quotes do not establish grounding.
Character limits retain partial boundary lines in the judge's context and exclude them from confidence grounding.
`ConfidenceSource` identifies its producer separately from the impact and likelihood `Source`.
Generated Certain, a confirmed Error or Warning, a confirmation after the judge's last repository lookup and a claim
that states its trigger is not established request an additional contradiction check without exposing the producer's
confidence to the judge. The check resolves `reviewLowEffort`, has the judge's repository lookups and its own claim
deadline. A confirmed Error or Warning whose published confidence would be Medium or below, or Unassessed, goes to a
second check that resolves `reviewHighEffort` the same way. A confirmation after the last lookup and a claim with an
open trigger keep that condition as an assumption, which bounds confidence at Medium. The producer's proposed confidence
bounds the verified level. Unavailable or inconclusive confidence checks preserve independently valid findings at
supported bounded confidence; a check verdict other than a confirmation applies the disposition that verdict has for the
judge. Source-grounded counterevidence with a negative verdict produces Drop and removes the claim from verified
summaries. A primary negative verdict without grounded counterevidence remains SummaryOnly.

Structured synthesis challenges identify retained findings by stable ID. Reconsideration runs once per challenged
ID through the existing verification executor, carrying the patch and original producer's repository-access context.
The existing protocol records separate verifier runtime identities, evidence and confidence transitions. Exact
replacement equality after line-ending normalization suppresses optional native and fallback patches independently
of finding validity; rejected patch metadata remains in review-result JSON.
Local recorded-response regressions cover a correct allocation loop, missing and incorrect updates, confidence
resolution and synthesis reconsideration. They verify pipeline behavior without measuring live model accuracy or recall.
Per-file checkpoints and completed job JSON persist their comment and reason independently of analytics.
Synthesis and runner transport retain that evidence for authorized Code Insights collection as
`PolicyWithheld`. Collection does not supply publication receipts or settled outcome credit for
those candidates.

Automatic judge effort requires both model and transport support. The provider catalog supplies exact
model effort facts; the driver port confirms the selected transport. Resolved runtimes and optional
runner bindings carry that capability. Unknown or incompatible bindings use provider defaults.

Client model selection saves verified purpose bindings in one transaction. Explicit logical-purpose
mappings take precedence. A conflicting selection returns 409 and preserves the saved bindings.
See [client model selection](ai/credentials.md#client-model-selection).

The local ProCursor SCM broker validates client existence before delegating credential and SDK work
through the existing bounded broker port. Azure DevOps owns the native backend. Review-context tools
consume neutral prepared symbol coordinates; absent coordinates remain unavailable and do not enable
additional provider families. Runner symbol requests use its existing proxy boundary.

## Crawl synchronization and locks

Discussion synchronization shares one authoritative provider thread snapshot between status, memory,
archive and Code Insights consumers. PostgreSQL stores scoped thread fingerprints and independent
consumer acknowledgments. Durable notification generations and fenced target leases survive retries
and restarts. Historical feedback uses the same target lock and invalidates current acknowledgments.
Full reconciliation defaults to 300 seconds independently of review discovery.

Active review admission observes discussion status, ownership and the current revision while deferring
memory generation to the durable discussion worker. Successful memory progress is checkpointed per
thread under the current fence without advancing observation freshness. Memory dependencies include
discussion content, resolution grounding and AI configuration; unrelated PR revisions preserve current
memory acknowledgments. Closed observations process all consumers before final evidence is acknowledged.

A shared fair scheduler bounds global, provider-host and client-provider-host concurrency at 8/4/1 by
default. Separate operation scopes preserve EF context isolation. Target locks cover consumer writes;
existing review admission locks continue to protect queue mutation.
[Discussion synchronization](operate/discussion-synchronization.md) documents cadence and diagnostics.


Manual pull request overview refreshes consume the optional paged capability on the existing review
discovery adapters. One fenced source refresh owns the entire native page sequence and its shared
60-second cooldown. Native pagination headers and continuations remain inside provider adapters.
The rich page contract distinguishes complete upstream listing coverage from collected row counts and
visible-row metadata completeness. Deadline and storage limits produce incomplete coverage.

Scheduled discovery traverses native pages for Azure DevOps, GitHub, GitLab and Forgejo through the
registered adapters. Saved canonical filters retain repository identity; legacy names resolve uniquely
within the saved project, owner or group. Complete observations precede synchronization. A page failure,
throttle, cancellation or traversal bound prevents absent-job maintenance for that configuration.

The crawler requires the shared pull-request synchronizer. It uses that service for discovered
pull requests and lifecycle checks on jobs absent from discovery. Scheduled intake selects one owned,
active, verified connection with matching grants and captures its non-secret configuration and configured
reviewer identity. Active alternative selectors cause refusal. A short target admission lease validates
saved target identity and authority before provider preparation. Preparation runs without the lease,
so target mutations can complete during provider reads. A final lease covers current target and authority
checks and queue admission. Connection edits do not share that lease. Queued credential resolution may observe later
rotation. Known native closed or merged status permits absent-job maintenance; missing or unknown status
does not establish closure.
The synchronizer owns duplicate-safe job persistence, revision decisions and thread maintenance.

Automatic-review activity is independent of manual-review lifecycle. The revision-conditional automation
endpoint changes only activity and revision. Enabling requires current eligible connection authority;
pausing an owned saved target does not require credentials or current grants. Creation, restore and
manual lifecycle enable leave automatic reviews paused. Successful automation responses contain the
saved target snapshot confirmed under the operation's admission lease. Later mutations do not change
that response's activity or revision.

PostgreSQL advisory locks use shared helpers with explicit bigint, `hashtext` and two-integer key
protocols. Callers own key derivation, transactions, isolation and retries. Transaction locks retain
the caller's transaction lifetime; owned session leases retain separate acquisition and disposal.
