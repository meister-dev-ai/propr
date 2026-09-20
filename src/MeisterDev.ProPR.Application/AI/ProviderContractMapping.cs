// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Projects the product's stored AI configuration onto the provider library's contract. This is the whole
///     adapter boundary: the library never sees a persistence or API type, and the product keeps its own shapes
///     with the extra provenance and accounting metadata a driver has no use for.
/// </summary>
public static class ProviderContractMapping
{
    /// <summary>Projects a stored connection profile onto the endpoint a driver needs to reach it.</summary>
    /// <remarks>
    ///     The host primitives ride on the endpoint because they are bound to one connection and a driver serves
    ///     every connection of its family from one instance: a handle taken when the driver was constructed would
    ///     have to be told which connection each call is for, and a family naming its own connection is what the
    ///     whole primitive set is shaped to prevent.
    /// </remarks>
    /// <param name="connection">The stored connection profile.</param>
    /// <param name="hostContext">
    ///     What the host allows the family to do against this connection, or null where the caller has no access
    ///     to build one.
    /// </param>
    public static ProviderEndpoint ToProviderEndpoint(
        this AiConnectionDto connection,
        IProviderConnectionContext? hostContext = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new ProviderEndpoint(
            connection.ProviderKind,
            connection.BaseUrl,
            connection.AuthMode,
            connection.Secret,
            connection.DefaultHeaders,
            connection.DefaultQueryParams)
        {
            HostContext = hostContext,

            // The family declared one set of fields and reads back one set of values. Which of them the host
            // kept in the protected credential store, which in the settings document and which in the
            // credential envelope is the host's decision, and putting them back together here is what keeps
            // that decision out of the family.
            DeclaredValues = AiConnectionDto.MergeDeclaredValues(
                connection.ProviderSettings,
                connection.DeclaredSecrets,
                connection.Secret,
                connection.AuthMode),
        };
    }

    /// <summary>Projects probe options onto the same endpoint shape used at run time.</summary>
    /// <remarks>
    ///     The probe context carries the transport and nothing else. Discovery and verification are calls the
    ///     family makes to the endpoint the operator entered, and a family reaches the network only through the
    ///     client factory the host supplies, so an endpoint built here without one is one no family can probe.
    /// </remarks>
    /// <param name="options">The probe options supplied by an operator.</param>
    /// <param name="hostContext">
    ///     The transport the probe leaves through, or null where the caller has no access to build one.
    /// </param>
    /// <param name="declaredValues">
    ///     The family's declared settings as the form holds them, which the saved connection would carry.
    ///     A family whose verification reads one of them is otherwise probed against a configuration the
    ///     connection will not have, and reports a failure the saved profile would not produce.
    /// </param>
    public static ProviderEndpoint ToProviderEndpoint(
        this AiConnectionProbeOptionsDto options,
        IProviderConnectionContext? hostContext = null,
        IReadOnlyDictionary<string, string>? declaredValues = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ProviderEndpoint(
            options.ProviderKind,
            options.BaseUrl,
            options.AuthMode,
            options.Secret,
            options.DefaultHeaders,
            options.DefaultQueryParams)
        {
            HostContext = hostContext,

            // The credential goes in the same way it does for a saved connection. A probe is where a family is
            // first asked whether the values an operator typed work, and a family whose credential is several
            // fields would otherwise be probed without one and report a failure the saved profile will not have.
            DeclaredValues = AiConnectionDto.MergeDeclaredValues(
                declaredValues,
                new Dictionary<string, string>(StringComparer.Ordinal),
                options.Secret,
                options.AuthMode),
        };
    }

    /// <summary>Reduces a configured model to what a driver addresses it by.</summary>
    /// <param name="model">The configured model.</param>
    public static ProviderModelDescriptor ToProviderModel(this AiConfiguredModelDto model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new ProviderModelDescriptor(
            model.Id,
            model.RemoteModelId,
            model.SupportedProtocolModes,
            model.ReasoningContentField,
            model.SupportsPromptCaching,
            model.SupportsReasoning);
    }

    /// <summary>Adopts a driver's verification outcome as the product's own snapshot.</summary>
    /// <param name="result">The verification outcome reported by the driver.</param>
    public static AiVerificationResultDto ToDto(this ProviderVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AiVerificationResultDto(
            result.Status,
            result.FailureCategory,
            result.Summary,
            result.ActionHint,
            result.CheckedAt,
            result.Warnings,
            result.DriverMetadata);
    }

    /// <summary>
    ///     Adopts a driver's discovery outcome, stamping the provenance the provider cannot know: that each entry
    ///     was discovered rather than hand-entered, and when it was last seen.
    /// </summary>
    /// <param name="result">The discovery outcome reported by the driver.</param>
    /// <param name="discoveredAt">When the discovery ran.</param>
    public static AiModelDiscoveryResultDto ToDto(this ProviderModelDiscoveryResult result, DateTimeOffset discoveredAt)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AiModelDiscoveryResultDto(
            result.DiscoveryStatus,
            result.ManualEntryAllowed,
            result.Warnings,
            result.Models.Select(model => model.ToDto(discoveredAt)).ToList());
    }

    private static AiConfiguredModelDto ToDto(this ProviderDiscoveredModel model, DateTimeOffset discoveredAt)
    {
        return new AiConfiguredModelDto(
            Guid.Empty,
            model.RemoteModelId,
            model.DisplayName,
            model.OperationKinds,
            model.SupportedProtocolModes,
            model.TokenizerName,
            model.MaxInputTokens,
            model.EmbeddingDimensions,
            model.SupportsStructuredOutput,
            model.SupportsToolUse,
            AiConfiguredModelSource.Discovered,
            discoveredAt,
            MaxContextTokens: model.MaxContextTokens);
    }
}
