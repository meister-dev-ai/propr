// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     Covers the out-of-scope publication rule and the withheld-findings note on the published summary, as
///     applied while a review is posted.
/// </summary>
public partial class ReviewOrchestrationServicePostConfigurationTests
{
    private const string OutsideChangeFile = "src/Legacy.cs";
    private const string PublicUiOrigin = "https://propr.example.com";

    [Fact]
    public async Task WithholdOutOfScope_KeepsOutsideChangeFindingsOffThePullRequest_AndInThePersistedResult()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetWithholdOutOfScopeFindingsAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var commentPoster = CreatePublicationService();
        var (service, _) = CreateService(
            jobs,
            clientRegistry,
            BuildResultWithOneFindingOfEachScope(),
            commentPoster);

        await service.ProcessAsync(job, CancellationToken.None);

        await commentPoster.Received(1).PublishReviewAsync(
            job.ClientId,
            job.CodeReviewReference,
            Arg.Any<ReviewRevision>(),
            Arg.Is<ReviewResult>(result =>
                result.Comments.Count == 1 && result.Comments[0].FilePath == WarningFile),
            Arg.Any<ReviewerIdentity>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewPublicationContext?>());

        // Withholding a finding from the pull request does not discard it: the review record still has both.
        await jobs.Received(1).SetResultAsync(
            job.Id,
            Arg.Is<ReviewResult>(result => result.Comments.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithholdOutOfScope_WhenOff_PublishesOutsideChangeFindingsAndLeavesTheSummaryAlone()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);

        var commentPoster = CreatePublicationService();
        var (service, _) = CreateService(
            jobs,
            clientRegistry,
            BuildResultWithOneFindingOfEachScope(),
            commentPoster,
            publicUiOrigin: PublicUiOrigin);

        await service.ProcessAsync(job, CancellationToken.None);

        // The default configuration is the one that must stay byte-identical, summary included.
        await commentPoster.Received(1).PublishReviewAsync(
            job.ClientId,
            job.CodeReviewReference,
            Arg.Any<ReviewRevision>(),
            Arg.Is<ReviewResult>(result =>
                result.Comments.Count == 2 && result.Summary == "Summary."),
            Arg.Any<ReviewerIdentity>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewPublicationContext?>());
    }

    [Fact]
    public async Task WithheldFindings_AreReportedInThePublishedSummaryByReason_WithALinkBackToProPr()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetMinimumSeverityToPostAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CommentSeverity.Warning));
        clientRegistry.GetWithholdOutOfScopeFindingsAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var orchestratorResult = new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new(WarningFile, 1, CommentSeverity.Warning, "A warning to keep."),
                new(SuggestionFile, 2, CommentSeverity.Suggestion, "Below the threshold."),
                new(OutsideChangeFile, 3, CommentSeverity.Warning, "In pre-existing code.")
                {
                    ScopeRelation = ReviewCommentScopeRelation.OutsideChange,
                },
            }.AsReadOnly());

        ReviewResult? published = null;
        var commentPoster = CreatePublicationService();
        await commentPoster.PublishReviewAsync(
            Arg.Any<Guid>(),
            Arg.Any<CodeReviewRef>(),
            Arg.Any<ReviewRevision>(),
            Arg.Do<ReviewResult>(result => published = result),
            Arg.Any<ReviewerIdentity>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewPublicationContext?>());

        var (service, _) = CreateService(
            jobs,
            clientRegistry,
            orchestratorResult,
            commentPoster,
            publicUiOrigin: PublicUiOrigin);

        await service.ProcessAsync(job, CancellationToken.None);

        Assert.NotNull(published);
        Assert.StartsWith("Summary.", published.Summary, StringComparison.Ordinal);
        Assert.Contains("2 findings are held back from this pull request", published.Summary, StringComparison.Ordinal);
        Assert.Contains("- 1 below the minimum severity to post", published.Summary, StringComparison.Ordinal);
        Assert.Contains("- 1 in pre-existing code outside this change", published.Summary, StringComparison.Ordinal);
        Assert.Contains(
            $"[Open the full review in ProPR]({PublicUiOrigin}/jobs/{job.Id:D}/protocol)",
            published.Summary,
            StringComparison.Ordinal);

        // The link goes to a page, so it must carry no query string: GitLab HTML-encodes the summary body it
        // posts, which would turn a query separator into an entity.
        Assert.DoesNotContain("?", published.Summary, StringComparison.Ordinal);

        // The persisted summary stays the reviewer's own. The note describes publication, and the reader of
        // the review record is already looking at everything it reports.
        await jobs.Received(1).SetResultAsync(
            job.Id,
            Arg.Is<ReviewResult>(result => result.Summary == "Summary."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithheldFindings_WithNoConfiguredPublicUrl_ReportTheCountWithoutALink()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetWithholdOutOfScopeFindingsAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        ReviewResult? published = null;
        var commentPoster = CreatePublicationService();
        await commentPoster.PublishReviewAsync(
            Arg.Any<Guid>(),
            Arg.Any<CodeReviewRef>(),
            Arg.Any<ReviewRevision>(),
            Arg.Do<ReviewResult>(result => published = result),
            Arg.Any<ReviewerIdentity>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewPublicationContext?>());

        var (service, _) = CreateService(
            jobs,
            clientRegistry,
            BuildResultWithOneFindingOfEachScope(),
            commentPoster);

        await service.ProcessAsync(job, CancellationToken.None);

        Assert.NotNull(published);
        Assert.Contains("1 finding is held back from this pull request", published.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Open the full review in ProPR", published.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithheldFindings_AreRecordedOnTheJobProtocol()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetWithholdOutOfScopeFindingsAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        var protocolId = Guid.NewGuid();
        protocolRecorder.BeginAsync(default, default).ReturnsForAnyArgs(protocolId);

        var (service, _) = CreateService(
            jobs,
            clientRegistry,
            BuildResultWithOneFindingOfEachScope(),
            CreatePublicationService(),
            protocolRecorder: protocolRecorder);

        await service.ProcessAsync(job, CancellationToken.None);

        // Diagnosing "why was this not posted" starts on the job protocol, so the decision has to be recorded
        // there rather than only on the pull request the reader may not be looking at.
        // Every string argument needs a matcher once any of them has one, or NSubstitute cannot tell which
        // specification belongs to which parameter.
        await protocolRecorder.Received(1).RecordPublicationEventAsync(
            protocolId,
            Arg.Is<string>(name => name == "publication_withheld_findings"),
            Arg.Is<string?>(details =>
                details!.Contains("\"withheldCount\":1", StringComparison.Ordinal)
                && details.Contains("\"outsideChangedLines\":1", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindingsAboveTheLimit_AreHeldBackAndCountedOnTheJobProtocol()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetMaximumFindingsToPostAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<int?>(1));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        var protocolId = Guid.NewGuid();
        protocolRecorder.BeginAsync(default, default).ReturnsForAnyArgs(protocolId);

        var commentPoster = CreatePublicationService();
        var (service, _) = CreateService(
            jobs,
            clientRegistry,
            BuildResultWithOneFindingOfEachScope(),
            commentPoster,
            protocolRecorder: protocolRecorder);

        await service.ProcessAsync(job, CancellationToken.None);

        // Both findings have the same rank, so the earlier one is posted.
        await commentPoster.Received(1).PublishReviewAsync(
            job.ClientId,
            job.CodeReviewReference,
            Arg.Any<ReviewRevision>(),
            Arg.Is<ReviewResult>(result =>
                result.Comments.Count == 1
                && result.Comments[0].FilePath == WarningFile
                && result.Summary.Contains("- 1 above the limit of 1 finding for one pull request", StringComparison.Ordinal)),
            Arg.Any<ReviewerIdentity>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewPublicationContext?>());
        await protocolRecorder.Received(1).RecordPublicationEventAsync(
            protocolId,
            Arg.Is<string>(name => name == "publication_withheld_findings"),
            Arg.Is<string?>(details =>
                details!.Contains("\"withheldCount\":1", StringComparison.Ordinal)
                && details.Contains("\"aboveLimit\":1", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
        await jobs.Received(1).SetResultAsync(
            job.Id,
            Arg.Is<ReviewResult>(result => result.Comments.Count == 2),
            Arg.Any<CancellationToken>());
    }

    // A synthesized concern whose supporting findings are all posted is left out of the pull request. The review record
    // keeps it as a withheld finding with its reason, and the job protocol counts it apart from the held-back findings.
    [Fact]
    public async Task SynthesizedConcern_WhoseSupportingFindingsArePosted_IsKeptInTheRecordAsAWithheldFinding()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        var protocolId = Guid.NewGuid();
        protocolRecorder.BeginAsync(default, default).ReturnsForAnyArgs(protocolId);
        const string concernMessage = "Tenant isolation is missing in the cache layer.";
        var orchestratorResult = new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new(WarningFile, 1, CommentSeverity.Warning, "The cache key omits the tenant id.") { FindingId = "finding-a" },
                new(SuggestionFile, 2, CommentSeverity.Warning, "Orders read the cache entry of another tenant.") { FindingId = "finding-b" },
                new(null, null, CommentSeverity.Warning, concernMessage) { SupportingFindingIds = ["finding-a", "finding-b"] },
            }.AsReadOnly());

        var commentPoster = CreatePublicationService();
        var (service, _) = CreateService(jobs, clientRegistry, orchestratorResult, commentPoster, protocolRecorder: protocolRecorder);

        await service.ProcessAsync(job, CancellationToken.None);

        await commentPoster.Received(1).PublishReviewAsync(
            job.ClientId,
            job.CodeReviewReference,
            Arg.Any<ReviewRevision>(),
            Arg.Is<ReviewResult>(result =>
                result.Comments.Count == 2
                && result.Comments.All(comment => comment.Message != concernMessage)
                && result.Summary == "Summary."),
            Arg.Any<ReviewerIdentity>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewPublicationContext?>());
        await jobs.Received(1).SetResultAsync(
            job.Id,
            Arg.Is<ReviewResult>(result =>
                result.Comments.Count == 2
                && result.WithheldFindings.Count == 1
                && result.WithheldFindings[0].Comment.Message == concernMessage
                && result.WithheldFindings[0].ReasonCode == ReviewFindingGateReasonCodes.RestatesPublishedFindings),
            Arg.Any<CancellationToken>());
        await protocolRecorder.Received(1).RecordPublicationEventAsync(
            protocolId,
            Arg.Is<string>(name => name == "publication_withheld_findings"),
            Arg.Is<string?>(details =>
                details!.Contains("\"withheldCount\":0", StringComparison.Ordinal)
                && details.Contains("\"restatementsNotPosted\":1", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithheldFindings_WhenScmPostingIsDisabled_AreNotRecordedAsWithheld()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetWithholdOutOfScopeFindingsAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        clientRegistry.GetScmCommentPostingEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(default, default).ReturnsForAnyArgs(Guid.NewGuid());

        var commentPoster = CreatePublicationService();
        var (service, _) = CreateService(
            jobs,
            clientRegistry,
            BuildResultWithOneFindingOfEachScope(),
            commentPoster,
            protocolRecorder: protocolRecorder);

        await service.ProcessAsync(job, CancellationToken.None);

        // Nothing was published at all, so nothing was kept off a pull request. Recording a withheld count
        // here would read as a policy decision where the client had simply turned posting off.
        await commentPoster.DidNotReceive().PublishReviewAsync(
            Arg.Any<Guid>(),
            Arg.Any<CodeReviewRef>(),
            Arg.Any<ReviewRevision>(),
            Arg.Any<ReviewResult>(),
            Arg.Any<ReviewerIdentity>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewPublicationContext?>());
        await protocolRecorder.DidNotReceive().RecordPublicationEventAsync(
            Arg.Any<Guid>(),
            Arg.Is<string>(name => name == "publication_withheld_findings"),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        // The review itself is still persisted in full.
        await jobs.Received(1).SetResultAsync(
            job.Id,
            Arg.Is<ReviewResult>(result => result.Comments.Count == 2 && result.Summary == "Summary."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithheldFindings_WhenEveryFindingIsWithheld_StillPublishTheSummaryWithNoComments()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetWithholdOutOfScopeFindingsAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var orchestratorResult = new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new(OutsideChangeFile, 3, CommentSeverity.Warning, "In pre-existing code.")
                {
                    ScopeRelation = ReviewCommentScopeRelation.OutsideChange,
                },
                new("src/Other.cs", 9, CommentSeverity.Error, "Also pre-existing.")
                {
                    ScopeRelation = ReviewCommentScopeRelation.OutsideChange,
                },
            }.AsReadOnly());

        var commentPoster = CreatePublicationService();
        var (service, _) = CreateService(
            jobs,
            clientRegistry,
            orchestratorResult,
            commentPoster,
            publicUiOrigin: PublicUiOrigin);

        await service.ProcessAsync(job, CancellationToken.None);

        // Publication still runs with nothing to post inline. Skipping it would leave the pull request with no
        // account of the review at all, which is the one outcome the count exists to prevent.
        await commentPoster.Received(1).PublishReviewAsync(
            job.ClientId,
            job.CodeReviewReference,
            Arg.Any<ReviewRevision>(),
            Arg.Is<ReviewResult>(result =>
                result.Comments.Count == 0
                && result.Summary.Contains("2 findings are held back", StringComparison.Ordinal)),
            Arg.Any<ReviewerIdentity>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewPublicationContext?>());
    }

    private static ReviewResult BuildResultWithOneFindingOfEachScope()
    {
        return new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new(WarningFile, 1, CommentSeverity.Warning, "On a line this change touched.")
                {
                    ScopeRelation = ReviewCommentScopeRelation.OnChangedLine,
                },
                new(OutsideChangeFile, 3, CommentSeverity.Warning, "In pre-existing code.")
                {
                    ScopeRelation = ReviewCommentScopeRelation.OutsideChange,
                },
            }.AsReadOnly());
    }
}
