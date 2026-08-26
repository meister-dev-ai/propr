# Run ProPR without internet access

ProPR contacts your SCM host, your AI model endpoints, and your own infrastructure. It also sends one usage
report a day to `telemetry.meister-dev.ai`. Those are all the destinations, and this page lists them so you
can build an allowlist from it.

ProPR needs no license server and downloads no model catalog - see
[where your code goes](../reference/security.md#where-your-code-goes).

The usage report contains no code, no repository names and no user names. A Community installation sends a
random installation identifier and can switch the report off. A commercial installation also sends its
license identifier, which identifies the licensee, and keeps sending while the license is in force - see
[usage statistics](../reference/usage-statistics.md).

## What you have to host yourself

| Requirement | Notes |
|---|---|
| The three release images, mirrored | Pull them from the public registry into your internal one and keep the tags aligned - see [running published images](../operate/deploy.md#running-published-images) |
| Not a source build | Building the images from the checkout needs package feeds, so mirroring released images is the offline path - see [quickstart](../quickstart.md) |
| PostgreSQL with the `vector` extension | See [what you need](../quickstart.md#what-you-need) |
| A writable review workspace volume | See [review workspace](../operate/deploy.md#review-workspace) |
| A durable, backed-up key ring volume | See [what to back up](../operate/upgrades-and-backups.md#what-to-back-up) |

## What needs no network at all

| Capability | Why it works offline |
|---|---|
| The model catalog | Embedded in the product; a newer snapshot is an operator upload, not a fetch - see [the model catalog](../ai/models-and-catalog.md#the-model-catalog) |
| The ProRV knowledge lens | Its catalog is embedded too - see [review passes](../concepts/reviews.md#review-passes) |
| The edition and its capabilities | The license file is verified against a trust anchor compiled into the product and then stored in your own database; no license server is contacted in either edition - see [activating a license](../reference/editions.md#activating-a-license) |
| Sign-in | Local accounts need nothing external. Single sign-on to a cloud identity provider does, and is licensed - see [sign-in and sessions](../reference/security.md#sign-in-and-sessions) |
| Review diagnostics | The protocol, findings and thread memory are all in your database - see [reviews](../concepts/reviews.md) |

## Point ProPR at a model you host

Use the `openAiCompatible` family for anything serving an OpenAI-shaped API at a URL you supply, including
something you run yourself - see [AI providers](../ai/index.md).

An endpoint on a private address needs the private-egress opt-in. Read
[outbound request protection](../reference/security.md#outbound-request-protection) before you plan the
endpoint. The endpoint needs a TLS certificate the container running ProPR trusts, because nothing skips
certificate validation.

Three family-specific consequences of a private endpoint:

- **Azure OpenAI.** A private endpoint keeps the Azure hostname this family requires - see
  [outbound request protection](../reference/security.md#outbound-request-protection).
- **AWS Bedrock.** A private or VPC endpoint names no region, so supply it as the `region` default query
  parameter, and enter the models by hand because discovery needs an AWS host - see
  [AWS Bedrock](../ai/credentials.md#aws-bedrock).
- **Embeddings.** Thread memory and ProCursor need an embedding model, so you have to serve one yourself.
  Check what degrades if nothing does - see [purposes](../ai/purposes.md).

## What ProPR still contacts

- **Your AI provider**, unless you host the model yourself. The compliance host list constrains which
  provider families and endpoint hosts a tenant may reach - see
  [restrict where your code goes](restrict-where-code-goes.md).
- **Your SCM host.** The self-hosted variants authenticate with a credential issued by that host. Hosted
  Azure DevOps Services is the exception: it authenticates through Microsoft Entra, so it needs reachable
  Microsoft identity endpoints as well as the Azure DevOps host itself. See
  [support matrix](../platforms/index.md#support-matrix).
- **Your own collectors**, if you configured a trace or log endpoint - see
  [observability](../operate/observability.md).
- **`telemetry.meister-dev.ai`**, once a day. Community installations can switch this off under
  **Administration → Usage Statistics** - see [usage statistics](../reference/usage-statistics.md).

## Confirm it worked

1. Verify each AI connection. The probe is a real call to your endpoint, so a green verification proves
   the path, the credential and the certificate at once.
2. Call `GET /healthz` and confirm every check it reports is healthy - see
   [what the health checks mean](../operate/observability.md#what-the-health-checks-mean).
3. Attach a model from the catalog and confirm it arrives with a context window and a price. That
   metadata came from inside the image; nothing was fetched.
4. Trigger one review from the API and read its protocol - see
   [trigger a review](../reference/api.md#trigger-a-review).
5. Read your egress logs for that review. A review reads data from your SCM host and contacts your
   configured AI models. On hosted Azure DevOps you also see Microsoft identity endpoints, and you see your
   own collectors if you configured them. An entry for `telemetry.meister-dev.ai` is the daily usage report,
   not part of the review.
6. On a Community installation, switch the usage report off, then confirm `telemetry.meister-dev.ai` no
   longer appears in your egress logs.

If a verification or a health check fails, [troubleshooting](../operate/troubleshooting.md) routes the
symptom.
