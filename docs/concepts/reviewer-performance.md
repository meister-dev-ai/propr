# Reviewer performance

Reviewer Performance is a commercial Code Insights surface for tenant administrators. Collection must be
enabled for each client. The default view shows one cumulative F1 range over the selected dates; correctness,
acceptance, misses and coverage views are also available.

## Reading the range

Each measurement applies the supported scoring premises to the same retained counts of publications,
current outcomes and observed human misses:

| Evidence | Supported interpretations |
|---|---|
| Dismissed, WontFix and ByDesign, independently | Positive contribution, negative contribution, or excluded from the denominator |
| Settled substantive human concerns in scope | Acted-on concerns only, or all such settled concerns |
| Confirmed duplicate publications | Retain the outcome contribution, replace it with a negative contribution, or exclude it |

Positive contributions increase TP, negative contributions increase FP, and eligible human misses increase
FN. Precision is `TP / (TP + FP)`, observed recall is `TP / (TP + FN)`, and F1 is
`2TP / (2TP + FP + FN)`. Counts are summed before ratios are calculated. An empty denominator produces an
unavailable value. Excluded observations do not contribute to that denominator. These measurements do not
estimate TN or a complete population of defects.

Precision uses all supported published outcomes in the selected scope. Recall and F1 use covered source
pull requests. F1 applies TP, FP and FN from that same covered population; its precision contribution can
differ from the overall precision shown by the precision metric. Coverage exclusions remove a pull
request's findings and misses from the covered calculation without removing its supported findings from
overall precision.

Unknown outcomes, unresolved publications and failed outcome judgments are excluded before any duplicate
interpretation is applied. Duplicate counts are subsets of the outcome population; they replace a
contribution once. Suppressed repeats receive no publication credit. Generated, withheld, failed,
identity-unknown and suspected-duplicate observations remain available in evidence coverage.

Withheld observations include findings excluded by evidence verification, with their original model
and withholding reason. A failed or output-limited judge does not establish a finding's correctness;
these observations contribute to coverage without precision or publication credit.

The chart connects minimum-to-maximum bands, with an inner first-to-third-quartile band and a median line.
Each scoring premise receives equal display weight.
The range describes interpretation choices; it is not a confidence interval or variation between pull
requests. Cumulative series retain earlier evidence through the selected window. Per-period measurements
have gaps for missing periods and inactive series. Direct labels identify each series, metric median,
complete range and last available measurement date.
An open circle marks an inactive period or empty denominator; an X marks unavailable evidence or unsupported
attribution. A measured zero remains a score. Missing measurements are not interpolated.

## Filtering and comparing

Dates, population filters and saved-report controls share the **Filters** pane beside a single chart.
Comparison uses the available page width, with compact filters above both charts. **Hide filters** closes
the pane. On narrow screens, **Filters** opens the pane and **Apply filters** returns to the chart.
Metric, aggregation, bucket and series controls sit above the charts.

Filter by client, repository, model, finding type, kind, impact, likelihood, confidence and risk severity. Kind uses the Missing, Incorrect and
Extraneous qualifier. Selections are combined as a union within one dimension and an intersection across
dimensions. A finding with several selected types contributes once to the combined view. Its membership can
appear in several separate type series, so those series are not disjoint populations.

Impact, likelihood, confidence and risk severity also support separate series and matrix axes. Their values come
from the retained [finding risk assessment](reviews.md#finding-risk). Unassessed is a selectable
population, including historical findings. A risk restriction applies alongside the other filters to
the same observations. It does not weight the scoring formula or replace finding classification.

Client filters include authorized clients without measurements in the selected dates and are available in
all performance views. **All** includes every value in a dimension; **None** selects an empty population.

Dates are UTC source-cohort dates. Cumulative mode includes counts from the selected window start through
the inspected bucket. Per-period mode uses independent day, week or month buckets. Partial edge buckets are
clipped to the requested dates.

Hover a chart point to inspect its series, exact measurement window, stored precision, observed recall
and F1 ranges, and evidence limitations. Tap or click a point to select it for comparison and dimension
exploration. Focus the chart to inspect with the keyboard: Left and Right move between dates, Up and Down
move between series, and Home and End move to the first and last periods. Enter or Space selects the point;
Escape closes its details, and Page Up/Down scrolls long details. Unavailable periods remain inspectable.

The coverage panel shows the inspected point, or the window end before a point is selected. It remains
attached to that date even when an earlier point has the latest available score. **View exclusions** opens
the captured pull requests for that point, with client, repository, source identity, evidence reason,
collection attempts and recovery state. The list pages locally. Source links are available when retained
provider coordinates establish an unambiguous browser URL.

Activate **Compare side by side** to add an independently filtered view. Desktop charts appear beside each
other when both fit at a readable size; narrower screens stack them. Both views are read under the same
database snapshot, use the same 0–100% metric scale and share a calendar or elapsed-period axis. Calendar
positions preserve actual date distances; elapsed positions compare period ordinals. Comparison
shows B-minus-A differences for the same interpretation premise. Toggling comparison preserves the second
view's filters. Choose View A or View B under **Edit view**. **Apply filters** submits both views.

**Explore dimensions** opens a matrix at the current point. Choose two dimensions to inspect score ranges
for their intersections. Model is an available axis for qualified precision, observed recall and F1. Selecting a cell filters the
timeline and returns keyboard focus to it at the cell's measurement date. Filters being edited
apply after **Apply filters**. Changing aggregation, buckets or series keeps those edits pending; exploration
and snapshot capture use the scope displayed by the chart. Automatic measurement changes retain the scope
already submitted by **Apply filters**, including while its request is pending. Later unapplied filter edits
remain pending. If a replacement query fails, all displayed comparison views, measurements and saved-report
labels remain attached to the retained results.

## Evidence limits

Risk-filtered or risk-grouped recall and F1 require independent human concerns assessed with compatible
impact, likelihood and confidence evidence. That evidence is unavailable, so these measurements show an explicit
limitation. Selecting a risk matrix axis qualifies each cell without changing an otherwise unfiltered
timeline. Overall publication precision remains available when its selected outcomes support it.
Whole-pull-request correctness scores cannot be split by risk; risk-filtered correctness is unavailable.
The Missed findings list requires unrestricted risk filters because human concerns have no independent
impact, likelihood or confidence assessment. Collection coverage includes all risk levels.

Observed recall measures retained human review concerns; it does not measure every defect in the code.
Complete successful thread enumeration is required even when no human misses were found. Open human
threads with successful judgments are provisional and excluded from both settled-miss premises. Missing
enumeration, failed human judgments or incompatible source attribution exclude the affected source pull
request from recall and F1. A refresh failure alone does not exclude a source with sufficient retained
evidence. Failed human judgments display unavailable decisions in the Misses panel.

Coverage counts distinct source pull requests in the selected client, repository and date population.
Repeated dates or overlapping type memberships count a source once. The default advisory minimums are
80% covered sources and one covered pull request. They use unrounded counts and are configurable through
the [Code Insights settings](../operate/configuration.md#code-insights). Scores remain available below either
advisory when at least one covered source supplies a nonempty metric denominator. Complete enumeration
with zero misses is eligible. A period without scored outcomes or settled misses has no F1 value.

Exclusion reasons remain visible alongside scores. The covered subset can differ systematically
from excluded sources; its coverage percentage does not establish that it represents all pull requests.

Model-specific precision uses retained publication and outcome evidence. Model recall and F1 additionally
require positively retained execution opportunities and independent human concerns. Model grouping, filters
and matrix cells qualify each source before type and kind filtering. A source with unknown applicable
execution, original concern position or candidate matching is excluded from that model's covered outcomes
and misses. Models can have different covered workloads; compare their coverage as well as their scores.

A completed finding-producing pass records its actual remote and logical model, source connection,
original revision, assigned file/ranges, candidate set and first durable server acceptance. Valid structured
empty output is a certified zero. Failed, skipped, cancelled or invalid generation cannot certify zero.
A certified zero with a verified applicable missed concern has recall and F1 of 0%, even if precision has
an empty denominator. A certified zero without scored outcomes or concerns is inactive. Both remain in the
model facets. Context-only, planning and verifier calls do not establish detection opportunities.

Independent human concerns include substantive human confirmation in reviewer-owned threads. Bot-only
findings are not human ground truth. Original native revision, file, side, range and issue-raise time
establish compatibility; the observation head or later confirmation time cannot replace them. Later
reviews cannot remove an earlier qualified miss. Repeated concerns are combined only with positive
candidate or concern equivalence proof. Open concerns with successful judgments remain informational.

Selecting multiple models in Combined evaluates their applicable opportunity union. If B detected an issue
that A missed, A receives one miss, B receives none and selected A+B receives none. If neither detected it,
selected A+B receives one miss. Coverage and misses are never summed across singleton model views. A model
with positively proven non-assignment to the issue's file/range receives no miss for it. Complete A remains
usable when unrelated B fails; selecting an applicable incomplete B excludes that source from A+B.
Withheld candidates can establish detection but receive no published true-positive credit.
An earlier failed or invalid applicable pass remains unknown even when another pass completed. An
unresolved configured pass cannot prove that a model was absent from a file. Editing a concern removes
its prior equivalence until current evidence proves the relationship again.

Historical files, configuration and finding absence do not establish execution completeness or zero misses.
Missing original proof remains unreconstructable. Current PR-wide generation retains positive candidates,
but its assigned line-level scope is not certified, so it cannot establish non-detection for arbitrary
concerns. Cross-revision reuse retains its origin; an unchanged file digest alone cannot certify that all
relevant review context stayed unchanged. Such opportunities remain excluded from model recall/F1.

Finding-type and kind recall/F1 are available only
over sources whose relevant settled misses have compatible recorded classification. Eligibility is
assessed before finding-dimension filters can remove unattributed misses. Missing classifications remain
unknown and exclude the affected source.

Repository identities include client, provider family, host and repository ID when that provenance is
recorded. Missing source identity is displayed as unavailable provenance. Outcome observations require
matching source identity. Recall and F1 require compatible miss attribution across the selected client,
repository and date population, including when model, type or kind filters are applied. Eligible human
concerns without source identity exclude compatible sources because their membership cannot be
established. Sources with coverage records remain visible even when they have no findings or scored misses.

## Current evidence and saved reports

Saved reports capture the selected assessment dimensions, observed rubric versions and score limitations.
Opening an older report preserves its captured values; absent risk selections mean an unrestricted
scope and absent assessment evidence remains Unassessed.

Current results reuse stored counts. Collection and background catch-up refresh those counts as evidence
changes. Each view shows its latest evidence update time. **Report captured** shows the response capture
time separately. Background collection refreshes retained pull requests, including those already closed.
Consistent finding outcomes and human concerns keep their stored judgments. Missing provider data keeps
affected sources excluded until collection completes. Recall and F1 remain available when the covered
population contains at least one source and metric denominators exist.
Missing update times are shown as unavailable. Pending updates warn that current scores may change as
updates are processed. Filtering uses retained evidence without repeating review analysis.

Current outcomes include reopened threads. Unfiltered Combined retains its global overlap exclusions for
reviewer findings and AI-authored discussions. Model measurements use independently verified concerns
before that overlap exclusion. Failed human judgments are displayed as unavailable decisions.

Transient provider reads, timeouts, classification and retention failures are eligible for automatic retry
after the configured cooldown. Missing usable connections, source coordinates or provider identity,
unsupported historical collection and missing observer dependencies require operator repair. Their
automatic retries are paused. Generic provider failures do not establish that a remote repository was
deleted and remain retryable.

After repairing a source, use **Requeue collection** in its live exclusion details. The request queues a
subsequent bounded background sweep. Active observations and disabled client collection prevent requeue
with an actionable message. Queued status persists when the dialog is reopened or current evidence is
loaded again. The dialog shows the request acknowledgement time or the queued sweep eligibility time.
An active observation shows its start and lease expiry. **Refresh current evidence** updates progress and
scores while preserving the inspected measurement and pending filter edits. Failed observations retain
their reason and permit another request after repair. Exclusions disappear when retained evidence becomes
usable. Queuing preserves the displayed evidence until a fresh read establishes its state.

For incomplete retained concern classification or candidate matching, **Retry retained classification**
uses only the captured provider scope and connection. It performs bounded local classification without
provider collection or review execution. The dialog records the captured retry cooldown; refresh after an
attempt to inspect its result. Successful facts and original source cutoffs remain fixed. Missing historical
execution or original-anchor proof offers no retry action. A collection retry cannot reconstruct it.
Classification input or concern-equivalence limits also offer no retry for unchanged evidence. Their
captured exclusion reason identifies the exceeded bound.

**Save snapshot** captures the server's complete query response, including filters, display identities,
counts, premises, calculated values, evidence reasons, source freshness and scoring version. Opening it
reads that stored payload without applying current evidence or recalculating its version. Metric selection
uses its stored precision, recall and F1 values. Pending-update status describes the time of capture; saved
scores remain unchanged. Coverage policy, exclusions, source links and recovery metadata describe the
capture. Calculation version `reviewer-performance-v2` treats the coverage minimums as advisories.
Saved v1 reports retain their stored values and availability. Reports without coverage fields identify
their absence; coverage is not reconstructed from current data. Collection commands are unavailable in saved
reports. Saved scope and measurement controls are fixed.
Dimension exploration is available only for intersections included in the capture, and inspection aligns to their
captured measurement bucket. A failed capture can be retried with the same request ID, name and scope.
After confirmed snapshot deletion, a failed return to current evidence retains the cached snapshot and marks
it deleted. Repeat deletion is disabled; returning to current or selecting another report remains available.

A report requires access to every included client. The report list includes only unexpired reports for which
the operator has that access. Reports retain their own expiration independently of
ordinary source retention. Collection opt-out stops new collection and retains saved reports for their
authorized operators until expiry or deletion. Explicit collection-data deletion or deletion of any included
client removes the entire report, including reports that also contain other clients. Administrators can
delete an authorized report directly. See [configuration](../operate/configuration.md#code-insights) for retention
and catch-up settings and [the API reference](../reference/api.md#reviewer-performance) for query bounds.
