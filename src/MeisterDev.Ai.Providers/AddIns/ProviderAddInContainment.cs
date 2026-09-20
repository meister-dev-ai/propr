// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security;

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>
///     Whether a path found under a directory still leads inside it.
/// </summary>
/// <remarks>
///     <para>
///         Enumeration reports what sits in a directory, and a link there can point anywhere. The final target is
///         resolved so the loader reads only what it meant to read and so the path it records beside a content
///         hash is the file it actually read.
///     </para>
///     <para>
///         Shared by the discovery pass and by an add-in's load context, because both decide whether to read a
///         path the filesystem handed them and the answer has to be the same in both.
///     </para>
/// </remarks>
internal static class ProviderAddInContainment
{
    /// <summary>
    ///     Why <paramref name="candidatePath" /> does not stay inside <paramref name="directoryPath" />, or
    ///     <see langword="null" /> when it does.
    /// </summary>
    /// <param name="directoryPath">The directory the path has to stay inside.</param>
    /// <param name="candidatePath">The path to place.</param>
    /// <param name="isDirectory">Whether the candidate is a directory, which decides how its link is resolved.</param>
    /// <remarks>
    ///     A path the filesystem refuses to resolve is refused rather than admitted. The resolution reads the
    ///     directory the entry sits in, which on Unix is readable without being traversable, so a directory an
    ///     operator can list but not enter raises rather than returning a target. That is one entry the loader
    ///     cannot place, and it is reported as such instead of ending the pass.
    /// </remarks>
    internal static string? GetContainmentFailureReason(string directoryPath, string candidatePath, bool isDirectory)
    {
        string? resolved;
        try
        {
            var linkTarget = isDirectory
                ? new DirectoryInfo(candidatePath).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                : new FileInfo(candidatePath).ResolveLinkTarget(returnFinalTarget: true)?.FullName;

            resolved = WithEveryLinkResolved(Path.GetFullPath(linkTarget ?? candidatePath));
        }
        catch (Exception exception)
            when (exception is IOException
                      or UnauthorizedAccessException
                      or SecurityException
                      or ArgumentException
                      or NotSupportedException)
        {
            return $"'{candidatePath}' could not be resolved to a final target: {exception.Message} The loader "
                   + $"reads only what it can place inside '{directoryPath}', and this path it cannot place.";
        }

        var root = Path.TrimEndingDirectorySeparator(WithEveryLinkResolved(Path.GetFullPath(directoryPath)))
                   + Path.DirectorySeparatorChar;

        return resolved.StartsWith(root, PathComparison)
            ? null
            : $"'{candidatePath}' leads outside '{directoryPath}'. The loader reads only what is inside the "
              + "directory it was given.";
    }

    /// <summary>
    ///     Walks an absolute path from its root and resolves every component through its links, so the result
    ///     names where the path really leads.
    /// </summary>
    /// <remarks>
    ///     <see cref="FileSystemInfo.ResolveLinkTarget(bool)" /> reads one entry. Calling it on a path resolves
    ///     the last component and leaves the directories above it as the text they were written as, so a link on
    ///     one of those points the path somewhere else while the text still reads as inside the directory. That
    ///     is the case this walk closes.
    ///     <para>
    ///         A component that cannot be read is kept as it was written, along with everything below it. Such a
    ///         component names nothing the loader can open either, and the containment test then answers on the
    ///         text.
    ///     </para>
    /// </remarks>
    /// <param name="fullPath">An absolute path.</param>
    /// <returns>The same path with every link along it resolved.</returns>
    private static string WithEveryLinkResolved(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var components = fullPath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var walked = root;
        for (var index = 0; index < components.Length; index++)
        {
            walked = Path.Combine(walked, components[index]);

            try
            {
                walked = new DirectoryInfo(walked).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? walked;
            }
            catch (Exception exception)
                when (exception is IOException
                          or UnauthorizedAccessException
                          or SecurityException
                          or ArgumentException
                          or NotSupportedException)
            {
                return Path.Combine([walked, .. components[(index + 1)..]]);
            }
        }

        return walked;
    }

    // A path names the same file whatever its casing on Windows and macOS, so comparing the text ordinally
    // there reports a path that is inside as outside. Linux distinguishes casing and the ordinal comparison is
    // the correct one.
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Whether <paramref name="candidatePath" /> stays inside <paramref name="directoryPath" />.</summary>
    /// <param name="directoryPath">The directory the path has to stay inside.</param>
    /// <param name="candidatePath">The path to place.</param>
    /// <param name="isDirectory">Whether the candidate is a directory, which decides how its link is resolved.</param>
    internal static bool IsCandidatePathContainedInDirectory(string directoryPath, string candidatePath, bool isDirectory)
    {
        return GetContainmentFailureReason(directoryPath, candidatePath, isDirectory) is null;
    }
}
