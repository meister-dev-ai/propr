// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Turns the logical model name an executor asked for into a usable chat client.
///     <para>
///         This is the step that keeps the provider key on the control plane. The manifest and the relay
///         request both carry only a name; the connection behind it is resolved here, on the side that holds
///         the credential, and never travels.
///     </para>
/// </summary>
public interface IRunnerRelayModelResolver
{
    /// <summary>
    ///     Resolves a named model for a client, or null when the installation has no such binding.
    /// </summary>
    /// <param name="clientId">The client whose bindings to resolve against.</param>
    /// <param name="logicalModelName">The named model role.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerRelayModel?> ResolveAsync(Guid clientId, string logicalModelName, CancellationToken ct = default);
}

/// <summary>
///     A resolved relay model: the client to call, and what its answers cost.
///     <para>
///         Pricing belongs here because the relay is where a remote review's spend is charged: it prices
///         each response against these rates exactly as the in-process budget decorator does. A relay that
///         could resolve a client but not its rates would charge nothing, and the job's cap would never
///         trip.
///     </para>
/// </summary>
/// <param name="Client">The chat client bound to the resolved connection.</param>
/// <param name="ProviderKind">The provider family, used to read cache-write token counts correctly.</param>
/// <param name="Pricing">The resolved model's rates. Unknown rates price to null, never to zero.</param>
public sealed record RunnerRelayModel(
    IChatClient Client,
    AiProviderKind ProviderKind,
    ModelPricing Pricing);

/// <summary>
///     Records what a relayed completion consumed.
/// </summary>
public interface IRunnerRelayUsageRecorder
{
    /// <summary>
    ///     Records usage for one physical call, attributed to the logical model that served it. Pricing is
    ///     not this port's job, because the relay prices against the resolved model. This exists so token
    ///     totals can be reconciled against what ingest later persisted.
    /// </summary>
    /// <param name="jobId">The job the completion belongs to.</param>
    /// <param name="logicalModelName">The model role that served it.</param>
    /// <param name="idempotencyKey">
    ///     Identifies the completion attempt. Recording twice under the same key must count once: a retry
    ///     that reached the provider only once has to cost what it actually cost.
    /// </param>
    /// <param name="usage">The token usage the provider reported.</param>
    /// <param name="ct">The cancellation token.</param>
    Task RecordAsync(
        Guid jobId,
        string logicalModelName,
        string idempotencyKey,
        UsageDetails? usage,
        CancellationToken ct = default);
}
