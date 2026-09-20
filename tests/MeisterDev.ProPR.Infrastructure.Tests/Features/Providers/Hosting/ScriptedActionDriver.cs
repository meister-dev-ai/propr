// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     A provider family whose declared action does whatever a test tells it to.
/// </summary>
/// <remarks>
///     The dispatch path's behaviour depends on what a family does — answer, ask for values, throw, or never
///     return — and none of those can be arranged through a real family. This one is handed the behaviour, so
///     each case is one test rather than a family written to fail in a particular way.
/// </remarks>
public sealed class ScriptedActionDriver : IAiProviderDriver, IAiProviderActions
{
    /// <summary>The action every test dispatches.</summary>
    public const string ActionId = "connect";

    /// <summary>The value the action collects on its second call.</summary>
    public const string CallbackInput = "callbackUrl";

    private readonly Func<ProviderEndpoint, IReadOnlyDictionary<string, string>, IProviderActionContext,
        Task<ProviderActionResult>> _behaviour;

    private ScriptedActionDriver(
        ProviderDeclaration declaration,
        Func<ProviderEndpoint, IReadOnlyDictionary<string, string>, IProviderActionContext,
            Task<ProviderActionResult>> behaviour)
    {
        this.Declaration = declaration;
        this._behaviour = behaviour;
    }

    /// <inheritdoc />
    public ProviderDeclaration Declaration { get; }

    /// <inheritdoc />
    /// <summary>The connections the action was invoked against, in order.</summary>
    public List<ProviderEndpoint> Invoked { get; } = [];

    /// <summary>Builds a family whose action runs <paramref name="behaviour" />.</summary>
    /// <param name="behaviour">What the action does.</param>
    /// <param name="declaration">What the family declares, or null for the default declaration.</param>
    public static ScriptedActionDriver Doing(
        Func<ProviderEndpoint, IReadOnlyDictionary<string, string>, IProviderActionContext,
            Task<ProviderActionResult>> behaviour,
        ProviderDeclaration? declaration = null)
    {
        return new ScriptedActionDriver(declaration ?? Declaring(), behaviour);
    }

    /// <summary>Builds a family whose action answers <paramref name="result" /> at once.</summary>
    /// <param name="result">What the action answers.</param>
    /// <param name="declaration">What the family declares, or null for the default declaration.</param>
    public static ScriptedActionDriver Answering(
        ProviderActionResult result,
        ProviderDeclaration? declaration = null)
    {
        return Doing((_, _, _) => Task.FromResult(result), declaration);
    }

    /// <summary>A declaration a test can vary one member of.</summary>
    /// <param name="actions">The actions declared, or null for the single default action.</param>
    /// <param name="capabilityKey">The premium capability required, if any.</param>
    /// <param name="window">How long a run may stay open, or null for the host's own bound.</param>
    /// <param name="coLocated">Whether the family needs the host and the browser on one machine.</param>
    /// <param name="opensListener">Whether an action of the family opens a socket.</param>
    /// <param name="fields">The connection configuration declared, or null for a listener port.</param>
    public static ProviderDeclaration Declaring(
        IReadOnlyList<ProviderDeclaredAction>? actions = null,
        string? capabilityKey = null,
        ProviderInvocationWindow? window = null,
        bool coLocated = false,
        bool opensListener = false,
        IReadOnlyList<ProviderDeclaredField>? fields = null)
    {
        return new ProviderDeclaration
        {
            Key = "example/provider",
            Label = "Example provider",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode("example/provider:ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs("example/provider:ApiKey"),
            ReachedHostPatterns = ["auth.example.com"],
            RequiredCapabilityKey = capabilityKey,
            RequiresBrowserCoLocation = coLocated,
            OpensListener = opensListener,
            InvocationWindow = window,
            Fields = fields ??
            [
                new ProviderDeclaredField("listenerPort", "Callback port", ProviderFieldKind.Int)
                {
                    DefaultValue = "1455",
                },
            ],
            Actions = actions ??
            [
                new ProviderDeclaredAction(
                    ActionId,
                    "Connect account",
                    [
                        new ProviderDeclaredField(CallbackInput, "Callback URL", ProviderFieldKind.String)
                        {
                            Scope = ProviderFieldScope.ActionInput,
                        },
                    ]),
            ],
        };
    }

    /// <inheritdoc />
    public Task<ProviderActionResult> InvokeAsync(
        ProviderEndpoint endpoint,
        string actionId,
        IReadOnlyDictionary<string, string> inputs,
        IProviderActionContext context)
    {
        this.Invoked.Add(endpoint);

        return this._behaviour(endpoint, inputs, context);
    }

    /// <inheritdoc />
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        return null;
    }

    /// <inheritdoc />
    public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ProviderModelDiscoveryResult("succeeded", true, [], []));
    }

    /// <inheritdoc />
    public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        return Task.FromResult(DriverFailureMapper.Verified("Verified."));
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        throw new NotSupportedException("This family exists for its action and serves no model.");
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
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode,
        int dimensions)
    {
        throw new NotSupportedException("This family exists for its action and serves no model.");
    }
}
