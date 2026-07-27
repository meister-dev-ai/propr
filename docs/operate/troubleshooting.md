# Troubleshooting

Find your symptom, go to the page that fixes it. This page holds no diagnosis of its own — every entry
below is owned somewhere else, and that page has the detail.

## The stack itself

| Symptom | Likely cause | Where it is fixed |
|---|---|---|
| A container exits immediately at startup | A required value is missing, or a setting is outside its accepted range | [required values](configuration.md#required-values) |
| The API never becomes healthy and ProCursor crash-loops | ProCursor has no shared key, and the API waits for it | [running without ProCursor](deploy.md#running-without-procursor) |
| The first start fails on a database migration | The `vector` extension is not enabled on the database | [prerequisites](../quickstart.md#what-you-need) |
| A variable you set in `.env` had no effect | The compose file does not forward it, or the value could not be parsed | [does the example stack forward it?](configuration.md#does-the-example-stack-forward-it) |
| `/healthz` reports degraded or unhealthy | One dependency is down; which one is in the response body | [what the health checks mean](observability.md#what-the-health-checks-mean) |

## Signing in and reaching the UI

| Symptom | Likely cause | Where it is fixed |
|---|---|---|
| Nobody can sign in, though the stack started | No JWT signing secret is configured | [required values](configuration.md#required-values) |
| Sign-in is refused after repeated attempts | The per-account lockout or the per-IP limiter has tripped | [sign-in and sessions](../reference/security.md#sign-in-and-sessions) |
| Sessions end sooner than expected | The idle timeout or the absolute lifetime was reached | [sign-in and sessions](../reference/security.md#sign-in-and-sessions) |
| Browser calls from your own origin are blocked | The origin is neither the public base URL's nor in the allowed list | [deployment topology](deploy.md#deployment-topology) |
| A feature is refused with a license message | The capability needs a commercial license | [editions](../reference/editions.md) |

## Connecting to an SCM host

| Symptom | Likely cause | Where it is fixed |
|---|---|---|
| A provider family is missing from the connection, trigger or webhook forms | The family is not enabled for the installation | [enabling a provider family](../platforms/index.md#enabling-a-provider-family) |
| Saving a connection is refused with HTTP 400 | The field combination is not valid for that family and authentication kind | [HTTP 400 when saving a connection](../platforms/index.md#http-400-when-saving-a-connection) |
| An Azure DevOps Server connection will not verify | Scheme, certificate trust, or an authentication mode that server does not accept | [Azure DevOps](../platforms/azure-devops.md#troubleshooting) |
| The connection verifies but reviewer identity will not resolve | A missing scope, an inactive connection, or an identity that was looked up but never saved | [reviewer identity resolution](../platforms/index.md#connection-verifies-but-reviewer-identity-resolution-fails) |

## Webhooks and triggering

| Symptom | Likely cause | Where it is fixed |
|---|---|---|
| The provider fires but no review row appears | The delivery never arrived, or was rejected or deliberately ignored | [webhook troubleshooting](../platforms/webhooks.md#troubleshooting) |
| A delivery is rejected with 401, 400 or 404 | Secret mismatch, an event that family does not classify, or a pull request outside the configured filters | [webhook troubleshooting](../platforms/webhooks.md#troubleshooting) |
| A delivery is accepted but no review is queued | The pull request's reviewer is not the configured trigger identity | [webhook troubleshooting](../platforms/webhooks.md#troubleshooting) |
| The listener URL names an internal host | The public base URL is unset, so the URL was built from the request host | [deployment topology](deploy.md#deployment-topology) |

## Reviews

Confirming one review end to end, if you have not had a successful one yet, is
[first review](../quickstart.md#first-review).

| Symptom | Likely cause | Where it is fixed |
|---|---|---|
| The row stays pending while another review runs | Only one review at a time without a license | [editions](../reference/editions.md) |
| The row stays pending with nothing else running | The review worker is not running | [what the health checks mean](observability.md#what-the-health-checks-mean) |
| The row fails immediately | A purpose the review needs resolves to no model | [purposes](../ai/purposes.md) |
| An AI connection will not verify | Credentials, endpoint reachability, or the wrong family for that endpoint | [verification failures](../ai/credentials.md#verification-failures) |
| No comments on the pull request, or fewer than the summary suggests | Comment posting is off for the client, or the gate and the minimum severity to post filtered them | [why a finding did not get posted](../concepts/reviews.md#why-a-finding-did-not-get-posted) |
| A finding you expected never appeared | Exclusion patterns, the context budget, or the gate — all of which the protocol records | [what happens during a review](../concepts/reviews.md#what-happens-during-a-review) |

## Cost and spend figures

| Symptom | Likely cause | Where it is fixed |
|---|---|---|
| Reviews cost more than expected | No prompt caching on the route you chose, list prices instead of yours, or effort and pass settings | [control cost](../guides/control-cost.md) |
| A model shows no context window or no price | Its id matched no catalog entry | [a model is missing its context window or price](../ai/models-and-catalog.md#a-model-is-missing-its-context-window-or-price) |
| A job is held before it starts | A budget cap was already reached; a held job needs an operator to restart it | [what you can tune](../concepts/reviews.md#what-you-can-tune) |

## When you need to ask for help

Capture the review's protocol, or the webhook delivery row with its failure reason, and the response of
`GET /healthz`. Those three answer most questions without access to your logs, and none of them contains a
secret. See [review diagnostics](../reference/api.md#review-diagnostics).
