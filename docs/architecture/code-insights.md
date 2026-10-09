# Code Insights architecture

Code Insights collects review evidence and provides reviewer-performance measurements and saved reports.
Collection requires the commercial Code Insights capability and an enabled client collection policy.

For chart controls and score interpretation, see [Reviewer performance](../concepts/reviewer-performance.md).

## Collection

Reviewing and Crawling supply observations through provider-neutral contracts. Code Insights owns analytics
storage and calculations; PostgreSQL stores the evidence. Collection failures do not interrupt reviews,
comment publication or thread memory.

```mermaid
flowchart LR
    Observations[Review and crawl observations] --> Evidence[Stored evidence]
    Evidence --> Measurements[Performance measurements]
    Measurements -->|Save snapshot| Reports[Saved reports]
```

## Evidence and identity

| Evidence | Retained information |
|---|---|
| Reviewer findings | ProPR records the producing model, finding type and kind, publication, outcome and duplicate evidence. |
| Human concerns | ProPR records the discussion, substantive and acted-on judgements, scope, classification and collection coverage. |
| File reviews | ProPR records which model reviewed a file. This does not establish which human concerns that model missed. |

Evidence is associated with its client, provider host or organization, repository and pull request.
Repeated observations of one publication count once; suppressed repeats receive no additional publication
credit. Connection edits do not change a captured source identity.

Missing source identity, classification or judgement remains visible as unknown.

Thread observations update current outcomes, including reopened threads. A human concern that matches a
reviewer finding or contains an AI-authored comment is excluded from miss counts; its recorded judgements
remain available.

Current thread observations receive neutral resolution intent from provider policies. Providerless
stored discussion text uses an explicit compatibility decoder for the historical
service-account identifier, summary prefix and generated-marker grammar. Those exclusions remain
global because the retained text does not establish a provider family or current runtime capability.
Disposition, harvesting and history import obtain normalized intent for saved discussion statuses through
the credential-free compatibility codec. Their account and publication exclusions retain the separate
providerless stored-evidence decoder.

## Measurements

Performance measurements apply scoring premises to stored counts. Counts refresh as evidence is collected,
and background catch-up recovers missed updates. Filtering and range calculations reuse those counts.
Side-by-side views use a consistent set of evidence. Each view shows its latest evidence update time and
pending-update status separately from the response capture time.

Precision, recall and F1 are unavailable when their required evidence or denominator is missing. Incomplete
collection prevents supported recall. Model-specific precision is supported; model-specific recall and F1
lack compatible human-miss attribution. Recall and F1 by type or kind require compatible miss classification.
The API retains raw counts and scoring premises. The user interface presents metric ranges and evidence
limitations.

The [API reference](../reference/api.md#reviewer-performance) defines query limits and request fields.

## Saved reports and deletion

A saved report captures its scope, counts, scoring premises, results and evidence cutoff. Opening the report
uses that captured response without recalculation. Its evidence update time and pending-update status
describe the capture. Every report operation requires access to all included clients.

| Event | Effect on saved reports |
|---|---|
| Evidence or scoring changes | Saved reports retain their captured results. |
| Collection is disabled | Collection stops; authorized saved reports remain available until expiry or deletion. |
| Source evidence expires | Saved reports follow their own retention period. |
| A report expires or is deleted | ProPR removes the complete report. |
| A client or its collection data is deleted | ProPR removes every report containing that client's data, including reports spanning several clients. |

[Configuration](../operate/configuration.md#code-insights) defines collection, background processing and
retention settings. [Security](../reference/security.md) describes tenant access.
