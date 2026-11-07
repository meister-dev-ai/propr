// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.RestartReviewJob;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitHub;

/// <summary>
///     A review whose file reviews failed, from the review job to the review that ProPR posts on a GitHub pull request.
///     When no file was reviewed, ProPR posts nothing and fails the job with the failed files in its error, and a
///     restart after the cause is fixed posts the normal review. When some file reviews failed, the posted summary
///     lists the files without a review.
/// </summary>
public sealed class GitHubFailedFileReviewPublicationTests
{
    private const string SourceFile = "src/durations.ts";
    private const string TestFile = "test/durations.test.mjs";
    private const string NoFindingsRemained = "No publishable or summary-only findings remained after verification.";

    // The second case is the result the scan handed over before the change: a summary written over no reviewed file,
    // grounded with the line that no findings remained.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReviewInWhichEveryFileReviewFailed_PostsNothingAndFailsTheJobWithTheFailedFiles(bool summaryOverNoFile)
    {
        var fixture = new Fixture();
        var job = await fixture.AddJobAsync();
        var partial = summaryOverNoFile
            ? new ReviewResult($"The pull request changes the duration parser.\n\n**{NoFindingsRemained}**", [])
            : null;
        fixture.ReviewFails(new PartialReviewFailureException(2, 2, [ModelRefused(), ModelRefused()], partial, [SourceFile, TestFile], 0));

        await fixture.Service.ProcessAsync(job, CancellationToken.None);

        Assert.Empty(fixture.PostedReviewBodies);
        var failed = fixture.Jobs.GetById(job.Id)!;
        Assert.Equal(JobStatus.Failed, failed.Status);
        Assert.Contains(SourceFile, failed.ErrorMessage);
        Assert.Contains(TestFile, failed.ErrorMessage);
        Assert.Contains("HTTP 400", failed.ErrorMessage);
    }

    [Fact]
    public async Task AReviewInWhichSomeFileReviewsFailed_PostsASummaryThatListsTheFilesWithoutAReview()
    {
        var fixture = new Fixture();
        var job = await fixture.AddJobAsync();
        var partial = new ReviewResult($"The parser converts durations.\n\n**{NoFindingsRemained}**", []) { FailedReviewFilePaths = [TestFile] };
        fixture.ReviewFails(new PartialReviewFailureException(1, 2, [ModelRefused()], partial, [TestFile], 1));

        await fixture.Service.ProcessAsync(job, CancellationToken.None);

        var body = Assert.Single(fixture.PostedReviewBodies);
        Assert.Contains("The parser converts durations.", body);
        Assert.Contains("Review failed for 1 file", body);
        Assert.Contains($"- {TestFile}", body);
        Assert.DoesNotContain($"- {SourceFile}", body);
        Assert.Equal(JobStatus.Completed, fixture.Jobs.GetById(job.Id)!.Status);
    }

    [Fact]
    public async Task AReviewInWhichNoFileReviewFailed_PostsASummaryWithoutFailedFiles()
    {
        var fixture = new Fixture();
        var job = await fixture.AddJobAsync();
        fixture.ReviewReturns(new ReviewResult("The parser converts durations.", []));

        await fixture.Service.ProcessAsync(job, CancellationToken.None);

        var body = Assert.Single(fixture.PostedReviewBodies);
        Assert.Contains("The parser converts durations.", body);
        Assert.DoesNotContain("Review failed for", body);
        Assert.Equal(JobStatus.Completed, fixture.Jobs.GetById(job.Id)!.Status);
    }

    [Fact]
    public async Task ARestartAfterTheCauseIsFixed_PostsTheNormalReview()
    {
        var fixture = new Fixture();
        var job = await fixture.AddJobAsync();
        fixture.ReviewFails(new PartialReviewFailureException(2, 2, [ModelRefused(), ModelRefused()], null, [SourceFile, TestFile], 0));
        await fixture.Service.ProcessAsync(job, CancellationToken.None);

        // The configuration is fixed, so the model endpoint accepts the requests of the restarted job.
        fixture.ReviewReturns(new ReviewResult("The parser converts durations.", []));
        var restart = await new RestartReviewJobHandler(
                fixture.Jobs,
                Substitute.For<IReviewExecutionQueue>(),
                Substitute.For<ILogger<RestartReviewJobHandler>>())
            .HandleAsync(new RestartReviewJobCommand(job.Id));
        Assert.Equal(RestartReviewJobOutcome.Restarted, restart.Outcome);
        var restarted = fixture.Jobs.GetById(restart.NewJobId!.Value)!;
        await fixture.Service.ProcessAsync(restarted, CancellationToken.None);

        var body = Assert.Single(fixture.PostedReviewBodies);
        Assert.Contains("The parser converts durations.", body);
        Assert.DoesNotContain("Review failed for", body);
        Assert.Equal(JobStatus.Completed, fixture.Jobs.GetById(restarted.Id)!.Status);
    }

    private static InvalidOperationException ModelRefused()
    {
        return new InvalidOperationException("HTTP 400 (invalid_request_error: Unsupported parameter)");
    }

    /// <summary>
    ///     The review pipeline from the job to the GitHub API. The scan is a substitute that answers as the
    ///     file-by-file scan does, the job store keeps the jobs in memory, and the GitHub publication posts to a stub
    ///     of the GitHub API that records each review body.
    /// </summary>
    private sealed class Fixture
    {
        private readonly Guid _clientId = Guid.NewGuid();
        private readonly ProviderHostRef _host = new(ScmProvider.GitHub, "https://github.com");
        private readonly IFileByFileReviewOrchestrator _scan = Substitute.For<IFileByFileReviewOrchestrator>();
        private string? _watermark;

        public Fixture()
        {
            var store = new ReviewJobExecutionStoreAdapter(this.Jobs);
            this.Service = new ReviewOrchestrationService(
                store,
                this.CreatePullRequestFetcher(),
                this.CreateProviderRegistry(),
                this.CreateClientRegistry(),
                this.CreateScanStore(),
                Substitute.For<IProtocolRecorder>(),
                CreateToolsFactory(),
                CreateInstructionFetcher(),
                CreateExclusionFetcher(),
                Substitute.For<IRepositoryInstructionEvaluator>(),
                Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewRetries = 1 }),
                Substitute.For<ILogger<ReviewOrchestrationService>>(),
                CreateRuntimeResolver(),
                this._scan,
                workspaceManager: CreateWorkspaceManager());
        }

        public InMemoryReviewJobRepository Jobs { get; } = new();

        public ReviewOrchestrationService Service { get; }

        public List<string> PostedReviewBodies { get; } = [];

        public async Task<ReviewJob> AddJobAsync()
        {
            var job = new ReviewJob(Guid.NewGuid(), this._clientId, "https://github.com/meister-dev-propr-demo", "meister-dev-propr-demo", "101", 1, 1);
            job.SetProviderReviewContext(
                new CodeReviewRef(
                    new RepositoryRef(this._host, "101", "meister-dev-propr-demo", "meister-dev-propr-demo/propr-lens-demo"),
                    CodeReviewPlatformKind.PullRequest,
                    "1",
                    1));
            job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha"));
            await this.Jobs.AddAsync(job);
            return job;
        }

        public void ReviewFails(PartialReviewFailureException failure)
        {
            this._scan
                .ReviewAsync(
                    Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>())
                .ThrowsAsync(failure);
        }

        public void ReviewReturns(ReviewResult result)
        {
            this._scan
                .ReviewAsync(
                    Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>())
                .Returns(result);
        }

        private IPullRequestFetcher CreatePullRequestFetcher()
        {
            var fetcher = Substitute.For<IPullRequestFetcher>();
            fetcher.FetchRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(new PullRequestRef("feature/durations", "main", PrStatus.Active));
            fetcher.FetchAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>(), Arg.Any<ReviewRevision?>(), Arg.Any<IReviewRepositoryWorkspace?>())
                .Returns(call => new PullRequest(
                    call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<string>(2), call.ArgAt<int>(3), call.ArgAt<int>(4),
                    "Parse durations", null, "feature/durations", "main",
                    new List<ChangedFile>
                    {
                        new(SourceFile, ChangeType.Edit, "content", "diff"),
                        new(TestFile, ChangeType.Edit, "content", "diff"),
                    }.AsReadOnly()));
            return fetcher;
        }

        private IScmProviderRegistry CreateProviderRegistry()
        {
            var publication = this.CreateGitHubPublication();
            var registry = LocalScmPolicies.CreateRuntimeSubstitute();
            registry.GetCodeReviewPublicationService(ScmProvider.GitHub).Returns(publication);
            registry.GetRegisteredCapabilities(Arg.Any<ScmProvider>()).Returns([]);
            return registry;
        }

        private GitHubCodeReviewPublicationService CreateGitHubPublication()
        {
            var connections = Substitute.For<IClientScmConnectionRepository>();
            connections.GetOperationalConnectionAsync(this._clientId, this._host, Arg.Any<CancellationToken>())
                .Returns(
                    new ClientScmConnectionCredentialDto(
                        Guid.NewGuid(),
                        this._clientId,
                        ScmProvider.GitHub,
                        this._host.HostBaseUrl,
                        ScmAuthenticationKind.PersonalAccessToken,
                        "GitHub",
                        "ghp_test",
                        true));

            var httpClientFactory = Substitute.For<IHttpClientFactory>();
            httpClientFactory.CreateClient("GitHubProvider")
                .Returns(_ => new HttpClient(new GitHubApiStub(this.PostedReviewBodies)));
            return new GitHubCodeReviewPublicationService(
                new GitHubConnectionVerifier(connections, httpClientFactory),
                httpClientFactory,
                TestPostedCommentComposer.Default);
        }

        private IClientRegistry CreateClientRegistry()
        {
            var registry = Substitute.For<IClientRegistry>();
            registry.GetScmCommentPostingEnabledAsync(this._clientId, Arg.Any<CancellationToken>()).Returns(true);
            registry.GetCommentResolutionBehaviorAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(CommentResolutionBehavior.Silent);
            registry.GetEffectiveReviewerIdentityAsync(Arg.Any<Guid>(), Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
                .Returns(call => new ReviewerIdentity(call.ArgAt<ProviderHostRef>(1), "99", "propr-bot", "ProPR", true));
            return registry;
        }

        // The watermark marks a revision as reviewed. A restart of a revision the store already marks is skipped as
        // unchanged, so the store keeps what the service writes.
        private IReviewPrScanRepository CreateScanStore()
        {
            var scans = Substitute.For<IReviewPrScanRepository>();
            scans.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => this._watermark is null
                    ? null
                    : new ReviewPrScan(
                        Guid.NewGuid(), call.ArgAt<Guid>(0), call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<string>(3), call.ArgAt<int>(4),
                        this._watermark));
            scans.SetReviewWatermarkAsync(
                    Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    this._watermark = call.ArgAt<string>(5);
                    return Task.CompletedTask;
                });
            return scans;
        }

        private static IReviewContextToolsFactory CreateToolsFactory()
        {
            var factory = Substitute.For<IReviewContextToolsFactory>();
            factory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(Substitute.For<IReviewContextTools>());
            return factory;
        }

        private static IRepositoryInstructionFetcher CreateInstructionFetcher()
        {
            var fetcher = Substitute.For<IRepositoryInstructionFetcher>();
            fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));
            return fetcher;
        }

        private static IRepositoryExclusionFetcher CreateExclusionFetcher()
        {
            var fetcher = Substitute.For<IRepositoryExclusionFetcher>();
            fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(ReviewExclusionRules.Empty));
            return fetcher;
        }

        private static IReviewRepositoryWorkspaceManager CreateWorkspaceManager()
        {
            var workspace = Substitute.For<IReviewRepositoryWorkspace>();
            workspace.DisposeAsync().Returns(ValueTask.CompletedTask);
            var manager = Substitute.For<IReviewRepositoryWorkspaceManager>();
            manager.PrepareAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ReviewRepositoryWorkspacePreparationResult(workspace, null));
            return manager;
        }

        private static IAiRuntimeResolver CreateRuntimeResolver()
        {
            var model = AiConnectionTestFactory.CreateChatModel("gpt-5");
            var runtime = Substitute.For<IResolvedAiChatRuntime>();
            runtime.ChatClient.Returns(Substitute.For<IChatClient>());
            runtime.Model.Returns(model);
            runtime.Connection.Returns(
                AiConnectionTestFactory.CreateConnection(Guid.NewGuid(), [model], [AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, model)]));
            runtime.Capabilities.Returns(new AgentReviewRuntimeCapabilities(false, false, false, false));

            var resolver = Substitute.For<IAiRuntimeResolver>();
            resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>()).Returns(runtime);
            return resolver;
        }
    }

    /// <summary>The GitHub API as the review publication calls it. It records the body of every review posted.</summary>
    private sealed class GitHubApiStub(List<string> postedReviewBodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == "https://api.github.com/user")
            {
                return Json(new { login = "propr-bot" });
            }

            if (request.Method == HttpMethod.Post && uri.EndsWith("/pulls/1/reviews", StringComparison.Ordinal))
            {
                using var posted = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                lock (postedReviewBodies)
                {
                    postedReviewBodies.Add(posted.RootElement.GetProperty("body").GetString()!);
                }

                return Json(new { id = 5807456012 });
            }

            return Json(new { message = "Not Found" }, HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T payload, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(statusCode) { Content = new StringContent(JsonSerializer.Serialize(payload)) };
        }
    }
}
