// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Resolves a named logical model (a role such as <c>deep</c> / <c>fast</c> / <c>sec</c>) for a client into an
///     execution-ready runtime. Precedence is client override before tenant catalog; the mapping's connection,
///     configured model, reasoning effort, and protocol mode are applied. When a protocol recorder + id are supplied,
///     the resolved role name and the layer that resolved it are recorded to the review trace.
/// </summary>
public interface ILogicalModelResolver
{
    /// <summary>
    ///     Resolves a chat logical model to a chat runtime. Fails if the role does not exist for the client
    ///     (<see cref="MeisterProPR.Application.Exceptions.LogicalModelNotFoundException" />) or is not a chat model
    ///     (<see cref="MeisterProPR.Application.Exceptions.LogicalModelCapabilityMismatchException" />).
    /// </summary>
    Task<ResolvedLogicalModelChatRuntime> ResolveChatRuntimeAsync(
        Guid clientId,
        string roleName,
        IProtocolRecorder? recorder = null,
        Guid? protocolId = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Resolves an embedding logical model to an embedding runtime. Fails if the role does not exist for the client
    ///     or is not an embedding model, or if the resolved model is missing embedding capability metadata / returns a
    ///     different dimension count than <paramref name="expectedDimensions" />.
    /// </summary>
    Task<ResolvedLogicalModelEmbeddingRuntime> ResolveEmbeddingRuntimeAsync(
        Guid clientId,
        string roleName,
        int? expectedDimensions = null,
        IProtocolRecorder? recorder = null,
        Guid? protocolId = null,
        CancellationToken ct = default);
}
