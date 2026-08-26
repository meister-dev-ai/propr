// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     The profile an installation currently reports, as one record.
///     <para>
///         Nothing in verification, resolution, activation or quota enforcement reads it. It exists so that
///         reports arriving from one estate can be told apart from reports arriving from two.
///     </para>
/// </summary>
public sealed record SystemProfileSnapshot
{
    /// <summary>The components the profile hash is computed over.</summary>
    public required SystemProfileStableComponents Stable { get; init; }

    /// <summary>The components describing the host the last observation came from.</summary>
    public required SystemProfileVolatileComponents Volatile { get; init; }

    /// <summary>
    ///     The hash of the stable components: SHA-256 over their canonical rendering, in lower-case hexadecimal.
    /// </summary>
    public required string ProfileHash { get; init; }

    /// <summary>When the installation's profile was first recorded. It does not move once set.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>When the profile was last observed, whether or not the observation changed anything.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
