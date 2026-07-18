// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Features.Reviewing.Offline;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Offline;

public sealed class InMemoryBlockedPullRequestStoreTests
{
    private const string Scope = "https://dev.azure.com/org";
    private const string Project = "project";
    private const string Repo = "repo-1";
    private const int Pr = 42;
    private static readonly Guid ClientId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public async Task IsBlockedAsync_WhenNotBlocked_ReturnsFalse()
    {
        var store = new InMemoryBlockedPullRequestStore();

        Assert.False(await store.IsBlockedAsync(ClientId, Scope, Project, Repo, Pr));
    }

    [Fact]
    public async Task BlockAsync_ThenIsBlocked_ReturnsTrue()
    {
        var store = new InMemoryBlockedPullRequestStore();

        var created = await store.BlockAsync(ClientId, Scope, Project, Repo, Pr, Guid.NewGuid(), "too large");

        Assert.True(created);
        Assert.True(await store.IsBlockedAsync(ClientId, Scope, Project, Repo, Pr));
    }

    [Fact]
    public async Task BlockAsync_WhenAlreadyBlocked_IsIdempotent()
    {
        var store = new InMemoryBlockedPullRequestStore();
        await store.BlockAsync(ClientId, Scope, Project, Repo, Pr, Guid.NewGuid(), null);

        var second = await store.BlockAsync(ClientId, Scope, Project, Repo, Pr, Guid.NewGuid(), null);

        Assert.False(second);
        var blocks = await store.ListForClientAsync(ClientId);
        Assert.Single(blocks);
    }

    [Fact]
    public async Task UnblockAsync_RemovesBlock_AndIsIdempotent()
    {
        var store = new InMemoryBlockedPullRequestStore();
        await store.BlockAsync(ClientId, Scope, Project, Repo, Pr, Guid.NewGuid(), null);

        Assert.True(await store.UnblockAsync(ClientId, Scope, Project, Repo, Pr));
        Assert.False(await store.IsBlockedAsync(ClientId, Scope, Project, Repo, Pr));
        Assert.False(await store.UnblockAsync(ClientId, Scope, Project, Repo, Pr));
    }

    [Fact]
    public async Task ListForClientAsync_ReturnsOnlyThatClientsBlocks()
    {
        var store = new InMemoryBlockedPullRequestStore();
        var otherClient = Guid.NewGuid();
        await store.BlockAsync(ClientId, Scope, Project, Repo, Pr, Guid.NewGuid(), null);
        await store.BlockAsync(ClientId, Scope, Project, Repo, 43, Guid.NewGuid(), null);
        await store.BlockAsync(otherClient, Scope, Project, Repo, Pr, Guid.NewGuid(), null);

        var blocks = await store.ListForClientAsync(ClientId);

        Assert.Equal(2, blocks.Count);
        Assert.All(blocks, b => Assert.Equal(ClientId, b.ClientId));
    }
}
