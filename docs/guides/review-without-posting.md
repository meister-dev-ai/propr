# Run reviews without posting them

Let ProPR review real pull requests while nothing reaches them, so you can judge the output before your
team sees it.

A review with commenting disabled still runs and still costs tokens; only publication is skipped, and
everything it produced is stored and inspectable - see
[why a finding did not get posted](../concepts/reviews.md#why-a-finding-did-not-get-posted). That makes this
the safe way to trial ProPR, a new model, or a prompt override on live pull requests instead of on
contrived ones.

## 1. Turn posting off

On the client, **System → Post review comments to SCM**.

The switch is per client, so turn it off on every client you are trialling.

While it is off, no comment is posted and no thread is resolved, so auto-resolve severities never fire.
The minimum severity to post still applies to what would have been published and, as always, removes
nothing from the review record.

## 2. Run one deliberately

Do not wait for a review to happen. Trigger one - over the API, or by opening a pull request if a webhook
is already registered. Both routes are in [first review](../quickstart.md#first-review).

## 3. Read the results

| What you want to know | Where to read it |
|---|---|
| Whether it completed, and how far it got | **Reviews** in the top navigation, or **Review History** inside the client |
| What the model actually did, pass by pass | **Protocol ↗** on the review row, or [review diagnostics](../reference/api.md#review-diagnostics) |
| Which findings would have been published, kept to the summary, or dropped, and why | The gate records a decision per finding - see [why a finding did not get posted](../concepts/reviews.md#why-a-finding-did-not-get-posted) |
| What the trial cost | Token reporting per client, and what to change if it is higher than you expected - see [control what a review costs](control-cost.md) |

Judge quality from the gate decisions rather than from the finding count. A review that would have
published nothing is a normal outcome, not a failure.

## 4. Narrow the trial once you do want ProPR posting

A shadow pass is the same idea at finer grain: one pass that runs and is recorded but can never reach your
pull request, while the client publishes normally. Use it to try one model or one lens on live pull
requests. It costs full tokens - see [review passes](../concepts/reviews.md#review-passes).

If a particular pull request should not be reviewed at all, block it, and stop a job that is already
running separately - see [blocking and dismissing](../reference/api.md#blocking-and-dismissing).

## 5. Turn posting back on

Flip the switch back when you are satisfied. Nothing from the trial is published retroactively:
publication happens at the end of each review, so the next review of a pull request is the first one that
posts.

## Confirm it worked

- The review row reaches **Completed** and carries a result summary.
- The protocol shows the passes, the model calls, and a gate decision for every finding.
- The pull request itself has no ProPR summary comment and no inline comments.

If comments did appear, the client you triggered against is not the one you changed the switch on. For any
other symptom, [troubleshooting](../operate/troubleshooting.md) routes it.
