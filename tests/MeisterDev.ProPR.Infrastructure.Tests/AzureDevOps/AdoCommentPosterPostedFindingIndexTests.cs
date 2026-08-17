// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Cross-increment duplicate suppression against the index of findings already posted on the pull request.
///     The concerns that get re-posted drift in anchor, in severity and even in file between increments, so the
///     match is on the finding text alone; these tests pin that, and pin that a genuinely new finding still
///     reaches the pull request.
/// </summary>
public sealed class AdoCommentPosterPostedFindingIndexTests
{
    /// <summary>The host that issued the repository identifiers in this fixture.</summary>
    private const string Host = "https://dev.azure.com/org";

    private const string Project = "project";

    private static readonly Guid BotId = new("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ClientId = new("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid PostedFindingId = new("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    [Theory]
    [InlineData(null)]
    [InlineData("Active")]
    [InlineData("WontFix")]
    [InlineData("ByDesign")]
    public async Task PostResolvedThreadsAsync_DuplicateOfUnfixedPostedFinding_IsSuppressedAndNotPosted(string? status)
    {
        // An open thread is the whole defect: the concern is already on the pull request awaiting an answer.
        // A thread the reviewer closed as won't-fix or by-design is a decision, which makes re-raising worse,
        // not better.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(PostedFindingMatchDto.Match("4242", PostedFindingId, 0.93f));

        var invocations = new List<string>();
        var result = SingleFinding("The delete path re-checks ownership after the fetch.");

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("4242", status));

        Assert.Empty(invocations);
        Assert.Equal(0, diagnostics.PostedCount);
        Assert.Equal(1, diagnostics.SuppressedCount);
        Assert.Equal(1, diagnostics.SuppressionReasons["posted_finding_duplicate"]);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_DuplicateOfFindingAReviewerFixed_IsPostedAgain()
    {
        // A reviewer marking a thread fixed says the code moved. The concern coming back may well be real
        // again, so this is the one resolved state that must not suppress.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(PostedFindingMatchDto.Match("4242", PostedFindingId, 0.93f));

        var invocations = new List<string>();
        var result = SingleFinding("The delete path re-checks ownership after the fetch.");

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("4242", "Fixed"));

        Assert.Single(invocations);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_DuplicateOfThreadProPrAutoResolvedItself_IsSuppressed()
    {
        // Auto-resolve-by-severity leaves ProPR's own thread marked fixed. Reading that as a reviewer's fix
        // would turn cross-increment protection off entirely for every client that enables auto-resolve.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(PostedFindingMatchDto.Match("4242", PostedFindingId, 0.93f, autoResolvedByProPr: true));

        var invocations = new List<string>();
        var result = SingleFinding("The delete path re-checks ownership after the fetch.");

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("4242", "Fixed"));

        Assert.Empty(invocations);
        Assert.Equal(1, diagnostics.SuppressedCount);
        Assert.Equal(1, diagnostics.SuppressionReasons["posted_finding_duplicate"]);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_DuplicateOfThreadClosedWithoutBeingFixed_IsSuppressed()
    {
        // Azure DevOps "Closed" is how a reviewer dismisses a thread without claiming a fix. Re-raising a
        // dismissed concern is the behaviour this whole check exists to stop.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(PostedFindingMatchDto.Match("4242", PostedFindingId, 0.93f));

        var invocations = new List<string>();
        var result = SingleFinding("The delete path re-checks ownership after the fetch.");

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("4242", "Closed"));

        Assert.Empty(invocations);
        Assert.Equal(1, diagnostics.SuppressedCount);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_NearMissBelowTheThreshold_IsRecordedWithoutSuppressing()
    {
        // The other side of the line. Only recording the suppressions would make a threshold set too high
        // invisible, because nothing would ever show how close the misses came.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(PostedFindingMatchDto.NearMiss("4242", 0.82f));

        var invocations = new List<string>();
        var result = SingleFinding("The delete path re-checks ownership after the fetch.");

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("4242", "Active"));

        Assert.Single(invocations);
        Assert.Equal(0, diagnostics.SuppressedCount);
        var nearMiss = Assert.Single(diagnostics.PostedFindingNearMisses);
        Assert.Equal(0, nearMiss.Ordinal);
        Assert.Equal("4242", nearMiss.MatchedProviderThreadId);
        Assert.Equal(0.82f, nearMiss.MatchScore);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_EmbedderDown_CountsEachCandidateOnceAsAffected()
    {
        // Two tiers report the same candidate as degraded. Counting it twice puts the affected count above
        // the candidate count, which reads as more findings affected than the review produced.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(
                PostedFindingMatchDto.NoMatch(
                    ["posted_finding_index"],
                    "Cross-increment duplicate protection ran without the posted-finding index."));

        var invocations = new List<string>();
        var result = SingleFinding("The delete path re-checks ownership after the fetch.");

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("4242", "Active"));

        Assert.True(
            diagnostics.AffectedCandidateCount <= diagnostics.CandidateCount,
            $"affected {diagnostics.AffectedCandidateCount} exceeded candidates {diagnostics.CandidateCount}");
        Assert.Equal(1, diagnostics.AffectedCandidateCount);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_MatchedThreadNoLongerOnThePullRequest_IsPostedAgain()
    {
        // The earlier comment is gone, so suppressing would remove the concern from the pull request entirely.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(PostedFindingMatchDto.Match("4242", PostedFindingId, 0.93f));

        var invocations = new List<string>();
        var result = SingleFinding("The delete path re-checks ownership after the fetch.");

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("9999", "Active"));

        Assert.Single(invocations);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_MatchedThreadHasNoRemainingComments_IsPostedAgain()
    {
        // Every comment on the thread was deleted, so the concern is no longer readable on the pull request.
        // Retiring the finding against it would take the concern away with nothing left in its place.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(PostedFindingMatchDto.Match("4242", PostedFindingId, 0.93f));

        var invocations = new List<string>();
        var existingThreads = new List<PrCommentThread>
        {
            SummaryThread(),
            new("4242", "/src/Agents.cs", 999, new List<PrThreadComment>().AsReadOnly(), "Active"),
        };

        var diagnostics = await PostAsync(
            SingleFinding("The delete path re-checks ownership after the fetch."),
            invocations,
            index,
            existingThreads);

        Assert.Single(invocations);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_GenuinelyNewFindingOnTheSameFile_IsStillPosted()
    {
        // The criterion most at risk from this change.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(PostedFindingMatchDto.NoMatch());

        var invocations = new List<string>();
        var result = SingleFinding("This loop allocates inside the hot path.");

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("4242", "Active"));

        var posted = Assert.Single(invocations);
        Assert.Contains("This loop allocates inside the hot path.", posted, StringComparison.Ordinal);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_IndexLookup_ComparesTheFindingTextWithoutAnchorOrSeverity()
    {
        // The key has to survive a line moving, a severity flipping and the concern surfacing in another file,
        // so none of those may reach the lookup.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(PostedFindingMatchDto.NoMatch());

        var invocations = new List<string>();
        var result = new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new("/src/Other.cs", 278, CommentSeverity.Suggestion, "The delete path re-checks ownership after the fetch."),
            }.AsReadOnly());

        await PostAsync(result, invocations, index, ExistingThreads("4242", "Active"));

        await index.Received(1)
            .FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                "The delete path re-checks ownership after the fetch.",
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_IndexUnavailable_PostsAndReportsTheGapAsDegraded()
    {
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(
                PostedFindingMatchDto.NoMatch(
                    ["posted_finding_index"],
                    "Cross-increment duplicate protection ran without the posted-finding index."));

        var invocations = new List<string>();
        var result = SingleFinding("The delete path re-checks ownership after the fetch.");

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("4242", "Active"));

        Assert.Single(invocations);
        Assert.True(diagnostics.IsDegraded);
        Assert.Contains("posted_finding_index", diagnostics.DegradedComponents);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_IndexThrows_PostsRatherThanFailingTheReview()
    {
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("index unavailable"));

        var invocations = new List<string>();
        var result = SingleFinding("The delete path re-checks ownership after the fetch.");

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("4242", "Active"));

        Assert.Single(invocations);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Contains("posted_finding_index", diagnostics.DegradedComponents);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_SuppressedDuplicate_RecordsTheMatchedThreadAndScore()
    {
        // The suppressed finding is kept and flagged rather than dropped, and the score has to survive with it
        // or a badly chosen threshold cannot be spotted after the fact.
        var index = Substitute.For<IPostedFindingIndex>();
        index.FindDuplicateAsync(
                ClientId,
                Host,
                Project,
                "repo",
                7,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(PostedFindingMatchDto.Match("4242", PostedFindingId, 0.93f));

        var invocations = new List<string>();
        var result = new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new("/src/Agents.cs", 142, CommentSeverity.Error, "The delete path re-checks ownership after the fetch."),
            }.AsReadOnly());

        var diagnostics = await PostAsync(result, invocations, index, ExistingThreads("4242", "Active"));

        var suppression = Assert.Single(diagnostics.SuppressedFindings);
        Assert.Equal(0, suppression.Ordinal);
        Assert.Equal("posted_finding_duplicate", suppression.ReasonCode);
        Assert.Equal("4242", suppression.MatchedProviderThreadId);
        Assert.Equal(0.93f, suppression.MatchScore);
        Assert.Equal("/src/Agents.cs", suppression.FilePath);
        Assert.Equal(142, suppression.LineNumber);
    }

    [Fact]
    public async Task PostResolvedThreadsAsync_NoIndexBound_PostsExactlyAsBefore()
    {
        var invocations = new List<string>();
        var result = SingleFinding("The delete path re-checks ownership after the fetch.");

        var diagnostics = await PostAsync(result, invocations, index: null, ExistingThreads("4242", "Active"));

        Assert.Single(invocations);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Empty(diagnostics.SuppressedFindings);
    }

    private static ReviewResult SingleFinding(string message)
    {
        return new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new("/src/Agents.cs", 142, CommentSeverity.Error, message),
            }.AsReadOnly());
    }

    // A bot summary thread is always present, so the summary is not re-posted and every provider call these
    // tests observe is an inline finding. The earlier finding sits far from the candidate's anchor, so only the
    // index can match it: the deterministic anchor and text tiers cannot.
    private static PrCommentThread SummaryThread()
    {
        return new PrCommentThread(
            "1",
            null,
            null,
            new List<PrThreadComment>
            {
                new("Bot", "**AI Review Summary**\n\nEarlier increment.", BotId),
            }.AsReadOnly());
    }

    private static IReadOnlyList<PrCommentThread> ExistingThreads(string threadId, string? status)
    {
        return
        [
            SummaryThread(),
            new PrCommentThread(
                threadId,
                "/src/Agents.cs",
                999,
                new List<PrThreadComment>
                {
                    new("Bot", "ERROR: Something raised earlier.", BotId),
                }.AsReadOnly(),
                status),
        ];
    }

    private static Task<ReviewCommentPostingDiagnosticsDto> PostAsync(
        ReviewResult result,
        List<string> invocations,
        IPostedFindingIndex? index,
        IReadOnlyList<PrCommentThread> existingThreads)
    {
        AdoCommentPoster.AdoThreadFactory factory = (message, _, _, _) =>
        {
            invocations.Add(message);
            return Task.FromResult(
                new GitPullRequestCommentThread
                {
                    Id = 5000 + invocations.Count,
                    Comments = [new Comment { Id = (short)invocations.Count, Content = message }],
                });
        };

        var poster = new AdoCommentPoster(null!, null!, postedFindingIndex: index);
        return poster.PostResolvedThreadsAsync(
            result,
            factory,
            BotId,
            ClientId,
            organizationUrl: Host,
            projectId: Project,
            repositoryId: "repo",
            pullRequestId: 7,
            iterationId: 3,
            compareToIterationId: null,
            changeTrackingIds: new Dictionary<string, int>(),
            existingThreads: existingThreads,
            publicationIdentity: null,
            CancellationToken.None);
    }
}
