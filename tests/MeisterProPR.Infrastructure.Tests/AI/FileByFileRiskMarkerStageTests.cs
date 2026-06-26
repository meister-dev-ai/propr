// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.CodeAnalysis;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     When a structural analyzer can parse the file, security markers match only the ADDED
///     lines' real code — occurrences inside comments and string literals are excluded. When no analyzer
///     can handle the file, the stage falls back to plain added-content matching.
/// </summary>
public sealed class FileByFileRiskMarkerStageTests
{
    private const string CommentSource =
        "namespace N;\n" +
        "public class C\n" +
        "{\n" +
        "    // secret token note\n" +
        "    private int x = 1;\n" +
        "}\n";

    private const string CommentDiff =
        "@@ -3,2 +3,3 @@\n" +
        " {\n" +
        "+    // secret token note\n" +
        "     private int x = 1;\n";

    private const string StringSource =
        "namespace N;\n" +
        "public class C\n" +
        "{\n" +
        "    private string s = \"token value\";\n" +
        "}\n";

    private const string StringDiff =
        "@@ -3,1 +3,2 @@\n" +
        " {\n" +
        "+    private string s = \"token value\";\n";

    private const string CodeSource =
        "namespace N;\n" +
        "public class C\n" +
        "{\n" +
        "    private int token = 1;\n" +
        "}\n";

    private const string CodeDiff =
        "@@ -3,1 +3,2 @@\n" +
        " {\n" +
        "+    private int token = 1;\n";

    [Fact]
    public async Task SecurityTermInAddedComment_DoesNotFlag_WhenAnalyzerAvailable()
    {
        var markers = await RunAsync("src/C.cs", CommentSource, CommentDiff, FileByFileContextPrefetchStageTests.CreateRoslynAnalyzer());

        Assert.False(markers.HasSecurityMarkers);
    }

    [Fact]
    public async Task SecurityTermInAddedComment_Flags_WhenNoAnalyzer_FallsBackToAddedContent()
    {
        // No analyzer → plain added-content matching, which does see the keyword in the comment text.
        var markers = await RunAsync("src/C.cs", CommentSource, CommentDiff, null);

        Assert.True(markers.HasSecurityMarkers);
    }

    [Fact]
    public async Task SecurityTermInAddedStringLiteral_DoesNotFlag_WhenAnalyzerAvailable()
    {
        var markers = await RunAsync("src/C.cs", StringSource, StringDiff, FileByFileContextPrefetchStageTests.CreateRoslynAnalyzer());

        Assert.False(markers.HasSecurityMarkers);
    }

    [Fact]
    public async Task SecurityTermInAddedCode_Flags_WhenAnalyzerAvailable()
    {
        var markers = await RunAsync("src/C.cs", CodeSource, CodeDiff, FileByFileContextPrefetchStageTests.CreateRoslynAnalyzer());

        Assert.True(markers.HasSecurityMarkers);
        Assert.Contains("security.auth-token", markers.MatchedMarkers);
    }

    [Fact]
    public async Task NonAnalyzableFile_FallsBackToAddedContentMatching()
    {
        const string diff = "@@ -0,0 +1,1 @@\n+a token line\n";

        // .txt is not analyzable even though a Roslyn analyzer is supplied → fallback to added-content matching.
        var markers = await RunAsync("notes.txt", "a token line\n", diff, FileByFileContextPrefetchStageTests.CreateRoslynAnalyzer());

        Assert.True(markers.HasSecurityMarkers);
    }

    private static async Task<FileRiskMarkers> RunAsync(string path, string fullContent, string diff, IStructuralCodeAnalyzer? analyzer)
    {
        var stage = new FileByFileRiskMarkerStage(analyzer);
        var changedFile = new ChangedFile(path, ChangeType.Edit, fullContent, diff);
        var fileReviewContext = new ReviewSystemContext(null, [], null)
        {
            PerFileHint = new PerFileReviewHint(path, 1, 1, Array.Empty<ChangedFileSummary>()),
        };
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://org", "proj", "repo", 1, 1);
        var context = new PerFileReviewContext(job, changedFile, null, fileReviewContext, null, null, null);

        await stage.ExecuteAsync(context, CancellationToken.None);

        return context.FileReviewContext.PerFileHint!.RiskMarkers;
    }
}
