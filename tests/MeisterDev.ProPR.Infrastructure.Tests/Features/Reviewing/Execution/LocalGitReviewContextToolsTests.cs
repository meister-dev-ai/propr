// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class LocalGitReviewContextToolsTests
{
    [Fact]
    public void NativeSourcePolicyOwnsBoundedSymbolContextPreparation()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry;
        var repository = new RepositoryRef(new(ScmProvider.AzureDevOps, "https://host.test"), "repository", "project", "repository");
        Assert.Equal(
            new ProCursorReviewContextDto("repository", "refs/heads/feature", 42, 7),
            registry.GetSourceIdentityPolicy(ScmProvider.AzureDevOps).PrepareProCursorSymbolContext(repository, "refs/heads/feature", 42, 7));
        Assert.Null(registry.GetSourceIdentityPolicy(ScmProvider.GitHub).PrepareProCursorSymbolContext(repository, "refs/heads/feature", 42, 7));
    }

    [Fact]
    public void OfflineToolsRequireExplicitPreparedSymbolCoordinates()
    {
        Assert.Contains(
            typeof(LocalGitReviewContextTools).GetConstructors().SelectMany(ctor => ctor.GetParameters()),
            parameter => parameter.ParameterType == typeof(ProCursorReviewContextDto));
    }

    [Fact]
    public async Task SymbolQueriesConsumePreparedCoordinatesAndRetainDefaultQueryMode()
    {
        var gateway = Substitute.For<IProCursorGateway>();
        gateway.GetSymbolInsightAsync(Arg.Any<ProCursorSymbolQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto("current", null, true, true, null, []));
        var prepared = new ProCursorReviewContextDto("native-repository", "refs/heads/native", 99, 12);
        var repository = new RepositoryRef(new(ScmProvider.AzureDevOps, "https://host.test"), "captured-repository", "project", "repository");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var clientId = Guid.NewGuid();
        var tools = new LocalGitReviewContextTools(
            prepared, new StubWorkspace(), gateway,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            new ReviewContextToolsRequest(review, "refs/heads/captured", 7, clientId),
            NullLogger<LocalGitReviewContextTools>.Instance, providerRegistry: null);

        await tools.GetProCursorSymbolInfoAsync("Symbol", " ", null, CancellationToken.None);

        await gateway.Received(1).GetSymbolInsightAsync(
            Arg.Is<ProCursorSymbolQueryRequest>(request => request.ClientId == clientId && request.QueryMode == "name"
                                                                                        && request.MaxRelations == null && request.StateMode == "reviewTarget"
                                                                                        && request.ReviewContext == new ProCursorReviewContextDto(
                                                                                            "native-repository", "native", 99, 12)),
            CancellationToken.None);
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, true, true)]
    [InlineData(ScmProvider.AzureDevOps, false, false)]
    [InlineData(ScmProvider.GitHub, true, false)]
    [InlineData(ScmProvider.GitLab, true, false)]
    [InlineData(ScmProvider.Forgejo, true, false)]
    [InlineData((ScmProvider)999, true, false)]
    public async Task OfflineSymbolAvailabilityAndBoundedQueryRemainUnchanged(ScmProvider provider, bool hasClient, bool expectedQuery)
    {
        var gateway = Substitute.For<IProCursorGateway>();
        var expected = new ProCursorSymbolInsightDto("current", null, true, true, null, []);
        gateway.GetSymbolInsightAsync(Arg.Any<ProCursorSymbolQueryRequest>(), Arg.Any<CancellationToken>()).Returns(expected);
        var repository = new RepositoryRef(new(provider, "https://host.test"), "repository", "project", "project/repository");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var clientId = Guid.NewGuid();
        var tools = new LocalGitReviewContextTools(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry.GetSourceIdentityPolicy(provider)
                .PrepareProCursorSymbolContext(repository, "refs/heads/feature", 42, 7), new StubWorkspace(), gateway,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            new ReviewContextToolsRequest(review, "refs/heads/feature", 7, hasClient ? clientId : null),
            NullLogger<LocalGitReviewContextTools>.Instance, providerRegistry: null);

        var result = await tools.GetProCursorSymbolInfoAsync("Symbol", " qualifiedName ", 13, CancellationToken.None);

        if (expectedQuery)
        {
            Assert.Same(expected, result);
            await gateway.Received(1).GetSymbolInsightAsync(
                Arg.Is<ProCursorSymbolQueryRequest>(request => request.ClientId == clientId
                                                               && request.Symbol == "Symbol" && request.QueryMode == "qualifiedName"
                                                               && request.StateMode == "reviewTarget" && request.MaxRelations == 13
                                                               && request.ReviewContext == new ProCursorReviewContextDto("repository", "feature", 42, 7)),
                CancellationToken.None);
        }
        else
        {
            Assert.Equal("unavailable", result.Status);
            await gateway.DidNotReceiveWithAnyArgs().GetSymbolInsightAsync(default!);
        }
    }

    [Fact]
    public async Task LocalTools_ReadChangedFilesTreeAndContent_FromWorkspace()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var workspace = new StubWorkspace();
        var tools = new LocalGitReviewContextTools(
            MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry.GetSourceIdentityPolicy(host.Provider)
                .PrepareProCursorSymbolContext(repository, "feature/demo", 42, 7),
            workspace,
            new DisabledProCursorGateway(),
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileSizeBytes = 1024 * 1024 }),
            new ReviewContextToolsRequest(review, "feature/demo", 7, Guid.NewGuid(), TargetBranch: "main"),
            NullLogger<LocalGitReviewContextTools>.Instance);

        var changedFiles = await tools.GetChangedFilesAsync(CancellationToken.None);
        var tree = await tools.GetFileTreeAsync("feature/demo", CancellationToken.None);
        var content = await tools.GetFileContentAsync("src/Demo.cs", "feature/demo", 2, 3, CancellationToken.None);

        Assert.Single(changedFiles);
        Assert.Equal(["src/Demo.cs"], tree);
        Assert.Equal("line2\nline3", content);
    }

    private sealed class StubWorkspace : IReviewRepositoryWorkspace
    {
        public ReviewRepositoryWorkspaceLease Lease { get; } = new(
            Guid.NewGuid(),
            "workspace-key",
            "/tmp/mirror",
            "/tmp/source",
            "head-sha",
            "base-sha",
            "merge-base",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "Active");

        public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<ChangedFileSummary>>([new ChangedFileSummary("src/Demo.cs", ChangeType.Edit)]);
        }

        public Task<IReadOnlyList<string>> GetFileTreeAsync(string branchSide, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<string>>(["src/Demo.cs"]);
        }

        public Task<string?> ReadFileAsync(string path, string branchSide, CancellationToken ct)
        {
            return Task.FromResult<string?>("line1\nline2\nline3\nline4");
        }

        public Task<int> CountChangedLinesAsync(IReadOnlyCollection<string> paths, CancellationToken ct) => Task.FromResult(0);

        public Task<long> CountDiffBytesAsync(IReadOnlyCollection<string> paths, CancellationToken ct) => Task.FromResult(0L);


        public Task<string?> GetUnifiedDiffAsync(string path, CancellationToken ct)
        {
            return Task.FromResult<string?>("@@ -1,1 +1,2 @@\n-line1\n+line1\n+line2");
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
