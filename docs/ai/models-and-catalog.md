# Models, the catalog and prices

A verified connection does nothing until models are attached to it. This page covers getting the right models onto
a connection with correct metadata and correct prices. Spend figures and budgets are only as good as those.

Attaching a model does not decide what it is used for; that is [purposes](purposes.md).

## Discovery

Ask the provider what it exposes and pick from the result. Not every provider can be enumerated - when one
cannot, ProPR says so and manual entry stays available. Which providers those are, and
what to enter instead, is per family: see
[provider-specific setup notes](credentials.md#provider-specific-setup-notes).

Discovery returns model ids. It does not return context windows, capabilities or prices. The catalog holds
those. A LiteLLM gateway also states what each model is for (`mode`) and its input limit: an embedding model is then
offered as one whatever its name, and a model for images, speech or reranking is left out. Without a stated mode,
a model whose id contains `embedding` is offered as an embedding model and every other one as a chat model. On an
OpenAI connection, ProPR first leaves out image, video, speech, transcription, moderation, realtime, audio and search
models, recognised by their ids.

Discovery also returns `catalogModels`, the models of the catalog that the provider did not list. An OpenAI,
Azure OpenAI, Anthropic, Google Vertex or Amazon Bedrock connection gets the models of its own catalog provider. An
OpenAI-compatible or LiteLLM connection gets models from the whole catalog, each id once. Where several catalog
providers sell a model at different rates, that model comes without a price. Image, video, speech, transcription,
realtime and moderation models are left out. The list holds at most 200 models, the embedding models first and then
the chat models with the newest release first. It is returned also when the provider lists nothing or the listing
fails. A model id that neither list holds can still be entered by hand.

A discovery that fails says why in the categories of a verification, also when the call got no answer at all, for
example because the address does not resolve, the host does not trust the certificate, or the egress rules refuse
the connection. A redirect, such as one to a sign-in page in front of the endpoint, is a failure, because provider
calls do not follow redirects.

## Values of an embedding model

An embedding model is configured with a tokenizer, an input limit and a vector width. A connection can be saved
without them. ProPR then fills in each missing value from the first of these sources that has it:

1. The values OpenAI documents for `text-embedding-3-small`, `text-embedding-3-large` and `text-embedding-ada-002`:
   `cl100k_base`, 8191 tokens, and 1536, 3072 and 1536 dimensions. An id with a vendor prefix, such as
   `openai/text-embedding-3-small`, matches as well.
2. For the input limit, the context window of the model in the catalog, when its catalog entries agree on one value.
3. `cl100k_base`, 8191 tokens and 1536 dimensions.

A stated value is kept. A stated tokenizer must be `cl100k_base`, `o200k_base`, `o200k_harmony`, `r50k_base`,
`p50k_base` or `p50k_edit`, and a stated width must be between 64 and 4096. Discovered models and catalog models
arrive with the same values filled in.

ProPR stores embedding vectors with 1536 dimensions. The Embedding purpose accepts a model configured for 1536
dimensions and refuses any other width, so `text-embedding-3-large`, which returns 3072, cannot serve it. A width that
ProPR filled in is an assumption until a model check has measured the vectors the model returns.

## Checking the models

Verification proves the address and the credential. It does not prove a model: a model id with a typo, or one the
key may not use, verifies and then fails every call of a review. `POST /{connectionId}/check-models` calls up to
three configured models once each, a chat model for at most 16 tokens and an embedding model with one short text,
and reports each model that does not answer as configured, including an embedding model whose vectors have another
size than the configured dimensions. The result for an embedding model contains `returnedDimensions`, the length of the
vector the model returned, also when the check passed. The check uses the credential of the connection, so it costs
its owner a few tokens and is only run when asked for.

## The model catalog

ProPR ships with a bundled catalog of public model metadata - context window, whether the model does tool use and
structured output, embedding dimensions, and list pricing. It is embedded in the product, so nothing is fetched
from the internet to populate it.

When you browse the catalog while adding a model, the metadata comes along with your selection. Discovered
models are matched against it where the ids line up.

A platform administrator can upload a newer catalog snapshot from **Administration → SCM Providers**, in the
**Model catalog** section at the bottom of that page, when a model you need is newer than the bundled data.
Importing updates the shared entries every tenant reads. Tenant pricing overrides are left untouched, and a model
that has disappeared from the newer snapshot is kept, not removed from under a configuration still using it.

## Defining a model the catalog does not list

Private deployments, self-hosted models and fine-tunes are not in any public catalog. Add them by hand under the
tenant's **Model pricing overrides**, with **Define a model the catalog does not list**: you supply the provider,
model id, context window, prices, and whether it does tool calling, structured output and reasoning. A
hand-defined model behaves exactly like a catalog one from then on, for every client in that tenant.

## Pricing overrides

Catalog prices are public list prices. If you have negotiated different rates, override them per tenant under
**Tenant → Model pricing overrides**. Overrides apply to every client in the tenant and feed spend reporting and
budget enforcement, so getting them right is what makes cost numbers trustworthy.

Overrides are tenant-scoped. There is no per-client price override.

> **Commercial only.** Pricing overrides and hand-defined models are both tenant screens, so neither is reachable
> in a Community installation - see [editions](../reference/editions.md). Browsing the catalog and attaching its
> models to a client's connection work in either edition, and so does the third route below.

## A model is missing its context window or price

Its id did not match a catalog entry - most common with Bedrock and Vertex model ids.

On Commercial, add a pricing override for it, or define it by hand with the correct values. In Community neither is
reachable, and there is a third option that works in either edition: when you add the model to the connection, fill
in its capability and cost fields on the connection form instead of taking them from the catalog. Spend figures
then use what you entered.
