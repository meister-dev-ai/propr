// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Workers;

public sealed class MentionReplyWorkerTests
{
    [Fact]
    public async Task StartAsync_WhenRepositoryMissing_DoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns((IServiceScope)scope);
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns((object?)null);

        var worker = new MentionReplyWorker(channel.Reader, channel.Writer, scopeFactory, NullLogger<MentionReplyWorker>.Instance);

        var ex = await Record.ExceptionAsync(() => worker.StartAsync(CancellationToken.None));

        Assert.Null(ex);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReplyServiceMissing_SkipsJobWithoutThrowing()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var processingAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopeCreationCount = 0;

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(_ =>
        {
            scopeCreationCount++;
            if (scopeCreationCount >= 2)
            {
                processingAttempted.TrySetResult();
            }

            return (IServiceScope)scope;
        });

        var repo = Substitute.For<IMentionReplyJobRepository>();
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([]);
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns((object?)null);

        var worker = new MentionReplyWorker(channel.Reader, channel.Writer, scopeFactory, NullLogger<MentionReplyWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await channel.Writer.WriteAsync(new MentionReplyJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 7, 3, 11, "@bot please help"));
        await processingAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await worker.StopAsync(CancellationToken.None);
    }
}
