// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.ExampleAddIn;

/// <summary>
///     A driver written against the contract assembly alone, with no reference to the shared runtime or to any
///     host project.
/// </summary>
/// <remarks>
///     It answers for an existing family rather than a new one, because the identity axis is still the closed
///     provider enum and an add-in cannot name a family the enum does not carry. What it proves is the contract's
///     sufficiency: every type these members name, and every helper the implementation reaches for, is reachable
///     from the contract assembly.
/// </remarks>
public sealed class ExampleProviderDriver : IAiProviderDriver, IAiProviderActions
{
    /// <summary>The action an operator starts to connect an account.</summary>
    public const string ConnectActionId = "connect";

    /// <summary>The input the paste fallback collects.</summary>
    public const string CallbackUrlInput = "callbackUrl";

    /// <summary>The name this family's vendor reports its cache-write bucket under.</summary>
    private const string CacheWriteCountName = "example_cache_creation_tokens";

    /// <summary>
    ///     A complete declaration built against the contract assembly alone, exercising every member of it: the
    ///     ones a family with a credential flow needs as well as the ones the shipped families leave empty.
    /// </summary>
    /// <summary>The identity key every connection of this family is stored against.</summary>
    public const string FamilyKey = "example/provider";

    /// <summary>The one wire shape this family owns.</summary>
    public const string ChatCompletionsProtocol = FamilyKey + ":ChatCompletions";

    /// <summary>The one credential shape this family authenticates with.</summary>
    public const string ApiKeyAuth = FamilyKey + ":ApiKey";

    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,
        Label = "Example provider",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        LegacyNames = ProviderLegacyNames.Create(
            ["ExampleProvider"],
            new Dictionary<string, string> { [ApiKeyAuth] = "ApiKey" },
            new Dictionary<string, string> { [ChatCompletionsProtocol] = "ChatCompletions" }),
        Fields =
        [
            new ProviderDeclaredField("baseUrl", "Base URL", ProviderFieldKind.Url)
            {
                IsRequired = true,
                Hint = "Where this provider is reached.",
                Placeholder = "https://api.example.com/v1",
            },
            new ProviderDeclaredField("listenerPort", "Callback port", ProviderFieldKind.Int)
            {
                DefaultValue = "1455",
                Hint = "The port the vendor registered for its redirect.",
            },
            new ProviderDeclaredField("redirectUri", "Redirect URI", ProviderFieldKind.Url)
            {
                IsComputed = true,
                Hint = "Register this address with the provider.",
                VisibleWhen = new ProviderFieldVisibility("mode", "subscription"),
            },
            new ProviderDeclaredField("mode", "Account type", ProviderFieldKind.Choice)
            {
                Choices = ["apiKey", "subscription"],
                DefaultValue = "apiKey",
            },
            new ProviderDeclaredField("callbackUrl", "Callback URL", ProviderFieldKind.String)
            {
                Scope = ProviderFieldScope.ActionInput,
                Hint = "Paste the whole URL the provider redirected to.",
                AcceptsRedirectAddress = true,
            },
        ],

        // Named, so the notice an operator reads before starting the action states this port and not whichever
        // whole-number setting the family happens to declare.
        ListenerPortFieldNames = ["listenerPort"],
        RequestShapeDefaults = new ProviderRequestShapeDefaults(
            AcceptsTemperature: false,
            RefusedParameterNames: new Dictionary<string, ProviderRequestShapeConstraint>(StringComparer.Ordinal)
            {
                ["temperature"] = ProviderRequestShapeConstraint.Temperature,
            }),
        Actions =
        [
            new ProviderDeclaredAction(
                ConnectActionId,
                "Connect account",
                [
                    new ProviderDeclaredField(CallbackUrlInput, "Callback URL", ProviderFieldKind.String)
                    {
                        Scope = ProviderFieldScope.ActionInput,
                        Hint = "Paste the whole address the provider redirected to.",
                        AcceptsRedirectAddress = true,
                    },
                ]),
        ],
        AuthModes = [new ProviderDeclaredAuthMode(ApiKeyAuth, [AiCredentialFieldSupport.ApiKey])],

        // Chat only. The OpenAI-compatible set also carries Embeddings, and declaring a shape the family cannot
        // build a client for offers an operator a binding that fails on its first call.
        ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, ChatCompletionsProtocol]),
        RequiresBrowserCoLocation = true,
        ReachedHostPatterns = ["api.example.com", ".example.com"],
        RequiredCapabilityKey = "example-connections",
        ConformanceInputs = new ProviderConformanceInputs(
            ApiKeyAuth,

            // What this family's vendor reports for a call served largely from cache: a prompt count that
            // excludes both cache buckets, and a cache-write bucket under the vendor's own name. Recorded in the
            // shape the family's client hands the counts over in, because that is what its mapping reads.
            "{\"inputTokenCount\":120,\"outputTokenCount\":207,"
            + "\"cachedInputTokenCount\":4000,\"reasoningTokenCount\":200,"
            + "\"additionalCounts\":{\"example_cache_creation_tokens\":50}}"),
        InvocationWindow = new ProviderInvocationWindow(TimeSpan.FromMinutes(10), "listenerPort"),
        HasCredentialHealth = true,
        OpensListener = true,
    };

    /// <summary>
    ///     The sign-ins this family has started and not finished, by the opaque state the vendor hands back.
    /// </summary>
    /// <remarks>
    ///     A flow spans two calls: the one that sends the operator to the vendor, and the one that arrives with
    ///     what the vendor answered. The listener port is held across both, so the object holding it lives here
    ///     in between. A flow the operator abandons is never claimed, and what it holds lapses on its own
    ///     expiry — the lease and the stored handshake both carry one.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExampleConnectAction> _started =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ProviderDeclaration Declaration => Declared;


    /// <inheritdoc />
    public IReadOnlyList<string> SupportedProtocolModes => Declared.ProtocolModes.Supported;

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedAuthModes => Declared.SupportedAuthModes;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields =>
        Declared.CredentialFields;

    /// <inheritdoc />
    /// <remarks>
    ///     This is where a family refuses a target at configuration time, and the refusal reaches an operator
    ///     while the form is still open. The address is checked as well as the credential: an endpoint is
    ///     operator-supplied text, so a family that accepted any of it would turn its own probe into a way to
    ///     reach whatever the host can reach. The host's connect-time egress guard sits behind this and cannot be
    ///     opted out of, but it can only refuse the call once a review makes it.
    ///     <para>
    ///         The rule here is the host patterns this family declared it reaches, which is the strongest form
    ///         and covers plain http, a loopback address and a metadata endpoint in one check. A family whose
    ///         endpoint carries an operator's own hostname cannot enumerate its hosts and refuses a private,
    ///         loopback or link-local address explicitly instead.
    ///     </para>
    /// </remarks>
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var uri))
        {
            return "baseUrl must be an absolute URL.";
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "baseUrl must use https.";
        }

        if (!ReachesHost(uri.Host))
        {
            return $"the example provider is reached at {string.Join(" or ", Declared.ReachedHostPatterns)}.";
        }

        return target.HasApiKey && ProviderVocabulary.ValuesEqual(target.AuthMode, ApiKeyAuth)
            ? null
            : "the example provider needs an API key.";
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The family's own rules over what an operator entered, above the host's. The port this family binds has
    ///     to be one the operating system lets an unprivileged process take, which the host has no way to know and
    ///     which the declared shape of an integer does not state.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ValidateDeclaredValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var refusals = new Dictionary<string, string>(StringComparer.Ordinal);

        if (values.TryGetValue("listenerPort", out var port)
            && (!int.TryParse(port, out var parsed) || parsed is < 1024 or > 65535))
        {
            refusals["listenerPort"] = "The callback port is a number between 1024 and 65535.";
        }

        return refusals;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The redirect address the provider is told to send the operator back to, composed from the port on this
    ///     connection. It is computed rather than stored because it stops matching the moment the port changes,
    ///     and an operator cannot correct a stored copy of a value they do not set.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ComputeDeclaredValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var port = values.TryGetValue("listenerPort", out var declared) && !string.IsNullOrWhiteSpace(declared)
            ? declared
            : "1455";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["redirectUri"] = $"http://127.0.0.1:{port}/auth/callback",
        };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A real family calls its endpoint here. This one reports one model without a call, and still reads the
    ///     endpoint, because the model identifiers a provider exposes belong to the resource the endpoint names
    ///     and a discovery that ignored it would report the same models for every connection.
    /// </remarks>
    public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return Task.FromResult(
            new ProviderModelDiscoveryResult(
                "succeeded",
                true,
                [],
                [
                    new ProviderDiscoveredModel(
                        "example-model",
                        $"Example model at {new Uri(endpoint.BaseUrl, UriKind.Absolute).Host}",
                        [AiOperationKind.Chat],
                        [ProviderDeclaredProtocolModes.Auto, ChatCompletionsProtocol]),
                ]));
    }

    /// <inheritdoc />
    public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return Task.FromResult(DriverFailureMapper.Verified($"Verified the example provider at '{endpoint.BaseUrl}'."));
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);
        AiProtocolModeSupport.Require(this.Declaration.Key, this.SupportedProtocolModes, protocolMode);

        return new ExampleChatClient(new Uri(endpoint.BaseUrl, UriKind.Absolute), model.RemoteModelId);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     What a family whose vendor reports exclusive counts has to do. This vendor reports a prompt count that
    ///     excludes both cache buckets, names its cache-write bucket itself, and reports thinking tokens inside
    ///     the completion count, so the buckets are added into the input total here and the output total is left
    ///     alone. Returning the vendor's counts unchanged would floor the billed prompt at zero, and the
    ///     conformance kit replays this family's recorded payload through this member and refuses that.
    /// </remarks>
    public ProviderTokenUsage ReadUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return ProviderTokenUsage.Missing;
        }

        var cacheRead = usage.CachedInputTokenCount ?? 0;
        var cacheWrite = usage.AdditionalCounts?.TryGetValue(CacheWriteCountName, out var written) == true ? written : 0;

        return new ProviderTokenUsage(
            (usage.InputTokenCount ?? 0) + cacheRead + cacheWrite,
            usage.OutputTokenCount ?? 0,
            cacheRead,
            cacheWrite,
            usage.ReasoningTokenCount ?? 0);
    }

    /// <inheritdoc />
    public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        return ProviderRuntimeCapabilities.None;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The family declares no embedding shape, so the shared check refuses the call with the family and the
    ///     shape named, which is the same refusal an operator would have met at configuration time. A family that
    ///     served embeddings would declare the shape and build the generator here.
    /// </remarks>
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode,
        int dimensions)
    {
        AiProtocolModeSupport.Require(this.Declaration.Key, this.SupportedProtocolModes, protocolMode);

        throw new InvalidOperationException("The example provider serves no embedding models.");
    }

    /// <inheritdoc />
    /// <remarks>
    ///     One action with two branches, which is the shape a credential flow takes. The first call takes the
    ///     callback port, keeps the handshake and sends the operator to the vendor. The second arrives with the
    ///     address the vendor redirected to, claims the handshake, exchanges the code and stores what came back.
    ///     Both branches run against the invocation the host opened, so the port, the handshake and the
    ///     administrator who started the flow stay attached to one record.
    /// </remarks>
    public async Task<ProviderActionResult> InvokeAsync(
        ProviderEndpoint endpoint,
        string actionId,
        IReadOnlyDictionary<string, string> inputs,
        IProviderActionContext context)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(actionId, ConnectActionId, StringComparison.Ordinal))
        {
            return ProviderActionResult.Failed($"The example provider has no '{actionId}' action.");
        }

        return inputs.TryGetValue(CallbackUrlInput, out var callback) && !string.IsNullOrWhiteSpace(callback)
            ? await this.FinishAsync(context, callback)
            : await this.StartAsync(context);
    }

    /// <summary>Sends the operator to the vendor, holding the callback port until the answer arrives.</summary>
    /// <param name="context">What the host allows against this connection and this invocation.</param>
    private async Task<ProviderActionResult> StartAsync(IProviderActionContext context)
    {
        // Opaque and unguessable, because it is what the vendor hands back and what the handshake is claimed by.
        var state = Guid.NewGuid().ToString("N");
        var started = new ExampleConnectAction();

        var result = await started.StartAsync(context, state);
        if (result is ProviderActionOpenUrl)
        {
            this._started[state] = started;
            return result;
        }

        // The port was taken and nothing is going to arrive on it, so the next connection of this family can
        // have it back now.
        await started.DisposeAsync();
        return result;
    }

    /// <summary>Finishes a sign-in from the address the vendor redirected the operator to.</summary>
    /// <param name="context">What the host allows against this connection and this invocation.</param>
    /// <param name="callbackUrl">The address as the operator pasted it.</param>
    private async Task<ProviderActionResult> FinishAsync(IProviderActionContext context, string callbackUrl)
    {
        if (!Uri.TryCreate(callbackUrl.Trim(), UriKind.Absolute, out var redirected))
        {
            return ProviderActionResult.Failed("Paste the whole address the provider redirected to.");
        }

        var query = ReadQuery(redirected);
        var state = query.GetValueOrDefault("state");
        var code = query.GetValueOrDefault("code");

        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code))
        {
            return ProviderActionResult.Failed("That address carries no sign-in result. Paste the whole address the provider redirected to.");
        }

        if (!this._started.TryRemove(state, out var started))
        {
            return ProviderActionResult.Failed("That sign-in did not start here, or it has already finished.");
        }

        await using (started)
        {
            return await started.CompleteAsync(context, state, code);
        }
    }

    /// <summary>The query parameters of a redirect address, unescaped.</summary>
    /// <remarks>
    ///     Read here rather than with a framework helper, because an add-in compiles against the contract
    ///     assembly and the shapes the contract carries, and nothing else is guaranteed to be there.
    /// </remarks>
    /// <param name="redirected">The address the vendor redirected to.</param>
    private static Dictionary<string, string> ReadQuery(Uri redirected)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in redirected.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            parameters[Uri.UnescapeDataString(pair[..separator])] =
                Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return parameters;
    }

    /// <summary>Whether a host is one of the patterns this family declared it reaches.</summary>
    /// <param name="host">The host component of the configured URL.</param>
    private static bool ReachesHost(string host)
    {
        var normalized = host.TrimEnd('.');

        return Declared.ReachedHostPatterns.Any(pattern => pattern.StartsWith('.')
            ? normalized.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)
            : string.Equals(normalized, pattern, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Shows the shape the contract documents: recognise what only this family can recognise, then hand the
    ///     rest to the shared rule.
    /// </remarks>
    public ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var candidate in DriverFailureMapper.Unwind(exception))
        {
            if (candidate is InvalidTimeZoneException)
            {
                return ProviderFailureVerdict.Transient("The example provider reported a clock problem.");
            }
        }

        return DriverFailureMapper.ClassifyRuntimeFailure(exception);
    }

    /// <summary>The client one connection's calls go out on, holding what that connection was configured with.</summary>
    /// <param name="endpoint">Where this connection is reached.</param>
    /// <param name="remoteModelId">The model identifier as the provider knows it.</param>
    private sealed class ExampleChatClient(Uri endpoint, string remoteModelId) : INativeProtocolChatClient
    {
        public string NativeProtocol => ChatCompletionsProtocol;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"example from {remoteModelId} at {endpoint.Host}")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "example");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
