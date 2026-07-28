# MeisterDev.Ai.Providers

Everything needed to reach a model provider, and nothing about what the request is for.

The library answers four questions for a host application:

1. **Which provider families can this build actually call?** - the driver registry.
2. **Can I reach this endpoint, and what models does it have?** - probe, verify, discover.
3. **Give me an `IChatClient` / `IEmbeddingGenerator` for this endpoint and model.** - the driver seam.
4. **Was that failure worth retrying, and what did the call cost in tokens?** - retry classification and usage extraction.

It is built on `Microsoft.Extensions.AI`, so what comes back out is `IChatClient` and
`IEmbeddingGenerator<string, Embedding<float>>` - not a wrapper type of our own.

## Boundary rules

Two rules keep this library separable from the product that hosts it, and both are asserted by
`LibraryIsolationTests` rather than trusted:

- **No `ProjectReference` on any `MeisterDev.ProPR.*` project**, and no host assembly in the reference graph.
- **No file carries the commercial-only notice.** The library is Elastic-2.0 like the rest of the repository, but
  it gates nothing.

A third rule follows from them: no review vocabulary on the seam. The library knows about endpoints, models,
protocols, tokens and failures. It does not know what a finding, a pass, a client or a tenant is. Anything the
library would need a product concept for is instead *contributed* by the host - see
[Extension points](#extension-points-a-host-fills-in).

## Quick start

Register one driver per provider family you want callable, plus the registry:

```csharp
services.AddHttpClient("AiProviderRuntime", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .AddHttpMessageHandler(() => new ReasoningContentRoundTripHandler())
    .ConfigurePrimaryHttpMessageHandler(() => GuardedEgressHttpHandler.Create(allowPrivateEgress));

services.AddSingleton<OpenAiCompatibleRequestFactory>();
services.AddSingleton<OpenAiCompatibleTransport>();

services.AddSingleton<IAiProviderDriver, AzureOpenAiProviderDriver>();
services.AddSingleton<IAiProviderDriver>(sp => new AnthropicProviderDriver(
    sp.GetRequiredService<OpenAiCompatibleTransport>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    allowPrivateEgress,
    allowInsecureScheme: isDevelopment));
// … one line per family …

services.AddSingleton<IAiProviderDriverRegistry, AiProviderRegistry>();
```

Then resolve and call:

```csharp
var driver = registry.GetRequired(providerKind);

// Configuration time: is this target acceptable, and what is behind it?
if (driver.ValidateProbeTarget(new AiProbeTarget(baseUrl, authMode, hasCredential)) is { } refusal)
{
    return BadRequest(refusal);
}

var verification = await driver.VerifyAsync(endpoint, ct);
var discovery = await driver.DiscoverModelsAsync(endpoint, ct);

// Run time: one client per resolved binding.
var client = driver.CreateChatClient(endpoint, model, protocolMode);
var capabilities = driver.GetChatRuntimeCapabilities(endpoint, model, protocolMode);
```

`ProviderEndpoint` and `ProviderModelDescriptor` are the only inputs a driver takes - the same shapes at
configuration time and at run time, because both answer the same question:

```csharp
var endpoint = new ProviderEndpoint(
    AiProviderKind.Anthropic,
    "https://api.anthropic.com/v1",
    AiAuthMode.XApiKey,
    Secret: apiKey,                    // never logged; ToString() is overridden to elide it
    DefaultHeaders: null,
    DefaultQueryParams: null);

var model = new ProviderModelDescriptor(
    Id: configuredModelId,             // host-side id, carried through for correlation only
    RemoteModelId: "claude-sonnet-4-5",
    SupportedProtocolModes: [AiProtocolMode.Auto, AiProtocolMode.AnthropicMessages],
    ReasoningContentField: null);
```

## The public surface

| Namespace | What lives there |
| --- | --- |
| `Drivers` | `IAiProviderDriver`, `IAiProviderDriverRegistry`, `AiProviderRegistry`, the seven concrete drivers, `AiProtocolModeSupport`, `DriverFailureMapper` |
| `Contracts` | `ProviderEndpoint`, `ProviderModelDescriptor`, `ProviderVerificationResult`, `ProviderModelDiscoveryResult`, `ProviderDiscoveredModel`, `ProviderRuntimeCapabilities`, `ProviderSecretEnvelope`, `ProviderCallTarget`, `AiProbeTarget`, `INativeProtocolChatClient`, `ProviderReasoningRequest` |
| `Enums` | `AiProviderKind`, `AiProtocolMode`, `AiAuthMode`, `AiOperationKind`, `AiVerificationStatus`, `AiVerificationFailureCategory`, `ProviderReasoningEffort` |
| `Runtime` | `ProviderRuntimePipeline`, `IProviderChatClientDecorator`, `ProviderRuntimeStage` |
| `Resilience` | `ProviderRetryPolicy`, `ProviderFailureVerdict`, `ProviderRetryChatClient(Decorator)`, `ProviderRetryEmbeddingGenerator`, `ProviderCallFailedException` |
| `Usage` | `ProviderUsageExtractor`, `ProviderTokenUsage` |
| `Egress` | `GuardedEgressHttpHandler`, `EgressAddressPolicy`, `AzureAiHostPolicy` |
| `Transport` | `OpenAiCompatibleTransport`, `AnthropicMessagesChatClient`, `BedrockConverseChatClient`, `GoogleGenerateContentChatClient`, `GoogleEmbeddingGenerator`, `ReasoningContentRoundTripHandler`, the Bedrock/Google credential + endpoint resolvers |
| `Catalog` | `BundledCatalogSnapshot`, `ICatalogSnapshotImporter`, `ModelsDevCatalogSnapshotImporter`, `ProviderCatalogEntry` |
| `Diagnostics` | `SecretSafeRendering` |

### Enums name more than the drivers implement

`AiProviderKind`, `AiProtocolMode` and `AiAuthMode` are open vocabularies: a family can be named here before its
driver exists. **The registry, not the enum, is the authority on what this build can call.** Ask
`RegisteredKinds` before offering a family to an operator, and `SupportedProtocolModes` before offering a protocol
- otherwise opening the enum lets someone store a profile that only fails once a workload runs.

`AiProtocolModeSupport` is how a driver says no in one voice: `DescribeRefusal` for the configuration path (a
message), `Require` for the runtime path (a throw), `NarrowToSupported` so `Auto` can never resolve to a shape the
driver does not speak.

### Capabilities are facts, not intentions

`GetChatRuntimeCapabilities` reports what the provider *and the protocol it was bound to* can do -
provider-managed sessions, background responses, prompt caching, cache routing. A driver claims a capability only
when the client it just built actually exercises it: reaching Claude through an OpenAI-compatible proxy loses
cache-control breakpoints, so only the native Anthropic driver claims `SupportsPromptCaching`.

### Failure classification belongs to the driver

`ClassifyRuntimeFailure` has a default implementation that reads the HTTP status, and is right for anything
speaking HTTP with conventional codes. Override it to recognise SDK-specific signals *first*, then defer to
`DriverFailureMapper.ClassifyRuntimeFailure` for the rest - a driver that classifies everything itself gets the
common cases subtly wrong. Anthropic's `529 overloaded` and the AWS SDK's own throttling exception types are the
two cases in the library today.

### The runtime pipeline has a fixed stage order

`ProviderRuntimePipeline` composes decorators by the stage each one *declares*, so registration order is
irrelevant and adding a decorator cannot silently reorder the others:

```
Retry (outermost) → Observability → Budget → Normalization → the driver's client → wire
```

The order is behaviour, not style. Retry outermost means a metering stage counts each attempt exactly once;
observability inside retry sees each attempt separately and captures a budget refusal within the attempt that
provoked it; normalization innermost applies to retried attempts too.

## Extension points a host fills in

| Seam | Why the host owns it |
| --- | --- |
| `IProviderChatClientDecorator` | Cost, entitlement and telemetry are product concepts. A host contributes a decorator at the stage it belongs to; the library ships only the retry decorator. |
| `ProviderReasoningRequest` + `INativeProtocolChatClient` | `ChatOptions.RawRepresentationFactory` is invoked **per client**, which is what lets one call site serve providers that express reasoning incompatibly. A caller that finds `INativeProtocolChatClient` passes the neutral request; the OpenAI family gets the OpenAI library's options object. |
| `ProviderSecretEnvelope` | One credential is one opaque blob to whatever stores it; this is the only thing that knows what is inside. SigV4 needs three fields and a Google service account is a JSON document, so a bare string does not fit. Decoding tolerates a bare string, which is what rows written before the envelope contain. |
| `ProviderUsageExtractor` | Reads library-normalized properties first, then recovers missing counters from `UsageDetails.AdditionalCounts` by provider-specific name. A new provider adds its key set here rather than anywhere in the host's workload code. |
| `ICatalogSnapshotImporter` | The library carries the embedded snapshot and can parse it; persisting it is the host's. |

## Adding a provider driver

1. Add the family to `AiProviderKind` (and a protocol shape to `AiProtocolMode` if it speaks its own).
2. Implement `IAiProviderDriver`. Declare `SupportedProtocolModes` honestly and call
   `AiProtocolModeSupport.Require` at the top of `CreateChatClient` / `CreateEmbeddingGenerator`.
3. Validate the probe target through `AiProbeTargetValidation` so the SSRF-egress and auth-shape rules stay
   shared rather than re-derived.
4. Build the transport on the host's `"AiProviderRuntime"` `HttpClient` - that is where the egress guard and the
   reasoning round-trip live. An SDK with its own transport needs an `HttpClientFactory` hook to the same client
   (see `BedrockClientFactory`).
5. Claim capabilities only where the client exercises them. Refuse what the provider does not serve, with a
   message naming what to do instead (Anthropic has no embeddings; it says so rather than failing on the wire).
6. Add a `Usage.ProviderUsageExtractor` key set if the provider names its cache or reasoning counters its own way.
7. Add a `DriverConformanceFixture` entry. The conformance suite measures the seam behaviour every driver must
   share; a new driver joining it is how parity stops being a matter of opinion.
8. Register the driver in the host's composition root. The registry indexes what was registered - nothing else
   needs to change.

## Tests

`tests/MeisterDev.Ai.Providers.Tests` (166 test methods) covers per-driver behaviour, the transports' wire shape
against fake endpoints, retry mechanics on a controlled `TimeProvider`, usage extraction, the secret envelope, the
egress policies, the runtime pipeline's stage order, the shared conformance suite, and the isolation rules above.
