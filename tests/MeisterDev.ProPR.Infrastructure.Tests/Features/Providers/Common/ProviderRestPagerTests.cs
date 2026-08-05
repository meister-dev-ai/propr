// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Common;

public sealed class ProviderRestPagerTests
{
    [Fact]
    public async Task LoadAllAsync_ProviderReportsAnotherPage_ReturnsTheUnionOfEveryPage()
    {
        var requestedPages = new List<int>();

        var items = await ProviderRestPager.LoadAllAsync(
            (page, pageSize, _) =>
            {
                requestedPages.Add(page);

                return Task.FromResult(
                    page switch
                    {
                        1 => new ProviderRestPager.RestPage<string>(Fill("a", pageSize), true),
                        _ => new ProviderRestPager.RestPage<string>(["b1", "b2"], false),
                    });
            },
            item => item,
            "the collection",
            CancellationToken.None,
            pageSize: 3);

        Assert.Equal([1, 2], requestedPages);
        Assert.Equal(["a1", "a2", "a3", "b1", "b2"], items);
    }

    [Fact]
    public async Task LoadAllAsync_CollectionFitsInOnePage_AsksOnce()
    {
        var requestCount = 0;

        var items = await ProviderRestPager.LoadAllAsync(
            (_, _, _) =>
            {
                requestCount++;

                return Task.FromResult(new ProviderRestPager.RestPage<string>(["only"]));
            },
            item => item,
            "the collection",
            CancellationToken.None,
            pageSize: 3);

        Assert.Equal(1, requestCount);
        Assert.Equal(["only"], items);
    }

    /// <summary>
    ///     The reason the read follows a reported total rather than counting requests. A host free to serve a
    ///     smaller page than the one asked for, as Forgejo is, would otherwise look like it had run out of items
    ///     on its very first answer.
    /// </summary>
    [Fact]
    public async Task LoadAllAsync_HostServesSmallerPagesThanAskedFor_StillReadsTheWholeCollection()
    {
        var requestedPages = new List<int>();

        var items = await ProviderRestPager.LoadAllAsync(
            (page, _, _) =>
            {
                requestedPages.Add(page);

                // Asked for ten, serves two, and says the collection holds five.
                return Task.FromResult(
                    new ProviderRestPager.RestPage<string>(
                        page < 3 ? [$"p{page}a", $"p{page}b"] : ["p3a"],
                        TotalCount: 5));
            },
            item => item,
            "the collection",
            CancellationToken.None,
            pageSize: 10);

        Assert.Equal([1, 2, 3], requestedPages);
        Assert.Equal(["p1a", "p1b", "p2a", "p2b", "p3a"], items);
    }

    [Fact]
    public async Task LoadAllAsync_PagesOverlap_ReturnsEachItemOnceAndKeepsReading()
    {
        var items = await ProviderRestPager.LoadAllAsync(
            (page, _, _) => Task.FromResult(
                page switch
                {
                    1 => new ProviderRestPager.RestPage<string>(["a", "b"], true),
                    _ => new ProviderRestPager.RestPage<string>(["b", "c"], false),
                }),
            item => item,
            "the collection",
            CancellationToken.None,
            pageSize: 2);

        Assert.Equal(["a", "b", "c"], items);
    }

    [Fact]
    public async Task LoadAllAsync_HostRepeatsAPage_FailsRatherThanReturningAnUnknownSubset()
    {
        var requestCount = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ProviderRestPager.LoadAllAsync(
            (_, pageSize, _) =>
            {
                requestCount++;

                return Task.FromResult(new ProviderRestPager.RestPage<string>(Fill("a", pageSize), true));
            },
            item => item,
            "the collection",
            CancellationToken.None,
            pageSize: 2));

        // Stopped at the repeat rather than spinning to the bound.
        Assert.Equal(2, requestCount);
        Assert.Contains("already served", exception.Message, StringComparison.Ordinal);
        Assert.Contains("the collection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAllAsync_MoreRemainsAtTheBound_FailsRatherThanTruncating()
    {
        var page = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ProviderRestPager.LoadAllAsync(
            (_, _, _) =>
            {
                page++;

                return Task.FromResult(new ProviderRestPager.RestPage<string>([$"item{page}"], true));
            },
            item => item,
            "the collection",
            CancellationToken.None,
            pageSize: 1,
            maxPages: 4));

        Assert.Equal(4, page);
        Assert.Contains("exceeds the 4 items", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be reviewed completely", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAllAsync_ProviderRunsOutOfItemsWithoutSayingSo_EndsOnTheEmptyPage()
    {
        var items = await ProviderRestPager.LoadAllAsync(
            (page, pageSize, _) => Task.FromResult(
                page == 1
                    ? new ProviderRestPager.RestPage<string>(Fill("a", pageSize), true)
                    : new ProviderRestPager.RestPage<string>([], true)),
            item => item,
            "the collection",
            CancellationToken.None,
            pageSize: 2);

        Assert.Equal(["a1", "a2"], items);
    }

    [Fact]
    public async Task TryLoadAllAsync_ReadCannotBeCompleted_AnswersNull()
    {
        var items = await ProviderRestPager.TryLoadAllAsync(
            (_, pageSize, _) => Task.FromResult(new ProviderRestPager.RestPage<string>(Fill("a", pageSize), true)),
            item => item,
            "the collection",
            CancellationToken.None,
            pageSize: 2);

        Assert.Null(items);
    }

    [Fact]
    public async Task TryLoadAllAsync_ReadCompletes_AnswersTheUnion()
    {
        var items = await ProviderRestPager.TryLoadAllAsync(
            (_, _, _) => Task.FromResult(new ProviderRestPager.RestPage<string>(["a"], false)),
            item => item,
            "the collection",
            CancellationToken.None,
            pageSize: 2);

        Assert.Equal(["a"], items!);
    }

    private static IReadOnlyList<string> Fill(string prefix, int count)
    {
        return Enumerable.Range(1, count).Select(index => $"{prefix}{index}").ToList();
    }
}
