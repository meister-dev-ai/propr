// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Common;

public sealed class ProviderCursorPagerTests
{
    [Fact]
    public async Task LoadAllAsync_ConnectionHasAnotherPage_ResumesFromItsCursorAndReturnsTheUnion()
    {
        var requestedCursors = new List<string?>();

        var nodes = await ProviderCursorPager.LoadAllAsync(
            (cursor, _) =>
            {
                requestedCursors.Add(cursor);

                return Task.FromResult(
                    cursor switch
                    {
                        null => new ProviderCursorPager.CursorPage<string>(["a", "b"], true, "cursor-1"),
                        "cursor-1" => new ProviderCursorPager.CursorPage<string>(["c"], true, "cursor-2"),
                        _ => new ProviderCursorPager.CursorPage<string>(["d"], false, "cursor-3"),
                    });
            },
            "the connection",
            CancellationToken.None);

        Assert.Equal([null, "cursor-1", "cursor-2"], requestedCursors);
        Assert.Equal(["a", "b", "c", "d"], nodes);
    }

    [Fact]
    public async Task LoadAllAsync_ConnectionFitsInOnePage_AsksOnce()
    {
        var requestCount = 0;

        var nodes = await ProviderCursorPager.LoadAllAsync(
            (_, _) =>
            {
                requestCount++;

                return Task.FromResult(new ProviderCursorPager.CursorPage<string>(["only"], false, null));
            },
            "the connection",
            CancellationToken.None);

        Assert.Equal(1, requestCount);
        Assert.Equal(["only"], nodes);
    }

    [Fact]
    public async Task LoadAllAsync_NextPageWithoutACursor_FailsRatherThanRereadingTheSamePage()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ProviderCursorPager.LoadAllAsync(
            (_, _) => Task.FromResult(new ProviderCursorPager.CursorPage<string>(["a"], true, null)),
            "the connection",
            CancellationToken.None));

        Assert.Contains("already served", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAllAsync_CursorDoesNotAdvance_FailsRatherThanRereadingTheSamePage()
    {
        var requestCount = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ProviderCursorPager.LoadAllAsync(
            (_, _) =>
            {
                requestCount++;

                return Task.FromResult(new ProviderCursorPager.CursorPage<string>(["a"], true, "stuck"));
            },
            "the connection",
            CancellationToken.None));

        // First page takes the cursor, the second is handed the same one back and stops there.
        Assert.Equal(2, requestCount);
        Assert.Contains("the connection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAllAsync_MoreRemainsAtTheBound_FailsRatherThanTruncating()
    {
        var page = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ProviderCursorPager.LoadAllAsync(
            (_, _) =>
            {
                page++;

                return Task.FromResult(
                    new ProviderCursorPager.CursorPage<string>(
                        [$"node{page}"],
                        true,
                        $"cursor-{page}"));
            },
            "the connection",
            CancellationToken.None,
            maxPages: 3));

        Assert.Equal(3, page);
        Assert.Contains("exceeds the 3 items", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be reviewed completely", exception.Message, StringComparison.Ordinal);
    }
}
