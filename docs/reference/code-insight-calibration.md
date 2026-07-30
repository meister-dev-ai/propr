# Calibrating the Reviewer Performance numbers

Every number on the Reviewer Performance surface rests on a model's judgement of what became of a
finding. The surface says so. This page is the procedure that turns that admission into a measurement:
how to draw a sample, how to label it by hand, what agreement the labeller has to reach, and what
happens to each number while it has not.

Nothing here needs a model call. Drawing the sample and labelling it are the work; the model is only
measured against the result.

## Why an uncalibrated number is not a number

Three of the reads are model judgements rather than observations.

| Number | What the model decides |
|---|---|
| Precision, F1 | Whether a finding nobody acted on was wrong or merely unwanted |
| Rejection reasons | Which of five reasons a rejection was for |
| Recall | Whether a human comment was something the reviewer should have caught |

An outcome the provider reports (a thread resolved, code changed at the anchor) is an observation and
needs no calibration. The three above have no ground truth in the data at all, so their accuracy is
unknown until somebody measures it. A precision figure of 0.82 from a labeller that agrees with people
half the time is not a precision figure.

## The sample

Drawn by `scripts/code-insight-calibration-sample.sql`, which is reproducible on purpose: the draw
order is a hash of a seed and the finding id, so the same database, window and seed produce the same
sample for anybody who runs it.

```bash
psql "$DB_CONNECTION_STRING" \
  -v seed=calibration-2026-07 -v from_date=2026-01-01 -v to_date=2026-07-31 -v per_stratum=12 \
  -f scripts/code-insight-calibration-sample.sql
```

**Size.** Twelve findings per stratum. With five outcomes, three concern classes and four severities the
strata that exist in a real window come to roughly twenty, so a round is around 200 to 250 findings.
That is a day of labelling for two people and gives every stratum enough rows for a per-stratum
agreement figure to mean something. Fewer than eight per stratum is not worth drawing: one disagreement
moves the stratum's agreement by more than ten points.

**Stratification.** Outcome, concern class and severity, with the repository carried on every row.
Proportional sampling would fill the sample with the common case and leave four rows of the rare one,
and the rare outcomes are exactly where a labeller is most likely to be wrong.

**A stratum with too few findings.** Take all of it and record the shortfall. Every output row carries
`stratum_size`, so a stratum with three findings is visible in the sample rather than discovered when
the counts do not add up. A stratum under eight rows gets no per-stratum agreement figure, only its
contribution to the overall one, and that has to be stated wherever the round's results are reported.

**What the sample deliberately omits.** The recorded outcome for the individual finding, the rejection
reason, and the model's confidence. A labeller who can see the machine's answer is no longer
independent, and agreement measured against a primed labeller is worthless. The stratum key names the
outcome the row was drawn for, which is unavoidable; nothing else about the machine's judgement is in
the file. Join the answers back on `finding_id` after both labellers have finished.

## The labelling

**Two labellers, independently.** One labeller measures nothing. If two people who both know the
codebase cannot agree on what happened to a finding, the task is not answerable and no model result
against it can be trusted, whichever way it comes out.

**The label set** is exactly what the product records, so a labeller is answering the same question the
model was asked.

- Outcome: addressed, acknowledged, dismissed, judged wrong, left unresolved.
- Rejection reason, for dismissed and judged wrong only: wrong, out of scope, redundant, deliberate
  trade-off, developer preference.
- For a harvested human thread: substantive, acted on, in scope, each as a yes or no.

**The instructions** given to a labeller are the definitions in the product, not a paraphrase. A
labeller reads the thread, the finding, and the diff at the anchor, and answers from those. Two rules
matter more than the rest, because they are where honest labellers diverge:

1. Silence is not rejection. A thread nobody replied to says nothing about whether the finding was
   right. Label it left unresolved.
2. A finding can be correct and unwanted. "Nobody acted on it" is not evidence that it was wrong.

**Disagreements** are resolved by discussion between the two labellers, and the resolved label is the
one the model is measured against. Both original labels are kept: the human agreement figure is
computed from those, and computing it from resolved labels would report perfect agreement every time.
A disagreement neither labeller can resolve is dropped from the round and counted, because a case
people cannot agree on cannot be used to judge a model.

## The agreement thresholds

Two measurements, in order. Both use Cohen's kappa, which corrects for the agreement that chance
alone would produce, because raw agreement on a distribution where four fifths of findings are
addressed looks excellent whatever the labeller does.

**Human agreement first, floor 0.70.** Below that the task is under-specified rather than the model
being bad, and the round stops: rewrite the instructions and draw again with a new seed. The floor is
0.70 rather than higher because the published work on this task reports 0.85 for a two-person coding
of the same question, so materially below that means our instructions are worse than theirs, and a
task humans agree on at 0.6 cannot support a claim about a model at all.

**Model agreement second, floor 0.60 per judgement.** Measured against the resolved human labels, and
separately for each of the three judgements: the outcome, the rejection reason, the miss judgement.
The floor is below the human one on purpose, because the model is being asked to match a resolved
consensus rather than another individual, and 0.60 is the conventional boundary between moderate and
substantial agreement. It is a floor for reporting a number at all, not a target.

A round reports, per judgement: kappa, the raw agreement, the confusion matrix, the number of dropped
cases, and every stratum that fell short of eight rows.

## The gate

Failing the model floor does not invalidate the collection. It changes what may be presented as a
measurement.

| Judgement below its floor | What happens |
|---|---|
| Outcome | Precision, recall and F1 are annotated as uncalibrated wherever they appear, and the rejection-reason distribution is suppressed, because a reason attached to a misjudged outcome is worse than no reason |
| Rejection reason | The reason distribution and its concern-class split are suppressed. Precision and acceptance are unaffected: they do not depend on the reason |
| Miss judgement | Recall and F1 are suppressed. Precision and acceptance stay, because neither uses a miss |

Suppressed means the number is not shown and the surface says why, in the same place it would have
been. It does not mean the underlying records are deleted, and it does not stop collection: a later
round with a better prompt is measured against the same stored evidence.

Acceptance rate is never suppressed by this gate. It is computed from outcomes the provider reported
and code changes that either happened or did not.

## Recording a round

A round is a file under `specs/analysis/`, named `code-insight-calibration-<seed>.md`, holding the
seed, the window, the per-stratum counts, both kappa figures, the confusion matrices, the dropped
cases, and the gate decision that followed. The sample and the labels go beside it as CSV. That is
what makes a later round comparable rather than a fresh opinion.
