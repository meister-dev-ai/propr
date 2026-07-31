// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Records the token spend of one model call that happens outside a review job, against the client that
///     caused it.
/// </summary>
/// <remarks>
///     <para>
///         Review passes get this from the job protocol: tokens land on the protocol, on the job's per-tier
///         breakdown, and on the client's daily usage sample, and the budget scope prices each call as it happens.
///         Post-hoc work such as insight classification or memory keyword extraction runs outside any job, so
///         none of that applies to it, and without this port its tokens would be spent and never counted: absent
///         from cost reporting, and absent from the month-to-date total a client budget cap is measured against.
///     </para>
///     <para>
///         The daily usage sample is the one place both kinds of spend meet, so that is where this writes. It does
///         not price a cap in-flight the way a review does, because there is no in-flight total to compare: a
///         classification is one call, not a sequence that can be stopped part-way.
///     </para>
/// </remarks>
public interface IModelUsageRecorder
{
    /// <summary>
    ///     Records what <paramref name="response" /> reports having cost, attributed to
    ///     <paramref name="clientId" /> and to the model behind <paramref name="runtime" />.
    /// </summary>
    /// <remarks>
    ///     Best-effort by contract: the caller has already spent the tokens, so a failure to record them must not
    ///     fail the work that spent them. A response carrying no usage payload records nothing.
    /// </remarks>
    /// <param name="clientId">The client whose configuration selected the model.</param>
    /// <param name="runtime">The runtime that answered, supplying the model, its prices, and the connection.</param>
    /// <param name="response">The response to read usage from; may be <see langword="null" />.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordAsync(
        Guid clientId,
        IResolvedAiChatRuntime runtime,
        ChatResponse? response,
        CancellationToken ct = default);
}
