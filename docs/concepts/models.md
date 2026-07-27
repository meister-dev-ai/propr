# Purposes and logical models

Nothing in the review loop names a provider. It asks for a *purpose*; the purpose resolves to a
*logical model*; the logical model is the only thing that knows which connection and which model id
get called.

Two objects, and the difference matters:

- An **AI purpose** is a fixed slot the review loop asks for - review generation, triage, verification,
  embeddings. The set is fixed; you cannot add one.
- A **logical model** is a name you choose (`fast`, `deep`, `embed` - anything) that points at one model
  on one connection and carries that model's reasoning effort and protocol mode.

That indirection is what you get for the extra object: because no client's review configuration names
a provider, you can move a tenant from OpenAI to Anthropic by repointing the names, and no purpose
mapping, review pass or prompt override has to change.

Everything operational lives elsewhere. Mapping purposes to names, what happens when one resolves to
nothing, and where reasoning effort and protocol mode are set are all in
[purposes, effort and protocol](../ai/purposes.md). Which providers a name can point at is in
[AI providers](../ai/index.md), and getting models attached to a connection in the first place is in
[models, the catalog and prices](../ai/models-and-catalog.md).
