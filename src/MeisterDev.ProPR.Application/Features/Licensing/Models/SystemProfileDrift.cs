// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     One recorded change to an installation's stable components.
///     <para>
///         A change to the volatile components records nothing, so every record here is a change to something
///         that was expected to hold for the life of the installation.
///     </para>
/// </summary>
public sealed record SystemProfileDrift
{
    /// <summary>When the change was observed.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    ///     Which stable components changed, named as they are in the profile document, sorted.
    /// </summary>
    public required IReadOnlyList<string> ChangedComponents { get; init; }

    /// <summary>The profile hash that held before the change.</summary>
    public required string PreviousHash { get; init; }

    /// <summary>The profile hash the installation reports after the change.</summary>
    public required string NewHash { get; init; }
}
