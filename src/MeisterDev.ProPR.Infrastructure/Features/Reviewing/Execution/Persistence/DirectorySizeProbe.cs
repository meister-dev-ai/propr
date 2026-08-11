// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Measures a mirror by adding up what is on disk.
///     <para>
///         An over-estimate of what a fetch transfers, since git compresses and the executor may already
///         hold most of the history in its cache. Erring high is the right direction for a ceiling whose
///         purpose is to stop an unexpectedly large repository from being transferred at all.
///     </para>
/// </summary>
public sealed class DirectorySizeProbe : IRunnerWorkspaceSizeProbe
{
    /// <inheritdoc />
    public Task<long> MeasureAsync(string mirrorPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(mirrorPath))
        {
            return Task.FromResult(0L);
        }

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(mirrorPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                total += new FileInfo(file).Length;
            }
            catch (FileNotFoundException)
            {
                // Git rewrites pack files under us during maintenance. A file that vanished mid-walk
                // contributes nothing rather than failing the measurement.
            }
        }

        return Task.FromResult(total);
    }
}
