// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Api.Tests.Workers;

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

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance);

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
        scopeFactory.CreateScope()
            .Returns(_ =>
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

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        var ex = await Record.ExceptionAsync(async () =>
        {
            await channel.Writer.WriteAsync(
                new MentionReplyJob(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "https://dev.azure.com/org",
                    "proj",
                    "repo",
                    7,
                    "3",
                    11,
                    "@bot please help"));
            await processingAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        });

        Assert.Null(ex);
        Assert.True(scopeCreationCount >= 2, "the worker should have continued to the next polling cycle after skipping the job");
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_RewritesProvenanceAnEarlierRunLost()
    {
        // A process that died between completing a mention job and recording who posted its answer left the
        // answer attributable to nobody, and this startup is the event that follows it. The same moment already
        // recovers jobs that were in flight, so it is where the lost bookkeeping is recovered too.
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns((IServiceScope)scope);

        var repo = Substitute.For<IMentionReplyJobRepository>();
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([]);
        var reconciler = Substitute.For<IMentionReplyProvenanceReconciler>();
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyProvenanceReconciler)).Returns(reconciler);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns((object?)null);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        await reconciler.Received(1).ReconcileAsync(Arg.Any<CancellationToken>());
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenRewritingProvenanceFails_StillHydratesThePendingJobs()
    {
        // Recovery bookkeeping runs after hydration and cannot undo it: an old provenance row that could not be
        // rewritten must not cost the worker the jobs it is meant to be working now.
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns((IServiceScope)scope);

        var pending = new MentionReplyJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo",
            7,
            "3",
            11,
            "@bot please help");

        var repo = Substitute.For<IMentionReplyJobRepository>();
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([pending]);
        var reconciler = Substitute.For<IMentionReplyProvenanceReconciler>();
        reconciler.ReconcileAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provenance store unavailable"));
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyProvenanceReconciler)).Returns(reconciler);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns((object?)null);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance);

        var ex = await Record.ExceptionAsync(() => worker.StartAsync(CancellationToken.None));

        Assert.Null(ex);
        await repo.Received(1).GetPendingAsync(Arg.Any<CancellationToken>());
        await worker.StopAsync(CancellationToken.None);
    }
}
