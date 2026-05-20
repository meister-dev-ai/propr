// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

internal static class FileNeighborhoodBuilder
{
    private const int MaxRelatedPaths = 20;

    public static FileNeighborhood Build(string branchSide, string branch, string filePath, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var normalizedFilePath = filePath.Replace('\\', '/').TrimStart('/');
        var normalizedPaths = paths
            .Select(path => path.Replace('\\', '/').TrimStart('/'))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        if (!normalizedPaths.Contains(normalizedFilePath, StringComparer.Ordinal))
        {
            return new FileNeighborhood(
                RepositorySearchStatuses.InvalidRequest,
                branchSide,
                branch,
                normalizedFilePath,
                null,
                [],
                [],
                [],
                [],
                [
                    new RepositorySearchLimitation(
                        normalizedFilePath, RepositorySearchLimitationReasons.FileNotFound, "The requested file was not found on the selected branch."),
                ],
                false);
        }

        var directory = Path.GetDirectoryName(normalizedFilePath)?.Replace('\\', '/') ?? string.Empty;
        var root = directory.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(normalizedFilePath);
        var areaPaths = normalizedPaths
            .Where(path => IsSameArea(path, directory, root, stem))
            .ToList();

        var nearbyTests = TakeRelated(areaPaths, path => IsTestPath(path));
        var configTouchpoints = TakeRelated(areaPaths, IsConfigPath);
        var registrations = TakeRelated(areaPaths, IsRegistrationPath);
        var docs = TakeRelated(normalizedPaths, IsDocsOrSpecPath);
        var truncated = nearbyTests.Truncated || configTouchpoints.Truncated || registrations.Truncated || docs.Truncated;
        IReadOnlyList<RepositorySearchLimitation> limitations = truncated
            ?
            [
                new RepositorySearchLimitation(
                    null,
                    RepositorySearchLimitationReasons.ResultTruncated,
                    $"File neighborhood sections are capped at {MaxRelatedPaths} paths."),
            ]
            : [];

        return new FileNeighborhood(
            RepositorySearchStatuses.Success,
            branchSide,
            branch,
            normalizedFilePath,
            ResolveOwningModule(normalizedFilePath, normalizedPaths),
            nearbyTests.Paths,
            configTouchpoints.Paths,
            registrations.Paths,
            docs.Paths,
            limitations,
            truncated);
    }

    private static RelatedPaths TakeRelated(IEnumerable<string> paths, Func<string, bool> predicate)
    {
        var matched = paths
            .Where(predicate)
            .Take(MaxRelatedPaths + 1)
            .ToList();
        var truncated = matched.Count > MaxRelatedPaths;
        return new RelatedPaths(matched.Take(MaxRelatedPaths).ToList().AsReadOnly(), truncated);
    }

    private static bool IsSameArea(string path, string directory, string root, string stem)
    {
        if (!string.IsNullOrWhiteSpace(directory) && path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(root) && path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(stem) && path.Contains(stem, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveOwningModule(string filePath, IReadOnlyList<string> paths)
    {
        var directory = Path.GetDirectoryName(filePath)?.Replace('\\', '/') ?? string.Empty;
        var current = directory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var project = paths.FirstOrDefault(path =>
                path.StartsWith(current + "/", StringComparison.OrdinalIgnoreCase) &&
                (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase)));
            if (project is not null)
            {
                return project;
            }

            current = Path.GetDirectoryName(current)?.Replace('\\', '/') ?? string.Empty;
        }

        return directory;
    }

    private static bool IsTestPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("Tests", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains(".spec.", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains(".test.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConfigPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegistrationPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("Registration", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("ServiceCollection", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("DependencyInjection", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDocsOrSpecPath(string path)
    {
        return path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("specs/", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("README.md", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RelatedPaths(IReadOnlyList<string> Paths, bool Truncated);
}
