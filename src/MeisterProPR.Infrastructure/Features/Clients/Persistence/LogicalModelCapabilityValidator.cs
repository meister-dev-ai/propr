// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     Default <see cref="ILogicalModelCapabilityValidator" />. Loads the referenced connection by its global id and
///     checks the configured model behind the mapping against the role's declared capability.
/// </summary>
public sealed class LogicalModelCapabilityValidator(IAiConnectionRepository connections) : ILogicalModelCapabilityValidator
{
    public async Task ValidateAsync(LogicalModelDto entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var connection = await connections.GetByIdAsync(entry.ConnectionId, ct)
                         ?? throw new LogicalModelReferenceInvalidException(
                             entry.Name,
                             $"connection '{entry.ConnectionId}' does not exist.");

        var model = connection.ConfiguredModels.FirstOrDefault(m => m.Id == entry.ConfiguredModelId)
                    ?? throw new LogicalModelReferenceInvalidException(
                        entry.Name,
                        $"configured model '{entry.ConfiguredModelId}' is not present on connection '{entry.ConnectionId}'.");

        switch (entry.Capability)
        {
            case AiOperationKind.Chat:
                if (!model.SupportsChat)
                {
                    throw new LogicalModelReferenceInvalidException(
                        entry.Name,
                        $"model '{model.RemoteModelId}' does not support chat, which this role requires.");
                }

                break;

            case AiOperationKind.Embedding:
                if (!model.SupportsEmbedding)
                {
                    throw new LogicalModelReferenceInvalidException(
                        entry.Name,
                        $"model '{model.RemoteModelId}' does not support embeddings, which this role requires.");
                }

                if (string.IsNullOrWhiteSpace(model.TokenizerName) || !model.EmbeddingDimensions.HasValue)
                {
                    throw new LogicalModelReferenceInvalidException(
                        entry.Name,
                        $"embedding model '{model.RemoteModelId}' is missing capability metadata (tokenizer name and/or embedding dimensions).");
                }

                break;

            default:
                throw new LogicalModelReferenceInvalidException(
                    entry.Name,
                    $"unknown capability '{entry.Capability}'.");
        }
    }
}
