# MeisterDev.Ai.Providers

`MeisterDev.Ai.Providers` is the host side of the provider system. A provider family — OpenAI, Azure OpenAI,
Anthropic, AWS Bedrock, Google Vertex, LiteLLM, and endpoints speaking an OpenAI-compatible protocol — ships
as an add-in carrying its own driver, its vendor SDK and its declaration. This library loads those add-ins
and hosts the families they declare.

A family is written against a separate assembly,
[`MeisterDev.Ai.Providers.Abstractions`](../MeisterDev.Ai.Providers.Abstractions/README.md), whose README
covers writing one. This page covers the host.

The library does four things for the application that hosts it:

1. **Which families this deployment can call** — the add-in loader finds them and the registry indexes them.
2. **Probe, verify, discover** — configuration-time calls on the driver seam, answered by the family.
3. **An `IChatClient` or `IEmbeddingGenerator<string, Embedding<float>>` for one endpoint and model** — the
   driver builds it and the runtime pipeline wraps it.
4. **Retry classification and token counts** — the driver classifies its own failures, and the usage
   decorator replaces the provider's counters with the ones the driver mapped them onto.

It is built on `Microsoft.Extensions.AI`, so it returns `IChatClient` and
`IEmbeddingGenerator<string, Embedding<float>>` directly, with no wrapper type of its own.

## Boundary rules

This library is developed inside the repository of ProPR, an automated code-review product, and is kept
separable from it by two rules:

- **No `ProjectReference` on any `MeisterDev.ProPR.*` project**, and no application assembly in the reference
  graph. `LibraryIsolationTests` asserts this, along with every public type sitting under the root namespace
  and the provider vocabulary coming from the contract assembly.
- **No file carries the commercial-only notice.** The library is Elastic-2.0 like the rest of the repository,
  and it gates nothing.

A third rule follows from those two. The seam carries no vocabulary from the hosting application: the
library's own vocabulary is endpoints, models, protocols, tokens and failures. The host supplies everything
named in its own terms through the [extension points](#extension-points-a-host-fills-in).

## Loading families

`ProviderAddInLoader.Load` runs once while the host starts and returns a `ProviderAddInCatalog`: the add-ins
that loaded, the assemblies that were declined, and the drivers to register. It reads two directories, the
built-in one first:

| Directory | Path | Who changes its contents |
| --- | --- | --- |
| Built-in | `provider-add-ins`, beside the application's own assemblies | a product release |
| External | `AI_PLUGIN_DIRECTORY`, defaulting to `plugins` under the content root | an operator |

Each add-in loads into its own `ProviderAddInLoadContext`, which resolves the assemblies named by
`ProviderAddInSharedAssemblies` from the host and everything else from the add-in's own folder.

The loader runs the checks in `MeisterDev.Ai.Providers.Conformance` against every family it loads and declines
one that fails, naming the check. The catalog's rejection list names each family that did not load, with a
`ProviderAddInRejectionCategory` and a reason:

| Category | What happened |
| --- | --- |
| `Failed` | The assembly threw while it was being read, or exposed no provider family. |
| `Duplicate` | Another add-in, or a family compiled into the host, already claimed the identity. |
| `MisPackaged` | The folder holds a copy of an assembly the host and the add-in have to share. |
| `VersionMismatch` | The family declared a contract version this host does not accept. |
| `NonConforming` | The family loaded and then failed one of the shared driver checks. |

## Composing the host

Provider traffic leaves on the host's named pipelines. `GuardedEgressHttpHandler` is the SSRF guard on each
one, and the runtime pipeline adds `ReasoningContentRoundTripHandler` and `FinishReasonNormalizingHandler`
outside it:

```csharp
services.AddHttpClient(ProviderHttpPipelines.Runtime, c => c.Timeout = ProviderHttpPipelines.RuntimeTimeout)
    .AddHttpMessageHandler(() => new ReasoningContentRoundTripHandler())
    .AddHttpMessageHandler(() => new FinishReasonNormalizingHandler())
    .ConfigurePrimaryHttpMessageHandler(() => GuardedEgressHttpHandler.Create(allowPrivateEgress));

// How a family reaches those pipelines. It wraps the family's own handlers outside the host's.
services.AddSingleton<IProviderHttpClientFactory, ProviderHttpClientFactory>();

services.AddSingleton(sp => ProviderAddInLoader.Load(
    ProviderAddInDirectories.Resolve(
        configuration[ProviderAddInDirectories.ExternalDirectoryKey],
        environment.ContentRootPath),
    sp.GetServices<IAiProviderDriver>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ProviderAddInLoader))));

services.AddSingleton<IAiProviderDriverRegistry>(sp => new AiProviderRegistry(
[
    .. sp.GetServices<IAiProviderDriver>(),
    .. sp.GetRequiredService<ProviderAddInCatalog>().Drivers,
]));
```

`AiProviderRegistry` throws when two drivers claim one family. The drivers the loader found are therefore
handed to it directly and never registered as `IAiProviderDriver`. Pass the compiled-in drivers to the loader,
as above. An add-in that then claims one of their identities is recorded as a `Duplicate`, and startup
continues.

Then resolve and call:

```csharp
var driver = registry.GetRequired(providerKind);

// Configuration time: validate the target, then verify it and discover models.
if (driver.ValidateProbeTarget(new AiProbeTarget(baseUrl, authMode, hasCredential)) is { } refusal)
{
    return BadRequest(refusal);
}

var verification = await driver.VerifyAsync(endpoint, ct);
var discovery = await driver.DiscoverModelsAsync(endpoint, ct);

// Run time: one client per resolved binding, composed through the decorator stages.
var client = pipeline.Compose(driver.CreateChatClient(endpoint, model, protocolMode), endpoint, model);
var capabilities = driver.GetChatRuntimeCapabilities(endpoint, model, protocolMode);
```

A driver takes two inputs, `ProviderEndpoint` and `ProviderModelDescriptor`, in the same shapes at
configuration time and at run time:

```csharp
var endpoint = new ProviderEndpoint(
    "meisterdev/anthropic",           // the identity key the family declares
    "https://api.anthropic.com/v1",
    "meisterdev/anthropic:XApiKey",   // the credential shape, qualified by the family that declared it
    Secret: apiKey,                   // never logged; ToString() is overridden to elide it
    DefaultHeaders: null,
    DefaultQueryParams: null)
{
    HostContext = connectionContext,  // the host primitives, bound to the connection this endpoint came from
};

var model = new ProviderModelDescriptor(
    Id: configuredModelId,             // host-side id, carried through for correlation only
    RemoteModelId: "claude-sonnet-4-5",
    SupportedProtocolModes: ["Auto", "meisterdev/anthropic:AnthropicMessages"],
    ReasoningContentField: null);
```

`HostContext` is how a family reaches the host: the HTTP client factory, the credential sessions, the leases,
the keyed store, the health signal and the invocation reporter. It is null on an endpoint the host built from
values an operator has typed and not yet stored. A configuration-time probe passes one of those. A family
whose vendor library needs its transport at construction returns `UnreachableClients.ChatClient(name)` in
that case. That client throws on first use.

## The public surface

| Namespace | What lives there |
| --- | --- |
| `AddIns` | `ProviderAddInLoader`, `ProviderAddInDirectories`, `ProviderAddInCatalog`, `LoadedProviderAddIn`, `RejectedProviderAddIn`, `ProviderAddInRejectionCategory`, `ProviderAddInOrigin`, `ProviderAddInLoadContext`, `ProviderAddInSharedAssemblies`, `ProviderAddInNames` |
| `Drivers` | `IAiProviderDriverRegistry`, `AiProviderRegistry`, `AiVocabulary`, `AiVocabularyResolution`, `AiProviderIdentityResolution`, `AiProviderLegacyNames` |
| `Hosting` | `ProviderHttpClientFactory`, `ProviderHttpPipelines`, `ProviderProbeContext` |
| `Egress` | `GuardedEgressHttpHandler`, `EgressUrlPolicy`, `DeclaredUrlFloor`, `ProviderHostPattern`, `ProviderBrowserUrlPolicy` |
| `Runtime` | `ProviderRuntimePipeline`, `ProviderRuntimeStage`, `IProviderChatClientDecorator`, `NormalizedUsageChatClientDecorator`, `ReasoningModelSamplingDecorator` |
| `Resilience` | `ProviderRetryPolicy`, `ProviderRetryChatClient(Decorator)`, `ProviderRetryEmbeddingGenerator`, `ProviderPacingChatClient(Decorator)`, `ProviderThrottleGate`, `ProviderCallFailedException` |
| `Transport` | `ReasoningContentRoundTripHandler`, `FinishReasonNormalizingHandler` |
| `Catalog` | `BundledCatalogSnapshot`, `ICatalogSnapshotImporter`, `ModelsDevCatalogSnapshotImporter`, `ProviderCatalogEntry` |
| `Contracts` | `ProviderSecretEnvelope`, `ProviderCallTarget` |

Both assemblies put their types under the root namespace `MeisterDev.Ai.Providers`, so the namespace does not
say which one a type came from. The driver contract itself — `IAiProviderDriver`, `ProviderEndpoint`,
`ProviderModelDescriptor`, `ProviderDeclaration`, the host primitive interfaces, the enums,
`AiProtocolModeSupport`, `DriverFailureMapper`, `UnreachableClients`, `ProviderTokenUsage` and
`SecretSafeRendering` — is in `MeisterDev.Ai.Providers.Abstractions`.

### A family is its identity key, and its shapes are qualified by that key

A provider family's identity is the key it declares, such as `meisterdev/anthropic`, and it is a `string`
everywhere it is held, stored or reported. The registry is the authority on which identities exist: ask
`RegisteredKinds` before offering a family to an operator.

The credential and wire shapes are strings too, and a shape a family owns persists as its key joined to the
mode name: `meisterdev/anthropic:XApiKey`. Two families may spell a mode the same way and mean two different
things. The qualifier keeps them apart, so a shape is compared over its whole value and never over the mode
name alone. Two wire shapes are host-reserved and carry no qualifier: `Auto`, which leaves the format to
the driver, and `Embeddings`. A family states which of those it honours and cannot declare a new one.

The set of valid shapes is what the loaded families declared, so ask `SupportedProtocolModes` and
`SupportedAuthModes` before offering one. Without that check an operator can store a profile that fails only
when a workload runs.

`AiVocabulary` and `AiProviderLegacyNames` read a stored identity or shape back against the loaded families,
including the spellings each family supersedes, and return an `AiVocabularyResolution` or an
`AiProviderIdentityResolution` saying what the stored value resolved to.

### Capabilities describe the client that was built

`GetChatRuntimeCapabilities` reports what the provider *and the protocol it was bound to* can do:
provider-managed sessions, background responses, prompt caching, cache routing. A driver claims a capability
only when the client it just built exercises it. Reaching Claude through an OpenAI-compatible proxy loses
cache-control breakpoints, so that route reports `SupportsPromptCaching` as false.

### Failure classification belongs to the driver

`IAiProviderDriver.ClassifyRuntimeFailure` has a default implementation that reads the HTTP status, and is
right for anything speaking HTTP with conventional codes. A driver overrides it to recognise SDK-specific
signals *first*. Call `DriverFailureMapper.ClassifyRuntimeFailure` for everything the override does not
recognise, because a driver that classifies everything itself gets the common cases wrong. Anthropic's
`529 overloaded` and the AWS SDK's own throttling exception types are the two cases in the shipped add-ins
today.

### The runtime pipeline has a fixed stage order

`ProviderRuntimePipeline` composes decorators by the stage each one declares, so registration order does not
affect the result:

```
Retry (outermost) → Pacing → Observability → Budget → Normalization → the driver's client → wire
```

Retry is outermost, so a metering decorator counts each attempt once. Pacing holds a call back while
`ProviderThrottleGate` is closed for the connection, so a retry waits out the window the provider stated.
Observability sees each attempt separately and records a budget refusal against the attempt that caused it.
Normalization applies to retried attempts as well.

## Extension points a host fills in

| Seam | Why the host owns it |
| --- | --- |
| `IProviderChatClientDecorator` | Cost accounting, entitlement and telemetry belong to the application, not to the transport. The host contributes a decorator at the stage it belongs to. The library ships the retry, pacing, sampling and usage-normalization decorators. |
| `IProviderConnectionContext` and the primitives it exposes | The HTTP pipelines, credential sessions, leases, keyed store, health signal and invocation reporter are all bound to one stored connection, which the host owns. The host attaches its `IProviderConnectionContext` to `ProviderEndpoint.HostContext`. |
| `ProviderSecretEnvelope` | A credential store sees one opaque blob, and this type decodes it. A family may declare several fields, or one holding a JSON document, so a bare string does not fit. Decoding also accepts a bare string, because rows written before the envelope contain one. |
| `ICatalogSnapshotImporter` | The library carries the embedded models.dev snapshot and parses it. The host persists it. |
| `EgressUrlPolicy` and `allowPrivateEgress` | What an installation permits an operator-entered address to reach is a deployment decision. `DeclaredUrlFloor` applies underneath it to every address a family declared. |

## Adding a provider family

[The contract's README](../MeisterDev.Ai.Providers.Abstractions/README.md) covers implementing
`IAiProviderDriver`, the three packaging settings, and running the driver checks in your own build. Two steps
are the host's:

- Drop the published folder in one of the two add-in directories. The loader takes it on the next start.
- Add a `ProviderRuntimeStage` decorator where the family needs behaviour the pipeline does not have.

## Tests

`tests/MeisterDev.Ai.Providers.Tests` covers the add-in loader and its rejection categories, the registry and
the vocabulary resolution, the egress policies, the host's HTTP pipelines, retry and pacing mechanics on a
controlled `TimeProvider`, usage normalization, the secret envelope, the runtime pipeline's stage order, the
conformance kit, and the isolation rules above. Each family has its own suite beside its add-in, in
`tests/MeisterDev.Ai.Providers.<Family>AddIn.Tests`.
