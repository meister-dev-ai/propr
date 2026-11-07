// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     File reviews that fail, as the file-by-file scan reports them. A review in which no file was reviewed has
///     nothing to synthesize and carries no partial result. A review in which some file reviews failed carries a
///     partial result over the reviewed files, and the result carries the files whose review failed.
/// </summary>
public sealed class FileByFileReviewOrchestratorFailedFileReviewTests
{
    private const string SourceFile = "src/durations.ts";
    private const string TestFile = "test/durations.test.mjs";

    [Fact]
    public async Task AReviewInWhichEveryFileReviewFailed_SynthesizesNothingAndCarriesTheFailedFiles()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("HTTP 400 (invalid_request_error)"));
        var (sut, job, pr, chatClient) = Build(aiCore);

        var failure = await Assert.ThrowsAsync<PartialReviewFailureException>(() =>
            sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None));

        Assert.Null(failure.PartialResult);
        Assert.Equal(0, failure.ReviewedFileCount);
        Assert.Equal([SourceFile, TestFile], failure.FailedReviewFilePaths.Order());
        Assert.Contains(SourceFile, failure.Message);
        Assert.Contains(TestFile, failure.Message);

        // The synthesis model is not called, so the attempt produces no summary.
        await chatClient.DidNotReceiveWithAnyArgs().GetResponseAsync(default!, default, default);
    }

    [Fact]
    public async Task AReviewInWhichSomeFileReviewsFailed_CarriesAPartialResultWithTheFailedFiles()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Is<PullRequest>(p => p.ChangedFiles[0].Path == SourceFile), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("The parser converts durations.", []));
        aiCore.ReviewAsync(Arg.Is<PullRequest>(p => p.ChangedFiles[0].Path == TestFile), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("HTTP 400 (invalid_request_error)"));
        var (sut, job, pr, _) = Build(aiCore);

        var failure = await Assert.ThrowsAsync<PartialReviewFailureException>(() =>
            sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None));

        Assert.Equal(1, failure.ReviewedFileCount);
        Assert.Equal([TestFile], failure.FailedReviewFilePaths);
        var partial = Assert.IsType<ReviewResult>(failure.PartialResult);
        Assert.Equal([TestFile], partial.FailedReviewFilePaths);
    }

    [Fact]
    public async Task AReviewInWhichNoFileReviewFailed_ReturnsAResultWithoutFailedFiles()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("The parser converts durations.", []));
        var (sut, job, pr, _) = Build(aiCore);

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.Empty(result.FailedReviewFilePaths);
    }

    // The review pass of the test file produces a finding, and the verification of that finding fails. The file
    // review fails as a whole, so its finding is not published and the file is among the failed file reviews.
    [Fact]
    public async Task AFileWhoseVerificationFailsAfterItsReviewPassProducedFindings_CountsAsAFailedFileReview()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Is<PullRequest>(p => p.ChangedFiles[0].Path == SourceFile), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "The parser converts durations.", [new ReviewComment(SourceFile, 4, CommentSeverity.Warning, "Confirmed overflow in parseDuration.")]));
        aiCore.ReviewAsync(Arg.Is<PullRequest>(p => p.ChangedFiles[0].Path == TestFile), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "The tests cover the parser.", [new ReviewComment(TestFile, 9, CommentSeverity.Warning, "Confirmed missing await in the timer test.")]));

        var verifier = Substitute.For<IReviewFindingVerifier>();
        verifier.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(), Arg.Any<ReviewVerificationContext?>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var items = call.ArgAt<IReadOnlyList<VerificationWorkItem>>(0);
                if (items.Any(item => item.Claim.AssertionText.Contains("timer test", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("HTTP 400 (invalid_request_error)");
                }

                return Task.FromResult<IReadOnlyList<VerificationOutcome>>(items.Select(Confirm).ToList());
            });
        var (sut, job, pr, _) = Build(aiCore, verifier);

        var failure = await Assert.ThrowsAsync<PartialReviewFailureException>(() =>
            sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None));

        var partial = Assert.IsType<ReviewResult>(failure.PartialResult);
        Assert.Equal([TestFile], partial.FailedReviewFilePaths);
        Assert.DoesNotContain(partial.Comments, comment => comment.FilePath == TestFile);
    }

    private static VerificationOutcome Confirm(VerificationWorkItem item)
    {
        return new VerificationOutcome(
            item.Claim.ClaimId,
            item.Claim.FindingId,
            VerificationOutcome.SupportedKind,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
            [],
            VerificationOutcome.ModerateEvidence,
            "Confirmed.",
            VerificationOutcome.AiMicroVerifierEvaluator,
            false);
    }

    private static (FileByFileReviewOrchestrator Sut, ReviewJob Job, PullRequest Pr, IChatClient ChatClient) Build(
        IAiReviewCore aiCore,
        IReviewFindingVerifier? verifier = null)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Parse durations",
            null,
            "feature/durations",
            "main",
            new[] { SourceFile, TestFile }.Select(path => new ChangedFile(path, ChangeType.Edit, "content", "diff")).ToList().AsReadOnly());

        // The results the file reviewer stores are the results the scan reads back, as the database does.
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                job.FileReviewResults.Add(call.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });

        var recorder = Substitute.For<IProtocolRecorder>();
        recorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            recorder,
            repo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, MaxFileReviewRetries = 3, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            reviewClaimExtractor: verifier is null ? null : new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: verifier);
        return (sut, job, pr, chatClient);
    }
}
