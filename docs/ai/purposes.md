# Purposes, effort and protocol

Every piece of AI work a review does asks for a purpose, and each purpose has to reach a logical model that names
a real model on a real connection. This page is that mapping surface: which purposes exist, how one resolves, what
happens when it does not, and the two properties a logical model carries — reasoning effort and protocol mode.

The distinction between a purpose and a logical model, and why the review loop never names a provider, is on
[how model selection works](../concepts/models.md). Mapping is done on the client's **AI Providers → Purposes**
screen; extra [review passes](../concepts/reviews.md#review-passes) pick a logical model by name in the same way.

## AI purposes

| Purpose (UI label) | API value | Used for |
|---|---|---|
| Review default | `reviewDefault` | Primary review generation, and answering @-mentions |
| Embedding default | `embeddingDefault` | Embeddings for thread memory and ProCursor similarity |
| Triage | `reviewTriage` | Cheap per-file complexity classification |
| Verification | `reviewVerification` | Evidence-gathering verification of a candidate finding |
| Low effort / Medium effort / High effort | `reviewLowEffort`, `reviewMediumEffort`, `reviewHighEffort` | Per-file review at the complexity tier triage assigned; High effort also runs synthesis |
| Memory reconsideration | `memoryReconsideration` | Re-judging an existing comment thread on a re-review |
| ProRV prefilter | `proRvPrefilter` | Not used by the review path — see below |

A purpose resolves in two layers, in this order:

1. **The purpose → logical model map.** Looked up for that one purpose. There is no fall-through inside this
   layer: an unmapped purpose never borrows the logical model mapped to another purpose.
2. **The purpose bindings on the client's active AI connection.** This layer does chain to a cheaper relative —
   Triage → Low effort → Review default; Verification → Triage → Low effort → Review default; and each per-tier
   review purpose → Review default. Review default, Embedding default and Memory reconsideration have no chain;
   each resolves only from its own binding.

Only when neither layer yields a model does behaviour depend on which purpose it was. Most degrade; two fail.

| Purpose | If nothing resolves |
|---|---|
| Review default | The review job fails and nothing is posted. An @-mention reply fails the same way |
| Embedding default | Thread memory is neither stored nor retrieved, comment screening keeps the comment, and ProCursor indexing and search fail |
| Low / Medium / High effort | The file is reviewed on the Review default model instead. For High effort, synthesis runs there too |
| Triage | The deterministic size heuristic classifies the file — no model judges complexity |
| Verification | Verification runs on the reviewing model itself, and the AI duplicate judge keeps both findings |
| Memory reconsideration | Reconsideration is skipped and the draft findings stand |
| ProRV prefilter | Nothing — see below |

Map at least **Review default** and **Embedding default** — the Purposes screen warns while either is missing —
and map the rest unless you have a reason not to.

A purpose mapped to a logical-model name that no longer exists, or whose connection or model has since been
removed, is a hard failure rather than a fallback: resolution stops there and does not try the connection's
purpose bindings.

`proRvPrefilter` is the exception: the optional ProRV knowledge lens runs on the model configured on the `prorv`
entry in the client's [review-pass list](../concepts/reviews.md#review-passes), not on this purpose. Mapping it
changes nothing in a production review.

## Defining a logical model

A purpose can only be mapped to a name that exists, so the names come first. On the client's
**AI Providers → Logical models** screen, each one is four choices:

| Field | What it is |
|---|---|
| Name | Whatever you want to refer to it by — `fast`, `deep`, `embed`. Purposes and review passes use this name |
| Connection and model | Which attached model on which connection it resolves to |
| Reasoning effort | How hard that model thinks under this name — see [reasoning effort](#reasoning-effort) |
| Protocol mode | Leave on Auto unless your endpoint serves only one shape — see [protocol mode](#protocol-mode) |

The same underlying model can back several names at different efforts, which is the point of the
indirection: `deep` and `fast` can be one model reasoning hard and reasoning not at all.

Define at least one chat name and one embedding name before mapping purposes, since **Review default**
and **Embedding default** both have to resolve.

## Two layers

Logical-model names themselves come from two places:

1. **Tenant catalog** — the logical models a tenant defines, available to all of its clients.
2. **Client override** — a client that needs a different model under a name defines its own; a client override
   shadows the tenant entry of the same name.

A client override can only point at a connection inside its own tenant.

A Community installation has no tenant-catalog layer at all, so every name is a client one — see
[tenant-owned connections](credentials.md#tenant-owned-connections).

## Reasoning effort

A logical model can carry a reasoning effort of `none`, `low`, `medium` or `high`. Because it is a property of the
name, you can reason hard on the name mapped to High effort and not at all on the one mapped to Triage. ProPR
translates it to whatever the provider expresses — an OpenAI reasoning effort, an Anthropic thinking budget, a
Gemini thinking configuration — so the same setting means the same thing across providers. `none` sends no
reasoning effort at all and leaves the provider at its own default.

## Protocol mode

Leave this on **Auto** unless you know your endpoint only implements one shape. Auto lets ProPR pick what the
provider and model support.

Set on a purpose binding on the connection form, an unspeakable mode is refused at save time with a message naming
what the provider does speak. Set on a logical model it is **not** checked, so it saves cleanly and the review
fails when that model is first called. On a logical model, prefer Auto.

What each family speaks, if you do need to pin one:

| Family | Protocol modes |
|---|---|
| `azureOpenAi`, `openAi`, `liteLlm` | `auto`, `responses`, `chatCompletions`, `embeddings` |
| `openAiCompatible` | `auto`, `chatCompletions`, `embeddings` — the Responses API is deliberately not assumed of an arbitrary compatible server |
| `anthropic` | `auto`, `anthropicMessages` |
| `awsBedrock` | `auto`, `bedrockConverse`, `embeddings` |
| `googleVertex` | `auto`, `googleGenerateContent`, `embeddings` |

`embeddings` is the shape used to call an embedding model, which is why the one family with no embedding models
does not list it.
