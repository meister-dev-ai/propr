// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     The payload of a license document.
///     <para>
///         No member is checked against installation state here. This type says what the document claims;
///         deciding whether the installation matches those claims belongs to the code that consumes a
///         verified license.
///     </para>
///     <para>
///         Claims the reader does not know are dropped rather than refused, so a license issued by a newer
///         tool still reads on an older build as long as the schema version stays the same.
///     </para>
/// </summary>
public sealed record LicenseClaims
{
    /// <summary>
    ///     The payload schema version. Defaults to the version this build writes.
    /// </summary>
    public int SchemaVersion { get; init; } = LicenseDocument.SchemaVersion;

    /// <summary>
    ///     The license identifier, carried in the <c>jti</c> claim. It is at most 256 characters and contains
    ///     no control characters.
    /// </summary>
    public required string LicenseId { get; init; }

    /// <summary>
    ///     The organization the license was issued to. It is at most 512 characters and contains no control
    ///     characters.
    /// </summary>
    public required string Licensee { get; init; }

    /// <summary>When the license was issued, to whole seconds.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>The first moment the license is in force, to whole seconds.</summary>
    public required DateTimeOffset NotBefore { get; init; }

    /// <summary>The moment the license stops being in force, to whole seconds.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The capabilities the license grants. May be empty.</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <summary>The quantitative limits the license states.</summary>
    public LicenseLimits Limits { get; init; } = LicenseLimits.None;
}
