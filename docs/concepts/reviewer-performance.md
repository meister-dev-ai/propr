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

Unknown outcomes, unresolved publications and failed outcome judgements are excluded before any duplicate
interpretation is applied. Duplicate counts are subsets of the outcome population; they replace a
contribution once. Suppressed repeats receive no publication credit. Generated, withheld, failed,
identity-unknown and suspected-duplicate observations remain available in evidence coverage.

The chart connects minimum-to-maximum bands, with an inner first-to-third-quartile band and a median line.
Each scoring premise receives equal display weight.
The range describes interpretation choices; it is not a confidence interval or variation between pull
requests. Cumulative series retain earlier evidence through the selected window. Per-period measurements
have gaps for missing periods and inactive series. Direct labels identify each series, metric median,
complete range and last available measurement date.

## Filtering and comparing

Dates, population filters and saved-report controls share the **Filters** pane beside a single chart.
Comparison uses the available page width, with compact filters above both charts. **Hide filters** closes
the pane. On narrow screens, **Filters** opens the pane and **Apply filters** returns to the chart.
Metric, aggregation, bucket and series controls sit above the charts.

Filter by client, repository, model, finding type and kind. Kind uses the Missing, Incorrect and
Extraneous qualifier. Selections are combined as a union within one dimension and an intersection across
dimensions. A finding with several selected types contributes once to the combined view. Its membership can
appear in several separate type series, so those series are not disjoint populations.

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

Activate **Compare side by side** to add an independently filtered view. Desktop charts appear beside each
other when both fit at a readable size; narrower screens stack them. Both views are read under the same
database snapshot, use the same 0–100% metric scale and share a calendar or elapsed-period axis. Calendar
positions preserve actual date distances; elapsed positions compare period ordinals. Comparison
shows B-minus-A differences for the same interpretation premise. Toggling comparison preserves the second
view's filters. Choose View A or View B under **Edit view**. **Apply filters** submits both views.

**Explore dimensions** opens a matrix at the current point. Choose two dimensions to inspect score ranges
for their intersections. Model is an available axis for precision exploration. Selecting a cell filters the
timeline and returns keyboard focus to it at the cell's measurement date. Filters being edited
apply after **Apply filters**. Changing aggregation, buckets or series keeps those edits pending; exploration
and snapshot capture use the scope displayed by the chart. Automatic measurement changes retain the scope
already submitted by **Apply filters**, including while its request is pending. Later unapplied filter edits
remain pending. If a replacement query fails, all displayed comparison views, measurements and saved-report
labels remain attached to the retained results.

## Evidence limits

Observed recall measures retained human review concerns; it does not measure every defect in the code.
Complete successful thread enumeration is required even when no human misses were found. Open human
threads are provisional and excluded from both settled-miss premises. Failed collection or judgement and
missing compatible attribution produce explicit reasons and unavailable recall/F1. Chart point details
show the metric ranges and their evidence limitations. Failed human judgements display unavailable
decisions in the Misses panel.

Model-specific precision is supported where publication and outcome evidence exists. All model-filtered,
model-grouped and model-axis recall/F1 measurements remain unavailable: file review exposures are retained,
but compatible membership connecting human misses to models is not recorded. A latest file model or
observation order does not establish that membership. Finding-type and kind recall/F1 are available only
when the relevant settled misses have compatible recorded classification. Missing classifications remain
unknown.

Repository identities include client, provider family, host and repository ID when that provenance is
recorded. Missing source identity is displayed as unavailable provenance. Outcome observations require
matching source identity. Recall and F1 require compatible miss attribution across the selected client,
repository and date population, including when model, type or kind filters are applied. Eligible human
concerns without source identity make recall and F1 unavailable because their membership cannot be
established.

## Current evidence and saved reports

Current results reuse stored counts. Collection and background catch-up refresh those counts as evidence
changes. Each view shows its latest evidence update time. **Report captured** shows the response capture
time separately.
Missing update times are shown as unavailable. Pending updates warn that current scores may change as
updates are processed. Filtering uses retained evidence without repeating review analysis.

Current outcomes include reopened threads. A human concern that matches a reviewer finding or contains an
AI-authored comment is excluded from miss counts; its recorded judgements remain visible. Failed collection
makes compatible recall unavailable. Failed human judgements are displayed as unavailable decisions.

**Save snapshot** captures the server's complete query response, including filters, display identities,
counts, premises, calculated values, evidence reasons, source freshness and scoring version. Opening it
reads that stored payload without applying current evidence or recalculating its version. Metric selection
uses its stored precision, recall and F1 values. Pending-update status describes the time of capture; saved
scores remain unchanged. Saved scope and measurement controls are fixed. Dimension exploration is
available only for intersections included in the capture, and inspection aligns to their
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
