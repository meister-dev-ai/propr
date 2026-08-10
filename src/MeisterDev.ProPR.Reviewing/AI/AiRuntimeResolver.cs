// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Resolves provider-neutral AI runtimes for chat and embedding purposes: it decides WHICH connection, model,
///     and protocol a purpose maps to, then hands that to <see cref="IAiRuntimeFactory" /> to build. Constructing a
///     runtime -- resolving the driver and composing the per-call decorators around it -- happens only there, so a
///     behaviour added to model calls cannot miss this path.
/// </summary>
public sealed class AiRuntimeResolver(
    IAiConnectionRepository aiConnectionRepository,
    IAiRuntimeFactory runtimeFactory,
    ILogicalModelResolver? logicalModelResolver = null,
    ILogicalModelCatalogRepository? logicalModelCatalog = null,
    ITenantProviderPolicyProvider? providerPolicies = null) : IAiRuntimeResolver
{
    public async Task<IResolvedAiChatRuntime> ResolveChatRuntimeAsync(
        Guid clientId,
        AiPurpose purpose,
        CancellationToken ct = default)
    {
        // When the purpose is mapped to a logical model, resolve through the catalog (connection, model, and
        // protocol come from the role). Otherwise fall back to the client's active AI purpose bindings.
        var roleName = await this.TryGetPurposeRoleAsync(clientId, purpose, ct);
        if (roleName is not null)
        {
            var resolvedRole = await logicalModelResolver!.ResolveChatRuntimeAsync(clientId, roleName, ct: ct);
            return resolvedRole.Runtime;
        }

        var resolved = await aiConnectionRepository.GetActiveBindingForPurposeAsync(clientId, purpose, ct);

        if (resolved is null)
        {
            // Last resort, and only once both lookups above have come up empty: try the purpose's fallback chain
            // against the logical-model mapping. A client that configures models exclusively as logical models has
            // no connection binding for the chain above to walk, so a purpose it has not mapped would otherwise
            // resolve to nothing at all, which is how a newly introduced purpose ends up permanently unusable on
            // an installation that has every model it needs.
            var fallbackRole = await this.TryGetFallbackPurposeRoleAsync(clientId, purpose, ct);
            if (fallbackRole is not null)
            {
                var resolvedFallback = await logicalModelResolver!.ResolveChatRuntimeAsync(clientId, fallbackRole, ct: ct);
                return resolvedFallback.Runtime;
            }

            throw new AiPurposeBindingNotConfiguredException(purpose);
        }

        if (!resolved.Model.SupportsChat)
        {
            throw new InvalidOperationException($"The configured model '{resolved.Model.RemoteModelId}' does not support chat workloads.");
        }

        await this.RefuseForbiddenProviderAsync(clientId, resolved.Connection, ct);

        return runtimeFactory.CreateChatRuntime(resolved.Connection, resolved.Model, resolved.Binding);
    }

    public async Task<IResolvedAiChatRuntime> ResolveChatRuntimeForModelAsync(
        Guid clientId,
        Guid configuredModelId,
        CancellationToken ct = default)
    {
        var resolved = await aiConnectionRepository.GetModelBindingAsync(clientId, configuredModelId, ct)
                       ?? throw new InvalidOperationException($"No chat-capable configured model '{configuredModelId}' is available for the client.");

        if (!resolved.Model.SupportsChat)
        {
            throw new InvalidOperationException($"The configured model '{resolved.Model.RemoteModelId}' does not support chat workloads.");
        }

        await this.RefuseForbiddenProviderAsync(clientId, resolved.Connection, ct);

        return runtimeFactory.CreateChatRuntime(resolved.Connection, resolved.Model, resolved.Binding);
    }

    public async Task<IResolvedAiEmbeddingRuntime> ResolveEmbeddingRuntimeAsync(
        Guid clientId,
        AiPurpose purpose,
        int? expectedDimensions = null,
        CancellationToken ct = default)
    {
        // Prefer a mapped logical model (the resolver enforces embedding capability + dimension match).
        var roleName = await this.TryGetPurposeRoleAsync(clientId, purpose, ct);
        if (roleName is not null)
        {
            var resolvedRole = await logicalModelResolver!.ResolveEmbeddingRuntimeAsync(clientId, roleName, expectedDimensions, ct: ct);
            return resolvedRole.Runtime;
        }

        var resolved = await aiConnectionRepository.GetActiveBindingForPurposeAsync(clientId, purpose, ct)
                       ?? throw new AiPurposeBindingNotConfiguredException(purpose);

        if (!resolved.Model.SupportsEmbedding)
        {
            throw new InvalidOperationException($"The configured model '{resolved.Model.RemoteModelId}' does not support embeddings.");
        }

        if (string.IsNullOrWhiteSpace(resolved.Model.TokenizerName) || !resolved.Model.EmbeddingDimensions.HasValue)
        {
            throw new InvalidOperationException($"The configured embedding model '{resolved.Model.RemoteModelId}' is missing capability metadata.");
        }

        if (expectedDimensions.HasValue && resolved.Model.EmbeddingDimensions.Value != expectedDimensions.Value)
        {
            throw new InvalidOperationException(
                $"The configured embedding model '{resolved.Model.RemoteModelId}' returns {resolved.Model.EmbeddingDimensions.Value} dimensions, but {expectedDimensions.Value} are required.");
        }

        await this.RefuseForbiddenProviderAsync(clientId, resolved.Connection, ct);

        return runtimeFactory.CreateEmbeddingRuntime(
            resolved.Connection,
            resolved.Model,
            resolved.Binding,
            resolved.Model.TokenizerName,
            resolved.Model.EmbeddingDimensions.Value);
    }

    // The tenant's provider policy is enforced again here, on the legacy purpose-binding path. The logical-model
    // path gets it from the connection scope guard; this path consults no guard, so a profile bound before the
    // policy changed would otherwise still run. Refusing before the runtime is built means no credential is used.
    private async Task RefuseForbiddenProviderAsync(Guid clientId, AiConnectionDto connection, CancellationToken ct)
    {
        if (providerPolicies is null)
        {
            return;
        }

        var policy = await providerPolicies.GetForClientAsync(clientId, ct);
        if (policy.DescribeRefusal(connection.ProviderKind) is { } refusal)
        {
            throw new InvalidOperationException($"The AI connection '{connection.DisplayName}' cannot be used because {refusal}.");
        }
    }

    // Returns the logical-model role mapped to the purpose for this client, or null when the logical-model layer is
    // unavailable (e.g. a resolver-less host) or the purpose is unmapped — in which case the caller uses the legacy
    // purpose-binding path.
    private async Task<string?> TryGetPurposeRoleAsync(Guid clientId, AiPurpose purpose, CancellationToken ct)
    {
        if (logicalModelResolver is null || logicalModelCatalog is null)
        {
            return null;
        }

        var roleName = await logicalModelCatalog.GetPurposeRoleAsync(clientId, purpose, ct);
        return string.IsNullOrEmpty(roleName) ? null : roleName;
    }

    /// <summary>
    ///     Walks the purpose's fallback chain against the logical-model mapping, returning the first mapped role.
    /// </summary>
    /// <remarks>
    ///     Deliberately not folded into <see cref="TryGetPurposeRoleAsync" />: that one is consulted first, and
    ///     widening it would change which model an already-working purpose resolves to. This runs only after both
    ///     the direct mapping and the connection binding (itself chain-walking) have found nothing, so it can
    ///     only turn a failure into a resolution, never one resolution into another.
    /// </remarks>
    private async Task<string?> TryGetFallbackPurposeRoleAsync(
        Guid clientId,
        AiPurpose purpose,
        CancellationToken ct)
    {
        if (logicalModelResolver is null || logicalModelCatalog is null)
        {
            return null;
        }

        foreach (var fallback in AiPurposeFallbacks.Chain(purpose))
        {
            var roleName = await logicalModelCatalog.GetPurposeRoleAsync(clientId, fallback, ct);
            if (!string.IsNullOrEmpty(roleName))
            {
                return roleName;
            }
        }

        return null;
    }
}
