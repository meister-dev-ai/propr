// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class LinkedItemContextBoundingTests
{
    private static LinkedItem Item(string key, string? description = null)
    {
        return new LinkedItem(key, "Issue", $"Title {key}", description, null, []);
    }

    [Fact]
    public void Bound_DeduplicatesByProviderKey_PreservingFirstOccurrenceOrder()
    {
        var items = new[] { Item("1"), Item("2"), Item("1"), Item("3") };

        var result = LinkedItemContextBounding.Bound(items, maxItems: 10, maxDescriptionChars: 2000, out var dropped);

        Assert.Equal(new[] { "1", "2", "3" }, result.Select(i => i.ProviderKey));
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void Bound_CapsItemCount_AndReportsDropped()
    {
        var items = new[] { Item("1"), Item("2"), Item("3"), Item("4") };

        var result = LinkedItemContextBounding.Bound(items, maxItems: 2, maxDescriptionChars: 2000, out var dropped);

        Assert.Equal(new[] { "1", "2" }, result.Select(i => i.ProviderKey));
        Assert.Equal(2, dropped);
    }

    [Fact]
    public void Bound_CountsDroppedAfterDeduplication()
    {
        var items = new[] { Item("1"), Item("1"), Item("2"), Item("3") };

        var result = LinkedItemContextBounding.Bound(items, maxItems: 2, maxDescriptionChars: 2000, out var dropped);

        // Three distinct items, cap of two, so exactly one distinct item is dropped.
        Assert.Equal(2, result.Count);
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void Bound_TruncatesDescriptionsOverTheCap()
    {
        var longBody = new string('x', 5000);
        var items = new[] { Item("1", longBody) };

        var result = LinkedItemContextBounding.Bound(items, maxItems: 5, maxDescriptionChars: 100, out _);

        Assert.NotNull(result[0].Description);
        Assert.Equal(101, result[0].Description!.Length); // 100 chars + the single ellipsis marker
        Assert.EndsWith("…", result[0].Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Bound_LeavesShortDescriptionsUntouched()
    {
        var items = new[] { Item("1", "short") };

        var result = LinkedItemContextBounding.Bound(items, maxItems: 5, maxDescriptionChars: 100, out _);

        Assert.Equal("short", result[0].Description);
    }
}
