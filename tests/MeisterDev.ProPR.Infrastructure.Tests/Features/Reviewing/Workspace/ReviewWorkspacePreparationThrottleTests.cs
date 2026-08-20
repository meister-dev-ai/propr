// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Workspace;

public sealed class ReviewWorkspacePreparationThrottleTests
{
    [Fact]
    public async Task EnterAsync_HoldsCallersBackOnceTheConfiguredSlotsAreTaken()
    {
        using var throttle = CreateThrottle(maxConcurrentPreparations: 1);

        var firstSlot = await throttle.EnterAsync(CancellationToken.None);
        var secondSlot = throttle.EnterAsync(CancellationToken.None);

        Assert.False(secondSlot.IsCompleted, "the second preparation waits for the first to finish");

        firstSlot.Dispose();
        (await secondSlot).Dispose();
    }

    [Fact]
    public async Task EnterAsync_AdmitsAsManyPreparationsAsConfigured()
    {
        using var throttle = CreateThrottle(maxConcurrentPreparations: 2);

        var firstSlot = await throttle.EnterAsync(CancellationToken.None);
        var secondSlot = await throttle.EnterAsync(CancellationToken.None);
        var thirdSlot = throttle.EnterAsync(CancellationToken.None);

        Assert.False(thirdSlot.IsCompleted);

        firstSlot.Dispose();
        (await thirdSlot).Dispose();
        secondSlot.Dispose();
    }

    [Fact]
    public async Task Slot_DisposedTwice_GivesBackOneSlot()
    {
        using var throttle = CreateThrottle(maxConcurrentPreparations: 1);

        var slot = await throttle.EnterAsync(CancellationToken.None);
        slot.Dispose();
        slot.Dispose();

        // A slot returned twice would raise the ceiling for the rest of the process lifetime.
        var next = await throttle.EnterAsync(CancellationToken.None);
        var queued = throttle.EnterAsync(CancellationToken.None);
        Assert.False(queued.IsCompleted);

        next.Dispose();
        (await queued).Dispose();
    }

    private static ReviewWorkspacePreparationThrottle CreateThrottle(int maxConcurrentPreparations)
    {
        return new ReviewWorkspacePreparationThrottle(
            Microsoft.Extensions.Options.Options.Create(new ReviewWorkspaceOptions { MaxConcurrentPreparations = maxConcurrentPreparations }));
    }
}
