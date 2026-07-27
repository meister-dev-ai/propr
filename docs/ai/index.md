# AI providers

ProPR reviews run on a model provider you configure and pay for. This page covers which providers ProPR can
call and how to choose between reaching a model through its own provider family and reaching it through a
gateway — a choice that is mostly about prompt caching, and therefore about money.

Source-control provider connections are a separate concern — see
[the platform overview](../platforms/index.md). The two are unrelated: a client reviewing Azure DevOps pull
requests can run its reviews on Anthropic.

The rest of the AI configuration is split across four pages:

| Page | What it covers |
|---|---|
| [Credentials](credentials.md) | Creating a connection per provider family, what to paste, and getting it verified and active |
| [Models and the catalog](models-and-catalog.md) | Attaching models, their metadata, and their prices |
| [Purposes](purposes.md) | Which model does which work, its reasoning effort, and its protocol mode |
| [Compliance](compliance.md) | Restricting, per tenant, which families and hosts may be reached |

Why the review loop never names a provider at all is explained in
[how model selection works](../concepts/models.md).

## Supported providers

The first column is the label the provider dropdown shows; the second is the value the API uses.

| Provider family | `providerKind` | Typical base URL | Chat | Embeddings |
|---|---|---|---|---|
| Azure OpenAI / AI Foundry | `azureOpenAi` | `https://your-resource.openai.azure.com/` | yes | yes |
| OpenAI (non-Azure) | `openAi` | `https://api.openai.com/v1` | yes | yes |
| LiteLLM | `liteLlm` | `https://gateway.example.com/v1` | yes | yes |
| OpenAI-compatible (custom base URL) | `openAiCompatible` | any | yes | yes |
| Anthropic (native) | `anthropic` | `https://api.anthropic.com/v1` | yes | **no** |
| AWS Bedrock | `awsBedrock` | `https://bedrock-runtime.<region>.amazonaws.com` | yes | yes |
| Google Gemini / Vertex AI | `googleVertex` | `https://<location>-aiplatform.googleapis.com` | yes | yes |

`openAiCompatible` is the catch-all for anything serving an OpenAI-shaped `/chat/completions` at a URL you supply:
a vendor's own API (DeepSeek, Qwen, Kimi, MiniMax, xAI, Mistral, Groq, Together, Fireworks), an aggregator such as
OpenRouter, or something you host yourself such as Ollama or vLLM. Reach for it before asking for a new provider
family — most "can ProPR use X" questions are answered by it.

Anthropic is the one family with no embedding models — see [Anthropic](credentials.md#anthropic). Nothing forces
a client onto one family either; [adding an AI connection](credentials.md#adding-an-ai-connection) covers mixing
several on one client.

## Native provider or gateway?

Anthropic, Bedrock and Gemini models are reachable two ways: through their own family, or through a
`liteLlm` / `openAiCompatible` gateway that fronts them. Both work, and [prompt caching](#prompt-caching) is
usually what decides which — the table below answers it per family.

What a native family gives you beyond caching:

- **Google Gemini / Vertex** — the project-scoped Vertex endpoint with service-account authentication rather than
  a public key.
- **AWS Bedrock** — the only route to a model available in one AWS region and nowhere else, and the only one that
  authenticates with your own AWS credentials rather than a shared gateway key.

A gateway is worth choosing when you want one key, one bill and one place to enforce rate limits across several
vendors, or when the vendor you want has no family of its own — which `openAiCompatible` covers.

## Prompt caching

Caching is the one worth money: it is the difference between paying for the same repeated prompt prefix on every
file of every review and paying for it once. A review is unusually well suited to it, because the reviewer's
standing brief is identical on every file of every pull request.

| Family | Prompt caching |
|---|---|
| `anthropic` | Yes. Anthropic caches only what the caller marks, and the native client marks it — the system prompt, and the end of the conversation prefix, once each is large enough to be worth caching |
| `googleVertex` | Yes. Gemini and Vertex cache a repeated prefix themselves and report what they served from it, so there is nothing for a caller to mark |
| `azureOpenAi` | Yes. The service caches a repeated prefix itself, so ProPR counts it as a caching provider without marking anything |
| `openAi` | Not claimed. ProPR places no breakpoint here and records the cache outcome as unsupported, though cached-token counts are still read from the usage payload when the endpoint reports them |
| `liteLlm`, `openAiCompatible` | No. A gateway request carries no cache breakpoint, so a Claude model reached this way pays full input price on every call |
| `awsBedrock` | No. A Bedrock cache breakpoint is a content block, and the AWS interface ProPR calls through offers no way to place one — so Bedrock does not cache here, native or fronted |

Where a provider reports cache reads in its usage payload, ProPR records them whatever the family: cached input
tokens and cache-write tokens are stored per model call alongside the ordinary input and output counts.

To see whether caching is actually happening, read the review protocol. Each model call carries a cache outcome
and, when the call was not served from cache, why not: the provider does not support it, the cacheable prefix
changed since the previous call, the provider's cache had expired, or the provider did not say. The endpoint that
returns it is under [review diagnostics](../reference/api.md#review-diagnostics).

For the full list of cost levers in the order worth pulling them, see
[controlling cost](../guides/control-cost.md).
