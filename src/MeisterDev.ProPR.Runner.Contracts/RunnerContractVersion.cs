// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;

namespace MeisterDev.ProPR.Runner.Contracts;

/// <summary>
///     The version of the contract an executor and the control plane speak, and the rule for deciding
///     whether two of them can work together.
///     <para>
///         A control plane and the executors it serves are deployed at different times, so a mismatch is
///         inevitable and has to be a clean refusal naming both sides rather than a confusing runtime
///         failure halfway through a review. The window is deliberately narrow: one prior version, so a
///         rolling upgrade does not strand a fleet, and no further, so the two sides cannot drift.
///     </para>
/// </summary>
public static class RunnerContractVersion
{
    /// <summary>
    ///     The version this build speaks. Version 2 put the review's title, description, and branches on the
    ///     target, and replaced the pass list's bare model name with a full binding, so an executor can count
    ///     tokens and budget its context without asking the control plane what it is about to call.
    /// </summary>
    public const int Current = 2;

    /// <summary>
    ///     How many older versions are still accepted. One, so a control-plane deploy does not refuse every
    ///     executor that has not been upgraded yet.
    /// </summary>
    public const int CompatibilityWindow = 1;

    /// <summary>
    ///     The oldest version that can read this build's job manifest. Evolution inside the window is
    ///     additive and a tolerant reader ignores what it does not know — but a version that changes
    ///     shapes, as 2 did to the pass list's model binding, moves this floor. Every operation serves
    ///     leased jobs, so a version that cannot read a manifest cannot usefully be served at all: the
    ///     floor clamps the whole window rather than gating one call.
    /// </summary>
    public const int OldestManifestCompatible = 2;

    /// <summary>
    ///     The oldest version this build will still serve: the compatibility window, clamped by the
    ///     manifest floor. One value for every gate — an offer that refuses a version the heartbeat calls
    ///     healthy is two answers to one question, and an operator chasing the wrong one.
    /// </summary>
    public static int Oldest => Math.Max(Math.Max(1, Current - CompatibilityWindow), OldestManifestCompatible);

    /// <summary>Whether an executor reporting <paramref name="reported" /> can be served.</summary>
    public static bool IsSupported(int reported)
    {
        return reported >= Oldest && reported <= Current;
    }

    /// <summary>
    ///     The refusal to give an executor whose version cannot be served. Names both the version it
    ///     reported and the range this control plane accepts, because an operator reading it needs to know
    ///     which side to move — and when the manifest floor is what clamped the range, says so, because
    ///     "too old" alone reads as an ordinary window miss rather than a shape change.
    /// </summary>
    /// <param name="reported">The version the executor reported.</param>
    public static string DescribeMismatch(int reported)
    {
        var direction = reported > Current
            ? "The runner is newer than the control plane; upgrade the control plane or run an older runner image."
            : reported < OldestManifestCompatible
                ? FormattableString.Invariant(
                    $"This control plane's job manifests require at least version {OldestManifestCompatible} because the manifest shape changed; upgrade the runner image.")
                : "The runner is too old; upgrade the runner image.";

        return FormattableString.Invariant(
            $"This runner speaks contract version {reported}, and this control plane accepts {Oldest} through {Current}. {direction}");
    }
}
