# Control what a review costs

Reduce what a review spends without losing the findings you care about. The levers are ordered by money
saved per unit of effort: the first two are configuration changes that cost you no coverage, the later
ones trade coverage or convenience for spend.

Keep two figures apart — what your provider bills you, and what ProPR reports you spent. The second is
only as good as the prices it has, which is why pricing comes before any tuning.

## 1. Choose a provider family that caches

Prompt caching is the lever worth the most money, and the connection's provider family decides whether you
get it — so it is settled when you create the connection rather than tuned afterwards. Which families
cache, and what a gateway buys you in return, is in [prompt caching](../ai/index.md#prompt-caching).

If you reach a cacheable model through a gateway today, add a second connection on its native family and
repoint the logical model at it. No client review configuration changes, because nothing in the review loop
names a provider — see [how model selection works](../concepts/models.md). Keep the gateway connection
active alongside the new one until you have compared two reviews.

## 2. Make the prices right before you tune anything

Confirm your negotiated rates are entered, and that no model in use is reporting no price at all. Do this
first; every decision below is judged on these numbers. See
[pricing overrides](../ai/models-and-catalog.md#pricing-overrides), and for a model with no price,
[the per-edition remedies](../ai/models-and-catalog.md#a-model-is-missing-its-context-window-or-price).

## 3. Spend reasoning effort where it changes the answer

Effort is a property of the logical-model name — see
[reasoning effort](../ai/purposes.md#reasoning-effort) — which is what lets you spend it unevenly. Where it
is worth spending:

- The name mapped to Review default is the expensive one: it applies to every file of every review.
- Triage and Verification are the cheap half of the job. Point them at a smaller model and an ordinary file
  costs less without touching the hard ones.
- Per-tier names go further — a small model on Low effort, the expensive one only on High effort.

All of them are mapped on one screen — see [purposes](../ai/purposes.md).

Baseline reasoning effort is a separate per-client setting whose default leaves cost unchanged. Leave it
there unless you have measured a reason not to — see
[what you can tune](../concepts/reviews.md#what-you-can-tune).

## 4. Count your passes

The review pass list is where a small change multiplies. Read the client's list and its multi-pass union
switch, and justify each entry — see [review passes](../concepts/reviews.md#review-passes).

Two habits follow. A shadow pass earns its cost while you are evaluating a model or a lens and not
afterwards, so remove it when the evaluation is over. Evidence-backed verification trades the other way,
buying back correct findings with extra model calls, so decide it deliberately rather than leaving it as
found — see [what you can tune](../concepts/reviews.md#what-you-can-tune).

## 5. Review less

- **Exclusions.** Generated, vendored and mechanical files are the cheapest tokens to save, because they
  are never sent. The patterns live in the repository rather than in ProPR — see
  [configuring ProPR from your repository](../concepts/repository-configuration.md).
- **Incremental re-reviews.** Already the default, and need no configuration — see
  [what happens during a review](../concepts/reviews.md#what-happens-during-a-review).
- **Iteration budgets.** They bound how long one file's review may keep gathering context before it must
  conclude — see [review loop budgets](../operate/configuration.md#review-loop-budgets).

What does **not** save money: the minimum severity to post, and disabling SCM comment posting. Both act
after every model call has been made and paid for. They are publication controls — see
[run reviews without posting them](review-without-posting.md).

## 6. Cap what a client can spend

Budget caps are the backstop rather than a tuning lever: they stop spend, they do not reduce it. Read
[what you can tune](../concepts/reviews.md#what-you-can-tune) before you set one, because the scopes do not
behave alike. Budgeting needs a commercial license — see [editions](../reference/editions.md).

If you run ProCursor, its indexing and querying spend embedding tokens reported separately from review
usage. Read [what it costs](../concepts/how-it-works.md#what-it-costs) before widening a source's root path
or adding tracked branches.

## Confirm it worked

1. Run two reviews of comparable size, before and after the change, and compare the per-client token and
   spend reporting.
2. Open the job protocol on each. It lists every pass and every model call, which is the fastest way to
   find a pass you forgot to remove or an effort setting applying more widely than you thought — see
   [review diagnostics](../reference/api.md#review-diagnostics).
3. If a model still shows no price, go back to step 2 above: the figures are wrong, not the spend.

For a symptom this page does not cover, [troubleshooting](../operate/troubleshooting.md) routes it.
