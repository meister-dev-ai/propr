// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Tests.AI;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Covers the audit trail for provider configuration: that a change is recorded against the owning tenant with
///     enough to reconstruct it, and that the credential is not part of "enough".
/// </summary>
public sealed class AiProviderConfigAuditTests
{
    private const string Secret = "sk-never-in-an-audit-trail";

    [Fact]
    public async Task CreatingAProfileRecordsWhoWhenAndWhat()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var db = CreateContext(databaseName);
        var clientId = SeedClient(db);
        var actorId = Guid.NewGuid();
        var repo = new AiConnectionRepository(db, CreateCodec(), null, null, Writer(databaseName, actorId));

        await repo.AddAsync(clientId, WriteRequest(Secret));

        var entry = await db.TenantAuditEntries.SingleAsync();
        Assert.Equal("ai.connection.created", entry.EventType);
        Assert.Equal(TenantCatalog.SystemTenantId, entry.TenantId);
        Assert.Equal(actorId, entry.ActorUserId);
        Assert.Contains("Primary", entry.Summary, StringComparison.Ordinal);
        Assert.Contains("OpenAiCompatible", entry.Summary, StringComparison.Ordinal);
    }

    // The whole point of recording a credential change is to know one happened. Recording the credential itself
    // would put a secret in the one store that is read by the most people.
    [Fact]
    public async Task ACredentialChangeIsRecordedAsHavingHappenedAndNeverAsItsValue()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var db = CreateContext(databaseName);
        var clientId = SeedClient(db);
        var repo = new AiConnectionRepository(db, CreateCodec(), null, null, Writer(databaseName));

        await repo.AddAsync(clientId, WriteRequest(Secret));

        var entry = await db.TenantAuditEntries.SingleAsync();
        Assert.Contains("credential=replaced", entry.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, entry.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProfileSavedWithoutTouchingTheCredentialSaysSo()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var db = CreateContext(databaseName);
        var clientId = SeedClient(db);
        var repo = new AiConnectionRepository(db, CreateCodec(), null, null, Writer(databaseName));

        await repo.AddAsync(clientId, WriteRequest(null));

        var entry = await db.TenantAuditEntries.SingleAsync();
        Assert.Contains("credential=unchanged", entry.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingAProfileIsRecordedToo()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var db = CreateContext(databaseName);
        var clientId = SeedClient(db);
        var repo = new AiConnectionRepository(db, CreateCodec(), null, null, Writer(databaseName));
        var created = await repo.AddAsync(clientId, WriteRequest(Secret));

        await repo.DeleteAsync(created.Id);

        var actions = await db.TenantAuditEntries.Select(entry => entry.EventType).ToListAsync();
        Assert.Contains("ai.connection.created", actions);
        Assert.Contains("ai.connection.deleted", actions);
    }

    // An entry that cannot be attributed to a tenant is dropped rather than written against a guess: an audit
    // trail that is wrong is worse than one with a gap. The configuration change itself still stands.
    [Fact]
    public async Task AProfileWhoseClientHasNoTenantIsNotAudited()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var db = CreateContext(databaseName);
        var repo = new AiConnectionRepository(db, CreateCodec(), null, null, Writer(databaseName));

        var created = await repo.AddAsync(Guid.NewGuid(), WriteRequest(Secret));

        Assert.NotNull(created);
        Assert.Empty(db.TenantAuditEntries);
    }

    private static AiConnectionWriteRequestDto WriteRequest(string? secret)
    {
        var chatModel = AiConnectionTestFactory.CreateChatModel("deepseek-reasoner");
        return new AiConnectionWriteRequestDto(
            "Primary DeepSeek",
            AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            AiAuthMode.ApiKey,
            AiDiscoveryMode.ManualOnly,
            [chatModel],
            [AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, chatModel)],
            Secret: secret);
    }

    // The writer opens its own short-lived context, exactly as it does in production, so the factory hands out a
    // fresh one over the same in-memory database rather than the caller's.
    private static IAiProviderConfigAuditWriter Writer(string databaseName, Guid? actorUserId = null)
    {
        var factory = Substitute.For<IDbContextFactory<MeisterProPRDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(CreateContext(databaseName)));

        var accessor = Substitute.For<IHttpContextAccessor>();
        if (actorUserId is { } actor)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Items["UserId"] = actor.ToString();
            accessor.HttpContext.Returns(httpContext);
        }

        return new TenantAuditAiProviderConfigWriter(factory, accessor);
    }

    private static Guid SeedClient(MeisterProPRDbContext db)
    {
        var clientId = Guid.NewGuid();
        db.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Audited client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        db.SaveChanges();
        return clientId;
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"MeisterDev.ProPR.AuditTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        return new SecretProtectionCodec(services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
    }

    private static MeisterProPRDbContext CreateContext(string databaseName)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MeisterProPRDbContext(options);
    }
}
