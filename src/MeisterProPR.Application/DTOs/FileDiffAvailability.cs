// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Diff availability state values returned with <see cref="FileDiffDto" />.
/// </summary>
public static class FileDiffAvailability
{
    /// <summary>Diff content is present and renderable.</summary>
    public const string Available = "Available";

    /// <summary>File is binary; no diff content.</summary>
    public const string Binary = "Binary";

    /// <summary>File path was not found in the PR's changed files.</summary>
    public const string NotFound = "NotFound";

    /// <summary>The source control provider could not be reached.</summary>
    public const string ProviderUnavailable = "ProviderUnavailable";
}
