// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class RepositorySearchExecutorTests
{
    [Fact]
    public async Task SearchCodeAsync_ExactIdentifier_DoesNotMatchIdentifierSubstrings()
    {
        var harness = new SearchHarness();
        harness.SetTree("src/Foo.cs", "src/Bar.cs");
        harness.SetFile("src/Foo.cs", "var token = GetToken();\nvar tokenFactory = Create();");
        harness.SetFile("src/Bar.cs", "var tokenFactory = Create();");

        var result = await harness.SearchCodeAsync(
            new CodeSearchRequest(
                "token",
                CodeSearchModes.ExactIdentifier,
                RepositorySearchBranchSides.Source,
                RepositorySearchPathScopes.Repository));

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        var match = Assert.Single(result.Matches);
        Assert.Equal("src/Foo.cs", match.FilePath);
        Assert.Equal(1, match.LineNumber);
        Assert.Equal(1, match.Rank);
    }

    [Fact]
    public async Task SearchCodeAsync_ExactPhrase_UsesCaseSensitivePhraseMatching()
    {
        var harness = new SearchHarness();
        harness.SetTree("src/Foo.cs", "src/Bar.cs");
        harness.SetFile("src/Foo.cs", "return \"Hello world\";");
        harness.SetFile("src/Bar.cs", "return \"hello world\";");

        var result = await harness.SearchCodeAsync(
            new CodeSearchRequest(
                "Hello world",
                CodeSearchModes.ExactPhrase,
                RepositorySearchBranchSides.Source,
                RepositorySearchPathScopes.Repository));

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        var match = Assert.Single(result.Matches);
        Assert.Equal("src/Foo.cs", match.FilePath);
        Assert.Equal(CodeSearchModes.ExactPhrase, result.SearchMode);
    }

    [Fact]
    public async Task SearchCodeAsync_FiltersApplyBeforeRanking()
    {
        var harness = new SearchHarness();
        harness.SetTree("src/Foo.cs", "tests/FooTests.cs", "generated/Foo.g.cs", "src/Foo.ts");
        harness.SetFile("src/Foo.cs", "needle");
        harness.SetFile("tests/FooTests.cs", "needle");
        harness.SetFile("generated/Foo.g.cs", "needle");
        harness.SetFile("src/Foo.ts", "needle");

        var result = await harness.SearchCodeAsync(
            new CodeSearchRequest(
                "needle",
                CodeSearchModes.ExactPhrase,
                RepositorySearchBranchSides.Source,
                RepositorySearchPathScopes.Repository,
                new CodeSearchFilterSet("csharp", PathPrefix: "src", ExcludeGenerated: true, ExcludeTests: true)));

        Assert.Equal(RepositorySearchStatuses.Partial, result.Status);
        var match = Assert.Single(result.Matches);
        Assert.Equal("src/Foo.cs", match.FilePath);
        Assert.Contains(result.Limitations, limitation => limitation.Reason == RepositorySearchLimitationReasons.FilterExcluded);
    }

    [Fact]
    public async Task SearchCodeAsync_ChangedFilesScope_UsesOnlyChangedPaths()
    {
        var harness = new SearchHarness([new ChangedPathSnapshot("src/Changed.cs", ChangeType.Edit, true, true)]);
        harness.SetTree("src/Changed.cs", "src/Unchanged.cs");
        harness.SetFile("src/Changed.cs", "needle");
        harness.SetFile("src/Unchanged.cs", "needle");

        var result = await harness.SearchCodeAsync(
            new CodeSearchRequest(
                "needle",
                CodeSearchModes.ExactPhrase,
                RepositorySearchBranchSides.Source,
                RepositorySearchPathScopes.ChangedFiles));

        Assert.Equal(RepositorySearchStatuses.Success, result.Status);
        var match = Assert.Single(result.Matches);
        Assert.Equal("src/Changed.cs", match.FilePath);
    }

    [Fact]
    public async Task SearchCodeAsync_InvalidRegex_ReturnsInvalidRequest()
    {
        var harness = new SearchHarness();

        var result = await harness.SearchCodeAsync(
            new CodeSearchRequest(
                "(",
                CodeSearchModes.Regex,
                RepositorySearchBranchSides.Source,
                RepositorySearchPathScopes.Repository));

        Assert.Equal(RepositorySearchStatuses.InvalidRequest, result.Status);
        Assert.Contains(result.Limitations, limitation => limitation.Reason == RepositorySearchLimitationReasons.InvalidRegex);
    }

    [Fact]
    public async Task SearchCodeAsync_PathologicalRegex_TimesOutInsteadOfHanging()
    {
        var harness = new SearchHarness();
        harness.SetTree("src/Foo.cs");
        harness.SetFile("src/Foo.cs", new string('a', 40) + '!');

        var stopwatch = Stopwatch.StartNew();
        var result = await harness.SearchCodeAsync(
            new CodeSearchRequest(
                "(a+)+$",
                CodeSearchModes.Regex,
                RepositorySearchBranchSides.Source,
                RepositorySearchPathScopes.Repository));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Search took {stopwatch.Elapsed}, the match timeout did not bound it.");
        Assert.Contains(result.Limitations, limitation => limitation.Reason == RepositorySearchLimitationReasons.RegexTimedOut);
    }

    [Fact]
    public async Task SearchCodeAsync_RecordsExactlyOneAggregateRepositorySearchPhase_PerScan()
    {
        var harness = new SearchHarness();
        harness.SetTree("src/Foo.cs", "src/Bar.cs", "src/Baz.cs", "src/Qux.cs");
        harness.SetFile("src/Foo.cs", "needle here\nno match");
        harness.SetFile("src/Bar.cs", "another needle");
        harness.SetFile("src/Baz.cs", "no match at all");
        harness.SetFile("src/Qux.cs", "needle once more");

        var request = new CodeSearchRequest(
            "needle",
            CodeSearchModes.ExactPhrase,
            RepositorySearchBranchSides.Source,
            RepositorySearchPathScopes.Repository);

        // Baseline (no collection active): capture the matches the executor produces.
        var baseline = await harness.SearchCodeAsync(request);

        CodeSearchResult collected;
        IReadOnlyList<ProtocolEventPhaseTiming>? phases;
        using (ToolTimingCollectorContext.BeginCollection())
        {
            collected = await harness.SearchCodeAsync(request);
            phases = ToolTimingCollectorContext.CaptureSnapshot();
        }

        Assert.NotNull(phases);

        // Exactly ONE repository_search phase per scan (aggregate), not 2xN per-candidate phases.
        var repositorySearchPhases = phases!.Where(p => p.Name == ProtocolEventToolPhaseNames.RepositorySearch).ToList();
        Assert.Single(repositorySearchPhases);

        // No per-file content-fetch phases remain (those were the other half of the 2xN bloat).
        Assert.DoesNotContain(phases!, p => p.Name == ProtocolEventToolPhaseNames.ScmFileContentFetch);

        // The aggregate phase carries a files_scanned summary.
        var aggregate = repositorySearchPhases[0];
        Assert.NotNull(aggregate.Summary);
        Assert.Contains("files_scanned=", aggregate.Summary!, StringComparison.Ordinal);

        // Behavior-preserving: matches identical to the baseline (same paths, lines, ranks, ordering).
        Assert.Equal(
            baseline.Matches.Select(m => (m.FilePath, m.LineNumber, m.Rank)),
            collected.Matches.Select(m => (m.FilePath, m.LineNumber, m.Rank)));
        Assert.Equal(baseline.Status, collected.Status);
        Assert.Equal(baseline.Truncated, collected.Truncated);
        Assert.Equal(baseline.Limitations.Count, collected.Limitations.Count);
    }

    private sealed class SearchHarness(IReadOnlyList<ChangedPathSnapshot>? changedPathSnapshots = null)
    {
        private readonly Dictionary<string, string?> _files = new(StringComparer.Ordinal);
        private readonly List<string> _tree = [];

        public void SetTree(params string[] paths)
        {
            this._tree.Clear();
            this._tree.AddRange(paths);
        }

        public void SetFile(string path, string? content, string branch = "feature/test")
        {
            this._files[$"{branch}:{path}"] = content;
        }

        public Task<CodeSearchResult> SearchCodeAsync(CodeSearchRequest request)
        {
            return RepositoryCodeSearchExecutor.ExecuteAsync(
                request,
                "feature/test",
                "main",
                changedPathSnapshots,
                (_, _) => Task.FromResult<IReadOnlyList<string>>(this._tree.ToList().AsReadOnly()),
                (path, branch, _) =>
                {
                    this._files.TryGetValue($"{branch}:{path}", out var content);
                    return Task.FromResult(content);
                },
                branch => branch,
                path => path,
                1_000_000,
                CancellationToken.None);
        }
    }
}
