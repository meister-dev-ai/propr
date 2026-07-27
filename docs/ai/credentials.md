# AI connection credentials

How to get one AI connection created, credentialled, verified and active - the base URL shape, what exactly to
paste as the credential, and the per-family gotchas that account for most failed probes. For the families
themselves and the native-or-gateway choice, see [AI providers](index.md).

## Adding an AI connection

In the frontend, open the client, then the **AI Providers** tab. Its left menu has four sub-sections -
**Connections**, **Logical models**, **Purposes** and **Review passes**. Start in Connections. The create button
there is labelled **Add Profile**; the object it creates is what these pages call an AI connection.

1. **Choose the provider family.** The dropdown offers only families this build has a driver for *and* the tenant
   permits - anything else is absent rather than disabled. An existing connection whose family is no longer
   available is flagged **Unavailable**, and reviews that would use it are refused.
2. **Enter the base URL.** It is stored exactly as entered. The form shows the expected shape per family.
3. **Enter the credential.** One field per connection - see [Credentials by provider](#credentials-by-provider).
4. **Verify.** ProPR probes the endpoint and reports what is wrong when the probe fails.
5. **Add models.** Either discover them from the provider, browse the model catalog, or define one by hand - see
   [models and the catalog](models-and-catalog.md).
6. **Activate it.** A connection is created inactive, and activation is refused until the connection has been
   verified since its last change. While it is inactive, its purpose bindings are invisible to the review loop -
   the second resolution layer described under [AI purposes](purposes.md#ai-purposes) only looks at active
   connections.
7. **Point a logical model at one.** A connection with models attached does nothing until a logical model names one
   of those models and a purpose or a review pass uses that name - see [purposes](purposes.md).

More than one connection can be active at the same time, so a single client can mix providers. Which model does
which work is decided by logical models, not by which connection was activated last.

### Tenant-owned connections

The steps above create a connection owned by one client. A tenant administrator can instead define one on the
tenant, under **Tenant → Connections**: the same provider, base URL, credential and models, verified the same
way.

A tenant connection is deliberately thinner. It has no purpose bindings and no activation, because it is only
ever reached through a tenant-catalog logical model - see [Two layers](purposes.md#two-layers). Every client in
the tenant inherits those names; the connection behind one shows in a client's own logical-model list as
**Tenant-managed** and cannot be opened or edited from there.

One caveat, and it is about edition rather than a capability key: a Community installation has no tenant layer to
define either object on, so define connections and logical models on the client there - see
[editions](../reference/editions.md).

## Credentials by provider

| Family | What to enter |
|---|---|
| `azureOpenAi` | The resource key, or choose **Azure Identity** to use the host's managed identity instead of a key. Use the AI resource endpoint, not a deployment URL. |
| `openAi` | The API key. |
| `liteLlm` | The gateway key. Models are named as the gateway exposes them. |
| `openAiCompatible` | Whatever key that endpoint expects, sent as a bearer token. |
| `anthropic` | The API key. It is sent as the `x-api-key` header, which is what Anthropic reads. |
| `awsBedrock` | `accessKeyId:secretAccessKey`, or `accessKeyId:secretAccessKey:sessionToken` for temporary credentials. |
| `googleVertex` | For Vertex, the full JSON key of a service account. For the Gemini API, a plain API key. |

Credentials are [encrypted at rest and never shown again](../reference/security.md#secrets-at-rest). On the form,
re-entering a credential replaces it; leaving the field untouched keeps the stored one.

## Provider-specific setup notes

### Azure OpenAI

Use `azureOpenAi` for every Azure-hosted endpoint, including Azure AI Foundry endpoints the portal labels an
"OpenAI endpoint". Configuring one of those under `openAi` is refused with a message telling you which family to
use.

Azure is also the one family whose host is restricted outright rather than guarded at connect time. Which hostnames
that accepts, and why, is under
[outbound request protection](../reference/security.md#outbound-request-protection).

### OpenAI

`https://api.openai.com/v1` with an API key. This family is for OpenAI's own endpoints only; an Azure-hosted URL
under it is refused, as above.

### LiteLLM and OpenAI-compatible

Both take the base URL you supply and the key that endpoint expects. Model ids are whatever the gateway or server
exposes, not what the upstream vendor calls them.

`openAiCompatible` is deliberately assumed to serve the chat-completions surface only, which also narrows what a
model attached to such a connection may claim - see [protocol mode](purposes.md#protocol-mode).

A self-hosted endpoint on a private address - Ollama, vLLM, an internal gateway - is refused until
`AI_ALLOW_PRIVATE_EGRESS` is set. What that opt-in does and does not permit is under
[outbound request protection](../reference/security.md#outbound-request-protection); the variable itself is in
[configuration](../operate/configuration.md).

### Anthropic

Anthropic rejects a bearer token, so any authentication mode other than the API key above is refused on the form
rather than failing with a 401 on the first call.

Anthropic serves no embedding models. If you review on Anthropic, point the Embedding default purpose at a model
on a different connection. Nothing stops you saving an Anthropic connection without one - the refusal comes at the
first embedding call, with a message telling you to bind that purpose elsewhere - so check it during setup rather
than discovering it when thread memory silently stops working.

The base URL is not pinned to Anthropic's own host. The Messages protocol is also served by enterprise proxies and
by gateways configured to pass it through, and pointing this family at one of those keeps the native behaviour -
including the [cache breakpoints](index.md#prompt-caching) an OpenAI-shaped route loses.

### AWS Bedrock

Name the region in the host: `https://bedrock-runtime.eu-central-1.amazonaws.com` runs inference in Frankfurt. An
AWS host that names no region is refused at save time, and that check reads the host and nothing else - a `region`
default query parameter will not satisfy it. AWS China hosts (`*.amazonaws.com.cn`) count as AWS hosts too.

The `region=<region>` default query parameter exists for a private or VPC endpoint, permitted by the
private-egress opt-in, whose host is not an AWS host and therefore names no region.

**Leave that parameter unset on an AWS host.** When both are present the query parameter wins, so a connection
on `bedrock-runtime.eu-central-1.amazonaws.com` carrying `region=us-east-1` sends your code to Virginia. The
host is what you read to see where a connection points; it is not what enforces it.

Model discovery also needs an AWS host; on a private or VPC endpoint, enter the models by hand. Discovery
against AWS notes that some models can only be called through an inference profile - where your account
requires one, use the profile id as the model id.

The credential is always the one you store. ProPR never falls back to the AWS credentials of the machine it runs
on: in a multi-tenant installation that identity belongs to the operator rather than to the tenant whose review is
running, so a connection without its own access key is refused instead of quietly billed to someone else's role.

### Google Gemini and Vertex AI

Two different front doors, and which one a connection is, is decided by its host rather than by a setting that
could disagree with the URL:

- Vertex AI - `https://<location>-aiplatform.googleapis.com`, with a required `project=<your-gcp-project>`
  default query parameter and a service-account JSON key.
- Gemini API - `https://generativelanguage.googleapis.com` with a plain API key, no project needed.

A Vertex host must name its location, so the global `aiplatform.googleapis.com` endpoint is refused: the location
is where the inference happens and it belongs in the URL. Vertex also has no model listing on this surface, so
enter the model ids you intend to use by hand. A Gemini API connection must target a `*.googleapis.com` host
unless the private-egress opt-in is on.

## Troubleshooting

### Verification failures

A failed probe reports a category and, where one applies, what to change.

| Category | Usual cause |
|---|---|
| `credentials` | The key is wrong, expired, or in the wrong format for the family |
| `endpointReachability` | The base URL is wrong, unreachable, or blocked by egress rules |
| `authorization` | The credential is valid but lacks access to the resource or model |
| `providerRejected` | The provider refused the request - quota, region, or a model not enabled on the account |
| `capabilityMismatch` | The endpoint does not serve what this family expects |
| `unknown` | Nothing more specific could be determined |

### Common messages

| Message | What to do |
|---|---|
| "Azure-hosted OpenAI endpoints … must use providerKind 'azureOpenAi'" | Recreate the profile under the Azure family |
| "An AWS Bedrock connection must target an AWS host" | Use a `bedrock-runtime.<region>.amazonaws.com` URL, or enable the private-egress opt-in for a VPC endpoint |
| "The endpoint must name its region" | Put the region in the host - the check reads the host, so a `region` query parameter will not clear it |
| "A Vertex AI endpoint must name its location" | Use a located host such as `https://europe-west4-aiplatform.googleapis.com` |
| "the '<provider>' provider is not on this tenant's permitted provider list" | Ask a tenant administrator to permit the family under [Compliance](compliance.md) |
| "'<url>' is not on this tenant's permitted endpoint list" | Ask a tenant administrator to permit the host under [Compliance](compliance.md) |
| "the '<provider>' provider does not speak the '<mode>' protocol" | Set the logical model's [protocol mode](purposes.md#protocol-mode) back to Auto |
| "Anthropic does not serve embedding models" | Point Embedding default at a logical model on another connection |
| "No models were discovered from the provider" | Not an error - add the model by hand, or browse the catalog |

A model that verifies and calls cleanly but reports no context window or price is a catalog problem rather than a
credential one - see
[a model is missing its context window or price](models-and-catalog.md#a-model-is-missing-its-context-window-or-price).
Reviews that run but cost more than expected are covered in [controlling cost](../guides/control-cost.md).

For a symptom that is not about this connection, start from [troubleshooting](../operate/troubleshooting.md).
