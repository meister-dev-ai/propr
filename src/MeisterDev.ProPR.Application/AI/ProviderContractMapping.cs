// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
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
    /// <param name="connection">The stored connection profile.</param>
    public static ProviderEndpoint ToProviderEndpoint(this AiConnectionDto connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new ProviderEndpoint(
            connection.ProviderKind,
            connection.BaseUrl,
            connection.AuthMode,
            connection.Secret,
            connection.DefaultHeaders,
            connection.DefaultQueryParams);
    }

    /// <summary>Projects probe options onto the same endpoint shape used at run time.</summary>
    /// <param name="options">The probe options supplied by an operator.</param>
    public static ProviderEndpoint ToProviderEndpoint(this AiConnectionProbeOptionsDto options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ProviderEndpoint(
            options.ProviderKind,
            options.BaseUrl,
            options.AuthMode,
            options.Secret,
            options.DefaultHeaders,
            options.DefaultQueryParams);
    }

    /// <summary>Reduces a configured model to what a driver addresses it by.</summary>
    /// <param name="model">The configured model.</param>
    public static ProviderModelDescriptor ToProviderModel(this AiConfiguredModelDto model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new ProviderModelDescriptor(model.Id, model.RemoteModelId, model.SupportedProtocolModes);
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
