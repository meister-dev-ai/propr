// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Application.Tests.Features.Clients;

public sealed class ClientsModuleTests
{
    [Fact]
    public async Task PatchAsync_WithEmptyCustomSystemMessage_ClearsStoredMessage()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        db.Clients.Add(new ClientRecord
        {
            Id = clientId,
            DisplayName = "Feature Client",
            IsActive = true,
            CustomSystemMessage = "keep me",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = new ClientAdminService(db);

        var result = await sut.PatchAsync(clientId, null, null, null, string.Empty);

        Assert.NotNull(result);
        Assert.Null(result!.CustomSystemMessage);
        Assert.Null(await db.Clients.Where(record => record.Id == clientId).Select(record => record.CustomSystemMessage).SingleAsync());
    }

    [Fact]
    public async Task SetReviewerIdentityAsync_WhenClientExists_PersistsReviewerId()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        db.Clients.Add(new ClientRecord
        {
            Id = clientId,
            DisplayName = "Reviewer Client",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = new ClientAdminService(db);

        var updated = await sut.SetReviewerIdentityAsync(clientId, reviewerId);

        Assert.True(updated);
        Assert.Equal(reviewerId, await db.Clients.Where(record => record.Id == clientId).Select(record => record.ReviewerId).SingleAsync());
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"ClientsModuleTests_{Guid.NewGuid()}")
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
