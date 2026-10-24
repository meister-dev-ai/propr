# SCM provider boundaries

Application coordinates review intake, synchronization, connection configuration and publication using
normalized provider identifiers and evidence. It does not declare concrete SCM DTOs, select native enum
members, interpret native status strings or supply a native default. Domain retains the persisted provider
identifiers and durable job state. ProCursor canonical source references use the neutral
`MeisterDev.ProPR.ProCursor.Contracts.Sources` namespace.

## HTTP and native contracts

The API owns compatibility routes, request parsing, public provider aliases, response field names and
schema defaults. It projects internal connection coordinates to the existing application and installation
HTTP fields. Guided discovery uses connection-addressed neutral descriptors, scopes, sources and
selection coordinates. The API validates client access and purpose-specific entitlements before
Application resolves the selected connection's existing native capabilities. GUID identity responses
retain their HTTP contracts.

Infrastructure owns SDK calls, credential types, native identity lookup and saved native scope projections.
Native discovery returns neutral project, source, branch and filter options. Application connection
metadata carries typed application and installation identifiers; provider configuration policies prepare
candidate and persisted authentication fields before the coordinator validates or writes them.

Native discovery descriptors own scope labels, optional project stages and supported source kinds.
Native adapters map selections to saved configuration coordinates, including empty repository listings.
Guided saves validate the selected scope and project independently of selected repository filters.
Connection-aware credential resolution uses the selected connection identifier and never falls back
to another connection on the same host. Saved targets remain readable offline. Unrelated patches
preserve repository-filter identifiers and mention claim times. Ambiguous saved targets require an
explicit connection choice before discovery or repository-filter replacement.
Source discovery validates scope ownership before querying repositories. Native project metadata
resolves saved names and case differences without rewriting stored configuration coordinates.
Native project ID matches take precedence over project-name fallback. Retained mention repository
coordinates remain stored values even when live discovery reports different canonical metadata.
Repository-filter replacement validates changed targets while preserving retained legacy targets
with null canonical references. Unresolved discovery permits unrelated edits but blocks target changes.

## Local declarations and runtime capabilities

Review discovery adapters may implement the neutral paged discovery capability in addition to the
bounded legacy list capability. The existing registry returns the same scoped adapter. Each native
request reads at most 100 reviews. Scheduled discovery shares a 15-second deadline and a 10,000 raw
observation bound across selected connection preparation, scoped repository enumeration, native pages
and revision enrichment. Cancellation interrupts the observation even when an SDK task does not complete
cooperatively. An expired deadline does not return partial discovery. The configured connection reviewer
filters native requests; an absent configured reviewer permits all open and draft reviews.
Adapters validate native continuation evidence and rebuild requests from saved source coordinates.
Provider next-link URLs are never used as HTTP destinations. Raw page coverage precedes reviewer
and destination filtering. Overlapping identities are deduplicated, while repeated pages and invalid
continuations produce incomplete discovery.

Selected scheduled repository enumeration requires terminal coverage. A missing later page or a full
installation repository page at the traversal bound refuses the configuration observation. Native
repository names support legacy and mixed filters. Repository-only Azure DevOps grants permit selected
project enumeration; each discovered repository still requires its matching enabled grant. Stub crawl
maintenance uses the stub status service, while saved automation retains its authority validation service.

Native connection verification, repository enumeration and query URI construction do not consistently
retain deployment prefixes. Scheduled discovery uses these native drivers; saved base-path compatibility
does not establish deployment-prefix support for every native operation.

The shared overview refresh bounds traversal to 15 seconds, 10,000 raw observations and 2 MiB of stored
source content. A storage or traversal bound does not establish terminal provider coverage. Source
completion is nullable for older cached payloads without that evidence. Collected counts describe
eligible observations, not provider totals. Visible metadata has separate completion and timestamps.
A complete listing with no eligible rows establishes an empty result. Failed or incomplete observations
do not establish absence. Legacy list capability evaluates at most 10,000 raw observations under the
provider deadline, selects eligible reviews in descending review-number order before its 100-row
compatibility bound, and retains unknown source completion. An oversized legacy result reports capacity
failure using the provider's original observation count.

Scheduled preparation runs between a short initial admission check and a final admission lease.
Current target identity, lifecycle, revision and selected authority are checked under the final lease
before queue admission. Automation changes confirm their saved target snapshot under the same lease;
the API returns that operation snapshot even when a later mutation advances the saved revision.

Native modules register credential-free source, configuration, identity, review-preparation and ingress
policies through module entry points. Native configuration policies also declare immutable readiness
profiles. Application aggregates these declarations, including the generic unknown-family result.

Local declarations can be available without a database, credentials or an enabled live adapter. Their
availability does not establish runtime support. The runtime registry retains its scoped adapter lifetimes,
capability checks and activation behavior. Composition registers the pure compatibility codec separately
from operational adapters.

## Captured and historical evidence

Review, mention and thread-pass construction receives explicit source context. Captured provider, host,
repository namespace, platform and external review identifier are separate from the saved scope and project
coordinates used by existing persistence and queue operations. Domain validates those saved coordinates
before accepting the captured context. Synchronization applies its later context checks at the existing
orchestration point.

Infrastructure prepares historical context for synchronization requests with no captured review reference.
That projection preserves the saved compatibility outcome; it does not enable a provider capability.
Historical boundary constructors remain available outside Application for existing persistence and native
callers. Scalar pull-request projections retain their existing shape and are published with the job's
separate source context.

The pure compatibility codec interprets retained provider names, webhook values, canonical source fallbacks
and providerless discussion status evidence. Current thread observations carry neutral resolution intent.
Application requests a neutral resolution action, and the native status writer supplies its own mutation
grammar. Existing raw status fields remain opaque evidence.

## Review integrity and accounting

Native review preparation runs before publication policy evaluation and result persistence. Suppressing
SCM comments retains internal review results and diagnostics. Source identity bytes, duplicate keys,
advisory-lock keys, captured scope and incremental carry-forward remain owned by their existing workflows.

Publication adapters render findings for each provider using shared safe Markdown primitives and a finite set
of embedded Handlebars templates prepared once. Clients select Shields.io or inline-code badges, with finding
types last and assessment evidence collapsed. Provider limits preserve finding text, safe markup and attribution.

Summaries order actions by recorded risk and identify the highest-risk confirmed defect first. Formatting preserves
raw findings and publication identities used by persistence, deduplication and discussion memory.

Applicable fixes require an exact source range and a confirmed current revision. Azure DevOps, GitHub and GitLab
receive native suggestions where supported. Forgejo and unsupported or stale cases receive labelled,
syntax-highlighted code blocks. Invalid or identical replacements are omitted without discarding the finding.

Provider identity policies supply observed native account facts. Licensing retains its fixed global
automation-account decision, exclusions and author-count enforcement. Reviewed-code security analysis
and documentation URLs are separate from SCM integration.
