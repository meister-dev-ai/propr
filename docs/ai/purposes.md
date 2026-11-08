# Purposes, effort and protocol

Every piece of AI work a review does asks for a purpose, and each purpose has to reach a logical model that names
a real model on a real connection. This page is that mapping surface: which purposes exist, how one resolves, what
happens when it does not, and the two properties a logical model carries - reasoning effort and protocol mode.

The distinction between a purpose and a logical model, and why the review loop never names a provider, is on
[how model selection works](../concepts/models.md). Mapping is done on the client's **AI Providers → Purposes**
screen; extra [review passes](../concepts/reviews.md#review-passes) pick a logical model by name in the same way.

## AI purposes

| Purpose (UI label) | API value | Used for |
|---|---|---|
| Review default | `reviewDefault` | Primary review generation, and answering @-mentions |
| Embedding default | `embeddingDefault` | Embeddings for thread memory and ProCursor similarity |
| Triage | `reviewTriage` | Cheap per-file complexity classification |
| Verification | `reviewVerification` | The evidence judge that decides every finding and its contradiction check, the AI duplicate judge and the acceptance forecast |
| Low effort / Medium effort / High effort | `reviewLowEffort`, `reviewMediumEffort`, `reviewHighEffort` | Per-file review uses the assigned complexity tier. High effort also runs synthesis, the rewrite of a summary that describes a finding the gate dropped, and the escalation check for Errors and Warnings that verification leaves at Medium or below. A runner uses the job manifest's available runtime when a requested purpose is unavailable. |
| Memory reconsideration | `memoryReconsideration` | Re-judging an existing comment thread on a re-review |
| ProRV prefilter | `proRvPrefilter` | Not used by the review path - see below |

A purpose is scoped to the work that asked for it. The High-effort model synthesis resolves applies to the
synthesis call and to the rewrite of its summary, so the other stages around synthesis - comment deduplication, the
comment quality filter, pull-request-level verification - each stay on their own purpose, and their token usage is
reported against the model they actually ran on.

A purpose resolves in this order:

1. **The purpose → logical model map.** A direct mapping takes precedence over connection bindings. A broken
   direct mapping fails resolution.
2. **The purpose bindings on the client's active AI connections.** ProPR searches every active client profile
   for an enabled binding to the requested purpose with a configured model. If none matches, it searches every
   active profile for each fallback purpose in order. Profiles are ordered by display name, then ID, within each
   purpose. This allows chat and embedding bindings to use separate providers. This layer chains to a cheaper relative -
   Triage → Low effort → Review default; Verification → Triage → Low effort → Review default; and each per-tier
   review purpose → Review default. Review default, Embedding default and Memory reconsideration have no chain;
   each resolves only from its own binding.
3. **Fallback logical-model mappings.** If no connection binding resolves, ProPR checks logical roles along
   the same fallback chain. A mapped fallback role takes precedence over a missing-model failure.

When no lookup yields a model, behavior depends on the requested purpose. Most degrade; two fail.

| Purpose | If nothing resolves |
|---|---|
| Review default | The review job fails and nothing is posted. An @-mention reply fails the same way |
| Embedding default | Thread memory is neither stored nor retrieved, and ProCursor indexing and search fail |
| Low / Medium / High effort | The file is reviewed on the Review default model instead. For High effort, synthesis runs there too |
| Triage | The deterministic size heuristic classifies the file - no model judges complexity |
| Verification | The evidence judge and its contradiction check run on the model of the reviewing pass, and the AI duplicate judge keeps both findings |
| Memory reconsideration | Reconsideration is skipped and the draft findings stand |
| ProRV prefilter | Nothing - see below |

Map at least **Review default** and **Embedding default** - the Purposes screen warns while either is missing -
and map the rest unless you have a reason not to.

A purpose mapped to a logical-model name that no longer exists, or whose connection or model has since been
removed, is a hard failure and not a fallback: resolution stops there and does not try the connection's
purpose bindings.

`proRvPrefilter` is the exception: the optional ProRV knowledge lens runs on the model configured on the `prorv`
entry in the client's [review-pass list](../concepts/reviews.md#review-passes), not on this purpose. Mapping it
changes nothing in a production review.

## Defining a logical model

A purpose can only be mapped to a name that exists, so the names come first. On the client's
**AI Providers → Logical models** screen, each one is four choices:

| Field | What it is |
|---|---|
| Name | Whatever you want to refer to it by - `fast`, `deep`, `embed`. Purposes and review passes use this name |
| Connection and model | Which attached model on which connection it resolves to |
| Reasoning effort | How hard that model thinks under this name - see [reasoning effort](#reasoning-effort) |
| Protocol mode | Leave on Auto unless your endpoint serves only one shape - see [protocol mode](#protocol-mode) |

The same underlying model can back several names at different efforts, which is the point of the
indirection: `deep` and `fast` can be one model reasoning hard and reasoning not at all.

Define at least one chat name and one embedding name before mapping purposes, since **Review default**
and **Embedding default** both have to resolve.

## Two layers

Logical-model names themselves come from two places:

1. **Tenant catalog** - the logical models a tenant defines, available to all of its clients.
2. **Client override** - a client that needs a different model under a name defines its own; a client override
   shadows the tenant entry of the same name.

A client override can only point at a connection inside its own tenant.

A Community installation has no tenant-catalog layer at all, so every name is a client one - see
[tenant-owned connections](credentials.md#tenant-owned-connections).

## Reasoning effort

A logical model can carry a reasoning effort of `none`, `low`, `medium` or `high`. Because it is a property of the
name, you can reason hard on the name mapped to High effort and not at all on the one mapped to Triage. ProPR
translates it to whatever the provider expresses - an OpenAI reasoning effort, an Anthropic thinking budget, a
Gemini thinking configuration - so the same setting means the same thing across providers.

A review pass uses the effort of the logical model it runs on. The baseline pass of a file runs on the logical model
that the file's tier purpose resolves to. When that logical model has the effort `none`, the baseline pass uses the
client's baseline reasoning effort. An additional review pass that names a logical model uses the effort of that
logical model, also when it is `none`.

The Responses API and the Anthropic, Google Vertex and Amazon Bedrock protocols receive `low`, `medium` and `high`.
On Chat Completions, ProPR sends none of these three efforts, also in runner jobs, and the model uses its own
default. OpenAI refuses function tools on Chat Completions from GPT-5.4 on with any effort other than
`none`, and an OpenAI-compatible server whose model does not know the field can refuse the request.

Each chat model on a connection has the setting **Accepts Reasoning Effort None**. For a review pass with the
effort `none`, ProPR sends the reasoning effort `none` to a model with this setting, on Chat Completions and on the
Responses API. For a model without it, ProPR leaves the effort out, and the model uses its own default. A
model that reasons by default then still reasons and bills reasoning tokens.

Turn the setting on when the provider documents the effort `none` for the model. OpenAI lists the accepted efforts
on the page of each model, and a model that does not accept `none` refuses the request with HTTP 400. Discovery and
the model catalog leave the setting off.

The effort of a logical model, the client's baseline reasoning effort and the effort of a review pass follow this
rule. A review pass with the effort `none` records the event `reasoning_effort_sent` in its trace. The event holds
`"reasoningParameter": "sent"` when ProPR sent `none`, and `"reasoningParameter": "omitted"` when ProPR left the
effort out.

The evidence judge requests `low` effort, including on its bounded output-budget retry, when the bundled
catalog advertises that effort for the exact model and the selected transport supports it. This call
setting overrides the selected logical model's effort. On any other model, the judge runs with the effort `none`,
and both contradiction checks run with the effort `none` on every model. These calls follow the rule above, also
when the judge runs on the reviewer's own model because no model resolves for the Verification purpose. The value `low`, `medium` or
`high` in `AI_VERIFICATION_REASONING_EFFORT` replaces both rules with that effort on every model. Selecting the
Verification purpose chooses a model; the supported call setting controls its reasoning effort. The effort of these calls
reaches each transport as described above, also in runner jobs, so Chat Completions receives only `none`. See
[verification](../concepts/reviews.md#what-happens-during-a-review) and
[judge budgets](../operate/configuration.md#evidence-judge-budgets).

## Protocol mode

Leave this on **Auto** unless you know your endpoint only implements one shape. Auto lets ProPR pick what the
provider and model support. For a chat model, the API that Auto picks depends on the family:

- `meisterdev/openAi` calls the Responses API, unless the model's
  [protocol modes](../reference/api.md#per-model-inputs) include Chat Completions and not Responses.
- `meisterdev/azureOpenAi` calls the Responses API for every model.
- `meisterdev/liteLlm` calls the Responses API when the model's protocol modes include Responses, and Chat
  Completions otherwise.
- `meisterdev/openAiCompatible` calls Chat Completions.
- `meisterdev/anthropic`, `meisterdev/awsBedrock` and `meisterdev/googleVertex` call their native APIs: Anthropic
  Messages, Bedrock Converse and Google generateContent.

Set on a purpose binding on the connection form, an unspeakable mode is refused at save time with a message naming
what the provider does speak. Set on a logical model it is **not** checked, so it saves cleanly and the review
fails when that model is first called. On a logical model, prefer Auto.

A protocol mode a family owns is written as the family's identity key, a `:`, and the mode name, so two
families that both speak a mode called `ChatCompletions` name two different shapes. `Auto` and `Embeddings`
belong to no family and are written on their own.

What each family speaks, if you do need to pin one:

| Family | Protocol modes |
|---|---|
| `meisterdev/azureOpenAi`, `meisterdev/openAi`, `meisterdev/liteLlm` | `Auto`, `<key>:Responses`, `<key>:ChatCompletions`, `Embeddings` |
| `meisterdev/openAiCompatible` | `Auto`, `meisterdev/openAiCompatible:ChatCompletions`, `Embeddings` - the Responses API is deliberately not assumed of an arbitrary compatible server |
| `meisterdev/anthropic` | `Auto`, `meisterdev/anthropic:AnthropicMessages` |
| `meisterdev/awsBedrock` | `Auto`, `meisterdev/awsBedrock:BedrockConverse`, `Embeddings` |
| `meisterdev/googleVertex` | `Auto`, `meisterdev/googleVertex:GoogleGenerateContent`, `Embeddings` |

`Embeddings` calls an embedding model, so the one family with no embedding models does not list it.
