// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>What the loader recorded about an assembly it did not take.</summary>
/// <param name="Category">Which kind of failure this was.</param>
/// <param name="Reason">What went wrong, in terms an operator can act on.</param>
/// <param name="FilePath">The assembly that was skipped.</param>
/// <param name="ContentHash">
///     The SHA-256 of that file, lowercase hexadecimal, or null when the file could not be read.
/// </param>
/// <param name="Key">The family's declared identity key, where the loader got far enough to read one.</param>
/// <param name="Origin">Which directory the assembly was found in.</param>
/// <remarks>
///     Recorded rather than thrown, because one unreadable file in a mounted directory would otherwise take the
///     whole product down, including every family that loaded. The record is what keeps a skip visible, which is
///     the cost of skipping rather than failing.
/// </remarks>
public sealed record RejectedProviderAddIn(
    ProviderAddInRejectionCategory Category,
    string Reason,
    string FilePath,
    string? ContentHash,
    string? Key,
    ProviderAddInOrigin Origin);
