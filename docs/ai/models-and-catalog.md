# Models, the catalog and prices

A verified connection does nothing until models are attached to it. This page covers getting the right models onto
a connection with correct metadata and correct prices - which is what makes spend figures and budgets trustworthy.

Attaching a model does not decide what it is used for; that is [purposes](purposes.md).

## Discovery

Ask the provider what it exposes and pick from the result. Not every provider can be enumerated - when one
cannot, ProPR says so and manual entry stays available rather than blocking you. Which providers those are, and
what to enter instead, is per family: see
[provider-specific setup notes](credentials.md#provider-specific-setup-notes).

Discovery returns model ids. It does not return context windows, capabilities or prices, which is what the
catalog is for.

## The model catalog

ProPR ships with a bundled catalog of public model metadata - context window, whether the model does tool use and
structured output, embedding dimensions, and list pricing. It is embedded in the product, so nothing is fetched
from the internet to populate it.

When you browse the catalog while adding a model, the metadata comes along with your selection. Discovered
models are matched against it where the ids line up.

A platform administrator can upload a newer catalog snapshot from **Administration → SCM Providers**, in the
**Model catalog** section at the bottom of that page, when a model you need is newer than the bundled data.
Importing updates the shared entries every tenant reads. Tenant pricing overrides are left untouched, and a model
that has disappeared from the newer snapshot is kept rather than removed from under a configuration still using it.

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
