// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Writes one portable evaluation artifact to durable storage.
/// </summary>
public interface IEvaluationArtifactWriter
{
    /// <summary>
    ///     Writes the supplied artifact to the requested output path.
    /// </summary>
    Task<string> WriteAsync(
        EvaluationArtifact artifact,
        string outputPath,
        CancellationToken cancellationToken = default);
}
