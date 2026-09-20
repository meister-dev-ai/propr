// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.ExampleAddIn;
using MeisterDev.Ai.Providers.Hosting;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     Which of a family's declared actions one connection is offered.
/// </summary>
/// <remarks>
///     The member carries a default so that adding it left every family built against the previous contract
///     offering what it always did. A family whose credential is written by its own sign-in overrides it, because
///     the sign-in and the disconnect are the same declaration and apply at different times.
/// </remarks>
public sealed class ProviderOfferedActionTests
{
    private static readonly ProviderConnectionState Connected = new(
        true,
        AiVerificationStatus.Verified,
        AiCredentialHealth.Healthy);

    // The add-in written against the contract alone, which states nothing about when its action applies. Asked
    // through the interface, which is how the host asks: the default is an interface member and a family that
    // does not state one has nothing on its own type.
    [Fact]
    public void AFamilyThatStatesNothingOffersEveryActionItDeclares()
    {
        var family = new ExampleProviderDriver();
        IAiProviderActions asked = family;

        Assert.Equal(
            family.Declaration.Actions.Select(action => action.Id),
            asked.OfferedActionIds(Connected));

        // Same answer for a connection in the opposite state: a family that states nothing is not reading the
        // state at all.
        Assert.Equal(
            family.Declaration.Actions.Select(action => action.Id),
            asked.OfferedActionIds(ProviderConnectionState.Unconnected));
    }

    [Fact]
    public void AFamilyThatReadsTheStateOffersTheSubsetItChose()
    {
        IAiProviderActions family = new CredentialFlowDriver();

        Assert.Equal(["connect"], family.OfferedActionIds(ProviderConnectionState.Unconnected));
        Assert.Equal(["disconnect"], family.OfferedActionIds(Connected));
    }

    [Fact]
    public void AFamilyMayOfferNoneOfWhatItDeclares()
    {
        IAiProviderActions family = new CredentialFlowDriver();

        Assert.Empty(family.OfferedActionIds(new ProviderConnectionState(true, AiVerificationStatus.Failed, AiCredentialHealth.Disabled)));
    }

    /// <summary>A family whose credential is written by its own flow, so its two actions apply at different times.</summary>
    private sealed class CredentialFlowDriver : IAiProviderDriver, IAiProviderActions
    {
        public ProviderDeclaration Declaration { get; } = new()
        {
            Key = "example/credentialFlow",
            Label = "Credential flow",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes =
            [
                new ProviderDeclaredAuthMode(
                    "example/credentialFlow:Grant",
                    [AiCredentialFieldSupport.ApiKey]),
            ],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs("example/credentialFlow:Grant"),
            Actions =
            [
                new ProviderDeclaredAction("connect", "Connect account", []),
                new ProviderDeclaredAction("disconnect", "Disconnect account", []),
            ],
        };

        public IReadOnlyList<string> OfferedActionIds(ProviderConnectionState connection)
        {
            if (connection.CredentialHealth == AiCredentialHealth.Disabled)
            {
                return [];
            }

            return connection.HasStoredCredential ? ["disconnect"] : ["connect"];
        }

        public Task<ProviderActionResult> InvokeAsync(
            ProviderEndpoint endpoint,
            string actionId,
            IReadOnlyDictionary<string, string> inputs,
            IProviderActionContext context)
        {
            return Task.FromResult(ProviderActionResult.Completed("Done."));
        }

        public string? ValidateProbeTarget(AiProbeTarget target)
        {
            return null;
        }

        public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
            ProviderEndpoint endpoint,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ProviderModelDiscoveryResult("succeeded", true, [], []));
        }

        public Task<ProviderVerificationResult> VerifyAsync(
            ProviderEndpoint endpoint,
            CancellationToken ct = default)
        {
            return Task.FromResult(DriverFailureMapper.Verified("Verified."));
        }

        public IChatClient CreateChatClient(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode)
        {
            throw new NotSupportedException("This family exists for its actions and serves no model.");
        }

        public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode)
        {
            return ProviderRuntimeCapabilities.None;
        }

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode,
            int dimensions)
        {
            throw new NotSupportedException("This family exists for its actions and serves no model.");
        }
    }
}
