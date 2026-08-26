# Control what a review costs

Reduce what a review spends without losing the findings you care about. The steps below are ordered by
money saved per unit of effort. The first two cost you no coverage; the later ones trade coverage or
convenience for spend.

What your provider bills you and what ProPR reports you spent are two different figures. ProPR's figure is
only as good as the prices you entered, so start with pricing.

## 1. Choose a provider family that caches

Prompt caching saves the most money, and the connection's provider family decides whether you get it.
Choose the family when you create the connection. Which families cache, and what a gateway gives you in
return, is in [prompt caching](../ai/index.md#prompt-caching).

If you reach a cacheable model through a gateway today, add a second connection on its native family and
repoint the logical model at it. No client review configuration changes, because nothing in the review loop
names a provider - see [how model selection works](../concepts/models.md). Keep the gateway connection
active alongside the new one until you have compared two reviews.

## 2. Make the prices right before you tune anything

Confirm your negotiated rates are entered, and that no model in use is reporting no price at all. Every
decision below is judged on these numbers. See
[pricing overrides](../ai/models-and-catalog.md#pricing-overrides), and for a model with no price,
[the per-edition remedies](../ai/models-and-catalog.md#a-model-is-missing-its-context-window-or-price).

## 3. Spend reasoning effort where it changes the answer

Effort is a property of the logical-model name, so you can spend it unevenly across purposes - see
[reasoning effort](../ai/purposes.md#reasoning-effort).

- The name mapped to Review default applies to every file of every review, so it is the expensive one.
- Triage and Verification are the cheap half of the job. Point them at a smaller model and an ordinary file
  costs less without touching the hard ones.
- Per-tier names go further - a small model on Low effort, the expensive one only on High effort.

All of them are mapped on one screen - see [purposes](../ai/purposes.md).

Baseline reasoning effort is a separate per-client setting whose default leaves cost unchanged. Leave it
there unless you have measured a reason not to - see
[what you can tune](../concepts/reviews.md#what-you-can-tune).

## 4. Count your passes

Read the client's pass list and its multi-pass union switch, and justify each entry - see
[review passes](../concepts/reviews.md#review-passes).

Remove a shadow pass once the evaluation it was added for is over. Evidence-backed verification works the
other way: it spends extra model calls to recover correct findings, so check whether the client has it
on - see [what you can tune](../concepts/reviews.md#what-you-can-tune).

## 5. Review less

- **One review per pull request.** The default. ProPR reviews a pull request at the first revision it
  sees, and later pushes wait to be asked for. This is the largest saving on a repository where people
  push often. Turning on "review every pushed update" for a client pays for every push - see
  [what you can tune](../concepts/reviews.md#what-you-can-tune).
- **Exclusions.** Generated, vendored and mechanical files are the cheapest tokens to save, because ProPR
  never sends them. The patterns live in the repository, not in ProPR - see
  [configuring ProPR from your repository](../concepts/repository-configuration.md).
- **Incremental re-reviews.** When a review does run again, it carries forward what the previous one
  already covered. This is the default and needs no configuration - see
  [what happens during a review](../concepts/reviews.md#what-happens-during-a-review).
- **Iteration budgets.** They bound how long one file's review may keep gathering context before it must
  conclude - see [review loop budgets](../operate/configuration.md#review-loop-budgets).

The minimum severity to post and disabling SCM comment posting save no money. Both act after every model
call has been made and paid for. They control publication - see
[run reviews without posting them](review-without-posting.md).

## 6. Cap what a client can spend

Budget caps stop spend, they do not reduce it. Read
[what you can tune](../concepts/reviews.md#what-you-can-tune) before you set one, because the scopes do not
behave alike. Setting a cap needs a commercial license. Once set, a cap is enforced in every edition - see
[editions](../reference/editions.md).

If you run ProCursor, its indexing and querying spend embedding tokens reported separately from review
usage. Read [what it costs](../concepts/how-it-works.md#what-it-costs) before widening a source's root path
or adding tracked branches.

## Confirm it worked

1. Run two reviews of comparable size, before and after the change, and compare the per-client token and
   spend reporting.
2. Open the job protocol on each. It lists every pass and every model call, so you can find a pass you
   forgot to remove, or an effort setting applying more widely than you thought - see
   [review diagnostics](../reference/api.md#review-diagnostics).
3. If a model still shows no price, go back to step 2 above: the figures are wrong, not the spend.

For a symptom this page does not cover, [troubleshooting](../operate/troubleshooting.md) routes it.
