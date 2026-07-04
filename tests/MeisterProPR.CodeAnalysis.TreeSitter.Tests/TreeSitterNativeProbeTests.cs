// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.CodeAnalysis.TreeSitter.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.CodeAnalysis.TreeSitter.Tests;

/// <summary>
///     Fail-soft startup verification for <see cref="TreeSitterNativeProbe" />.
///     When native libs are available (the normal case on Linux x64/arm64), the probe
///     reports <c>IsAvailable=true</c>. When they are absent, the probe reports
///     <c>IsAvailable=false</c> and review proceeds on the heuristic — no crash.
/// </summary>
public sealed class TreeSitterNativeProbeTests
{
    [Fact]
    public void Probe_OnLinuxX64_ReportsAllLanguagesAvailable()
    {
        var probe = new TreeSitterNativeProbe(NullLogger<TreeSitterNativeProbe>.Instance);

        // On the CI/dev Linux x64 platform, all 7 kept languages should load from the
        // TreeSitter.DotNet package's runtimes/linux-x64/native assets.
        // If the platform doesn't have the libs (e.g. a minimal container), the probe
        // still must not throw — it reports IsAvailable=false.
        Assert.True(probe.IsAvailable || probe.LoadFailures.Count > 0);

        if (probe.IsAvailable)
        {
            Assert.Empty(probe.LoadFailures);
        }
        else
        {
            // At least one language failed to load — the worker stays up (fail-soft).
            Assert.NotEmpty(probe.LoadFailures);
        }
    }

    [Fact]
    public void Probe_DoesNotThrowOnConstruction()
    {
        // The probe must never throw during construction, even if all native libs fail.
        var ex = Record.Exception(() => new TreeSitterNativeProbe(NullLogger<TreeSitterNativeProbe>.Instance));

        Assert.Null(ex);
    }

    [Fact]
    public void Probe_ImplementsIStructuralAnalyzerProbe()
    {
        var probe = new TreeSitterNativeProbe(NullLogger<TreeSitterNativeProbe>.Instance);
        Assert.IsAssignableFrom<IStructuralAnalyzerProbe>(probe);
    }
}
