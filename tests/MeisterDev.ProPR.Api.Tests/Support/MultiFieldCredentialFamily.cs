// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Api.Tests.Support;

/// <summary>
///     A family whose credential is several named fields, registered only by the test host.
/// </summary>
/// <remarks>
///     The behaviour it stands for belongs to the host and not to any one family: a field the declaration does
///     not name is refused, a required field left empty is refused naming it, and a credential of several fields
///     survives an update that re-enters none of them. Every shipped family reads a single key, so without a
///     subject here those rules would be asserted through whichever family happened to declare more than one
///     field, and would fall silent the moment that family changed its mind. Declared rather than loaded, so the
///     registry composes it directly and the conformance kit — which gates what the loader finds on disk — has
///     no say over a fixture.
/// </remarks>
public sealed class MultiFieldCredentialFamily : IAiProviderDriver
{
    /// <summary>The family key, outside the shipped namespace so it cannot collide with a real one.</summary>
    public const string FamilyKey = "meisterdevtests/multiField";

    /// <summary>The one credential shape this family declares.</summary>
    public const string CredentialAuth = FamilyKey + ":Credential";

    /// <summary>The wire shape this family declares.</summary>
    public const string Protocol = FamilyKey + ":Plain";

    /// <summary>The field naming the credential, which identifies rather than authenticates and is not masked.</summary>
    public const string PrincipalField = "principal";

    /// <summary>The secret half of the credential.</summary>
    public const string SecretField = "secret";

    /// <summary>An optional third part, supplied only for a scoped credential.</summary>
    public const string ScopeField = "scope";

    /// <summary>A base URL this family accepts, for a test that has to name one.</summary>
    public const string AcceptableBaseUrl = "https://multi-field.example.com/v1";

    private static readonly ProviderDeclaration Declared = new()
    {
        Key = FamilyKey,
        Label = "Multi-field credential (test)",
        Version = "1.0",
        ContractVersion = ProviderContract.Version,
        LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(["MultiField"], [CredentialAuth], [Protocol]),
        AuthModes =
        [
            new ProviderDeclaredAuthMode(
                CredentialAuth,
                [
                    new ProviderCredentialField(PrincipalField, "Principal", IsSecret: false),
                    new ProviderCredentialField(SecretField, "Secret"),
                    new ProviderCredentialField(ScopeField, "Scope", IsRequired: false),
                ])
            {
                Label = "Credential",
            },
        ],
        ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, Protocol]),
        ConnectionForm = new ProviderConnectionForm(
            NamePlaceholder: "Multi-field (test)",
            BaseUrlPlaceholder: AcceptableBaseUrl),
        ReachedHostPatterns = [],
        ConformanceInputs = new ProviderConformanceInputs(CredentialAuth),
    };

    /// <inheritdoc />
    public ProviderDeclaration Declaration => Declared;

    /// <inheritdoc />
    public string? ValidateProbeTarget(AiProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return ProbeTargetChecks.AbsoluteUrl(target, out _);
    }

    /// <inheritdoc />
    public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ProviderModelDiscoveryResult("Unsupported", ManualEntryAllowed: true, [], []));
    }

    /// <inheritdoc />
    public Task<ProviderVerificationResult> VerifyAsync(ProviderEndpoint endpoint, CancellationToken ct = default)
    {
        return Task.FromResult(new ProviderVerificationResult(AiVerificationStatus.Verified));
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
        throw new NotSupportedException("The multi-field fixture is configured, never called.");
    }

    /// <inheritdoc />
    public IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode)
    {
        throw new NotSupportedException("The multi-field fixture is configured, never called.");
    }
}
