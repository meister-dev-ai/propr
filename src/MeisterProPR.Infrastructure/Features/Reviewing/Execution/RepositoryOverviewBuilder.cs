// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

internal static class RepositoryOverviewBuilder
{
    private const int MaxSectionPaths = 25;

    public static RepositoryOverview Build(string branchSide, string branch, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var normalizedPaths = paths
            .Select(path => path.Replace('\\', '/').TrimStart('/'))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var sections = new[]
        {
            SelectSection("projects", normalizedPaths, IsProjectPath),
            SelectSection("entry_points", normalizedPaths, IsEntryPointPath),
            SelectSection("module_boundaries", normalizedPaths, IsModuleBoundaryPath),
            SelectSection("test_locations", normalizedPaths, IsTestPath),
            SelectSection("config_touchpoints", normalizedPaths, IsConfigPath),
            SelectSection("persistence_paths", normalizedPaths, IsPersistencePath),
            SelectSection("registration_locations", normalizedPaths, IsRegistrationPath),
            SelectSection("docs_and_specs", normalizedPaths, IsDocsOrSpecPath),
        };
        var truncated = sections.Any(section => section.Paths.Count >= MaxSectionPaths);
        IReadOnlyList<RepositorySearchLimitation> limitations = truncated
            ?
            [
                new RepositorySearchLimitation(
                    null,
                    RepositorySearchLimitationReasons.ResultTruncated,
                    $"Repository overview sections are capped at {MaxSectionPaths} paths."),
            ]
            : [];

        return new RepositoryOverview(
            RepositorySearchStatuses.Success,
            branchSide,
            branch,
            sections[0],
            sections[1],
            sections[2],
            sections[3],
            sections[4],
            sections[5],
            sections[6],
            sections[7],
            limitations,
            truncated);
    }

    private static RepositoryOverviewSection SelectSection(
        string name,
        IReadOnlyList<string> paths,
        Func<string, bool> predicate)
    {
        var matched = paths
            .Where(predicate)
            .Take(MaxSectionPaths)
            .ToList()
            .AsReadOnly();
        return matched.Count == 0
            ? RepositoryOverview.EmptySection(name)
            : new RepositoryOverviewSection(name, matched, []);
    }

    private static bool IsProjectPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("vite.config.ts", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEntryPointPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Startup.cs", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Controller", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Endpoint", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Handler", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsModuleBoundaryPath(string path)
    {
        return path.Contains("/Features/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/Modules/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/Domain/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/Application/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/Infrastructure/", StringComparison.OrdinalIgnoreCase);
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
        return fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPersistencePath(string path)
    {
        return path.Contains("Migration", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("DbContext", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Repository", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegistrationPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Contains("Registration", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("ServiceCollection", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("DependencyInjection", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDocsOrSpecPath(string path)
    {
        return path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("specs/", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith("README.md", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }
}
