// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using MeisterProPR.Api.Features.ProCursor;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.ProCursor;

public sealed class ManagedRemoteProCursorGatewayTests
{
    [Fact]
    public async Task CreateSourceAsync_InvalidatesRemoteRuntimeConfiguration()
    {
        var clientId = Guid.NewGuid();
        var knowledgeSourceRepository = Substitute.For<IProCursorKnowledgeSourceRepository>();
        knowledgeSourceRepository.ExistsAsync(
                Arg.Any<Guid>(),
                Arg.Any<ProCursorSourceKind>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        ProCursorKnowledgeSource? persistedSource = null;
        knowledgeSourceRepository.AddAsync(Arg.Do<ProCursorKnowledgeSource>(candidate => persistedSource = candidate), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        Uri? requestUri = null;
        using var httpClient = new HttpClient(new StubHandler(request => requestUri = request.RequestUri))
        {
            BaseAddress = new Uri("http://procursor.internal/")
        };
        var remoteGateway = new HttpProCursorGateway(httpClient, NullLogger<HttpProCursorGateway>.Instance);

        var gateway = new ManagedRemoteProCursorGateway(knowledgeSourceRepository, remoteGateway);

        await gateway.CreateSourceAsync(
            clientId,
            new ProCursorKnowledgeSourceRegistrationRequest(
                "Repo A",
                ProCursorSourceKind.Repository,
                "https://dev.azure.com/test-org",
                "project-a",
                "repo-a",
                "main",
                null,
                "auto",
                [new ProCursorTrackedBranchCreateRequest("main", ProCursorRefreshTriggerMode.BranchUpdate, true)],
                null,
                null,
                "repo-a"),
            CancellationToken.None);

        Assert.NotNull(persistedSource);
        Assert.Equal(
            $"http://procursor.internal/internal/procursor/runtime-config/sources/{persistedSource!.Id:D}/invalidate",
            requestUri?.AbsoluteUri);
    }

    private sealed class StubHandler(Action<HttpRequestMessage> onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { })
            });
        }
    }
}
