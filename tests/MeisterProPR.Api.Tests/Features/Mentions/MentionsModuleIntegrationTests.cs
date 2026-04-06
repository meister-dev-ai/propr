// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.Mentions;

public sealed class MentionsModuleIntegrationTests
{
    [Fact]
    public async Task StartAsync_WhenPendingJobsExist_ResetsAndReadsRepositoryBacklog()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var repo = Substitute.For<IMentionReplyJobRepository>();
        var pendingJob = new MentionReplyJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 7, 3, 11, "@bot please help");

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateAsyncScope().Returns(new AsyncServiceScope((IServiceScope)scope));
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([pendingJob]);
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns((object?)null);

        var worker = new MentionReplyWorker(channel.Reader, channel.Writer, scopeFactory, NullLogger<MentionReplyWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50, CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        await repo.Received(1).ResetStuckProcessingAsync(Arg.Any<CancellationToken>());
        await repo.Received(1).GetPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenReplyServiceIsRegistered_ProcessesQueuedJob()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var repo = Substitute.For<IMentionReplyJobRepository>();
        var replyService = Substitute.For<IMentionReplyService>();
        var job = new MentionReplyJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 7, 3, 11, "@bot please help");

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateAsyncScope().Returns(new AsyncServiceScope((IServiceScope)scope));
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([]);
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns(replyService);

        var worker = new MentionReplyWorker(channel.Reader, channel.Writer, scopeFactory, NullLogger<MentionReplyWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await channel.Writer.WriteAsync(job, CancellationToken.None);
        await Task.Delay(100, CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        await replyService.Received(1).ProcessAsync(job, Arg.Any<CancellationToken>());
    }
}
