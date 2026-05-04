// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Resolves protected runtime values referenced by an evaluation configuration.
/// </summary>
public interface IProtectedValueResolver
{
    /// <summary>
    ///     Resolves the supplied protected-value references.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IReadOnlyList<ProtectedValueReference> references,
        CancellationToken cancellationToken = default);
}
