// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Adds files of the full pull request listing to the files of a delta fetch. Each provider fetcher reads
///     the full listing on a delta fetch for the manifest of the whole pull request, so a file that the review
///     covers in addition to the delta is taken from that listing and fetched with its content like a file of
///     the delta.
/// </summary>
internal static class DeltaFileAdditions
{
    /// <summary>
    ///     Returns the delta entries followed by the entries of <paramref name="allFiles" /> whose path is in
    ///     <paramref name="requestedPaths" /> and not already part of the delta. Paths are compared without regard
    ///     to case, as carry-forward compares them. A requested path that is missing from the full listing adds
    ///     nothing, because the file is no longer part of the pull request.
    /// </summary>
    /// <param name="deltaFiles">The entries of the delta, in the order the provider listed them.</param>
    /// <param name="allFiles">The entries of the full pull request listing.</param>
    /// <param name="requestedPaths">The repository-relative paths to add, or <see langword="null" />.</param>
    /// <param name="pathOf">
    ///     Returns the repository-relative path of an entry, normalized like the paths of the fetched files, or
    ///     <see langword="null" /> for an entry that is not a file.
    /// </param>
    public static IReadOnlyList<T> AddRequestedFiles<T>(
        IReadOnlyList<T> deltaFiles,
        IReadOnlyList<T> allFiles,
        IReadOnlyCollection<string>? requestedPaths,
        Func<T, string?> pathOf)
    {
        ArgumentNullException.ThrowIfNull(deltaFiles);
        ArgumentNullException.ThrowIfNull(allFiles);
        ArgumentNullException.ThrowIfNull(pathOf);

        if (requestedPaths is not { Count: > 0 })
        {
            return deltaFiles;
        }

        var requested = new HashSet<string>(requestedPaths, StringComparer.OrdinalIgnoreCase);
        var included = new HashSet<string>(deltaFiles.Select(pathOf).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var additions = allFiles
            .Where(file => pathOf(file) is { } path && requested.Contains(path) && included.Add(path))
            .ToList();

        return additions.Count == 0 ? deltaFiles : [.. deltaFiles, .. additions];
    }
}
