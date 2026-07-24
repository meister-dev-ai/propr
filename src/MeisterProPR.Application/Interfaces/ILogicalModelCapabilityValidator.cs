// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Validates, at configuration time, that a logical model's mapping is consistent with its declared capability:
///     the referenced connection and configured model exist, and the model actually supports the role's capability
///     (chat vs embedding) with the metadata that capability requires. Centralized so every write path — repository,
///     the future admin API/UI, and the migration — enforces the same rules.
/// </summary>
public interface ILogicalModelCapabilityValidator
{
    /// <summary>
    ///     Throws <see cref="MeisterProPR.Application.Exceptions.LogicalModelReferenceInvalidException" /> when the entry
    ///     references a missing connection/model, or a model that cannot serve the declared capability. Returns normally
    ///     when the mapping is valid.
    /// </summary>
    Task ValidateAsync(LogicalModelDto entry, CancellationToken ct = default);
}
