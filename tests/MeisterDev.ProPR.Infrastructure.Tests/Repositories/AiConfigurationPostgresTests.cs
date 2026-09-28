// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Clients.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

[Collection("PostgresIntegration")]
public sealed class AiConfigurationPostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private Guid _firstId;
    private Guid _secondId;

    private MeisterProPRDbContext Context(params IInterceptor[] interceptors) => new(
        new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).AddInterceptors(interceptors).Options);

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        await using var db = this.Context();
        db.Tenants.Add(
            new TenantRecord
            {
                Id = this._tenantId, Slug = "ai-test-" + this._tenantId.ToString("N"),
                DisplayName = "AI lifecycle test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
        db.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId, TenantId = this._tenantId, DisplayName = "AI lifecycle test",
                CreatedAt = DateTimeOffset.UtcNow
            });
        var first = AiConnectionRepositoryTests.MakeProfile(this._clientId, true, true, "First");
        var second = AiConnectionRepositoryTests.MakeProfile(this._clientId, false, true, "Second");
        first.AuthMode = "ApiKey";
        second.AuthMode = "ApiKey";
        this._firstId = first.Id;
        this._secondId = second.Id;
        db.AiConnectionProfiles.AddRange(first, second);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (!fixture.IsAvailable) return;
        await using var db = this.Context();
        await db.Clients.Where(c => c.Id == this._clientId).ExecuteDeleteAsync();
        await db.Tenants.Where(t => t.Id == this._tenantId).ExecuteDeleteAsync();
    }

    private static AiWorkspacePurposeSelection Selection(AiConnectionDto profile)
    {
        var chat = profile.ConfiguredModels.Single(m => m.SupportsChat);
        var embedding = profile.ConfiguredModels.Single(m => m.SupportsEmbedding);
        return new(new(profile.Id, chat.Id), new(profile.Id, chat.Id), new(profile.Id, embedding.Id));
    }

    [Fact]
    public async Task Selection_SaveFailureRollsBackAllBindingsAndActivation()
    {
        await using var failing = this.Context(new FailAfterSave());
        var repository = AiConnectionRepositoryTests.CreateRepository(failing);
        var before = (await repository.GetByIdAsync(this._firstId))!;
        var selected = (await repository.GetByIdAsync(this._secondId))!;
        await Assert.ThrowsAsync<InjectedSaveException>(() => repository.SelectPurposesAsync(this._clientId, Selection(selected)));
        await using var read = this.Context();
        var saved = AiConnectionRepositoryTests.CreateRepository(read);
        var first = (await saved.GetByIdAsync(this._firstId))!;
        var second = (await saved.GetByIdAsync(this._secondId))!;
        Assert.Equal(before.UpdatedAt, first.UpdatedAt);
        Assert.Equal(before.PurposeBindings.Select(b => (b.Id, b.ConfiguredModelId)), first.PurposeBindings.Select(b => (b.Id, b.ConfiguredModelId)));
        Assert.False(second.IsActive);
    }

    [Fact]
    public async Task VerifiedUpdate_AuthMutationWithoutTimestampRejectsCandidate()
    {
        await using var db = this.Context();
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var original = (await repository.GetByIdAsync(this._firstId))!;
        var request = new AiConnectionWriteRequestDto(
            "Stale candidate", original.ProviderKind, original.BaseUrl,
            original.AuthMode, original.DiscoveryMode, original.ConfiguredModels, original.PurposeBindings);
        var result = await repository.VerifyUpdateAsync(
            this._clientId, original, request, async (_, _) =>
            {
                await using var writer = this.Context();
                await writer.AiConnectionProfiles.Where(p => p.Id == original.Id)
                    .ExecuteUpdateAsync(update => update.SetProperty(p => p.AuthMode, "AzureIdentity"));
                return original.Verification;
            });
        Assert.False(result.Applied);
        Assert.True(result.Conflict);
        Assert.Equal(original.DisplayName, (await repository.GetByIdAsync(original.Id))!.DisplayName);
    }

    [Fact]
    public async Task VerifiedUpdateAndRepeatedSelection_PreserveConfiguredModelIdentifiers()
    {
        await using var db = this.Context();
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var original = (await repository.GetByIdAsync(this._firstId))!;
        var request = new AiConnectionWriteRequestDto(
            "Verified replacement", original.ProviderKind, original.BaseUrl,
            original.AuthMode, original.DiscoveryMode, original.ConfiguredModels, original.PurposeBindings);
        var result = await repository.VerifyUpdateAsync(
            this._clientId, original, request,
            (_, _) => Task.FromResult(original.Verification));
        Assert.True(result.Applied);
        Assert.Equal(original.ConfiguredModels.Select(m => m.Id), result.Connection!.ConfiguredModels.Select(m => m.Id));
        var selected = Selection(result.Connection);
        Assert.True((await repository.SelectPurposesAsync(this._clientId, selected)).Applied);
        Assert.True((await repository.SelectPurposesAsync(this._clientId, selected)).Applied);
        await using var read = this.Context();
        var saved = (await AiConnectionRepositoryTests.CreateRepository(read).GetByIdAsync(original.Id))!;
        Assert.Equal(original.ConfiguredModels.Select(m => m.Id), saved.ConfiguredModels.Select(m => m.Id));
        Assert.True(saved.IsActive);
    }

    [Fact]
    public async Task ConcurrentSelections_OneCompleteSelectionWins()
    {
        var barrier = new SaveBarrier();
        await using var firstDb = this.Context(barrier);
        await using var secondDb = this.Context(barrier);
        var firstRepository = AiConnectionRepositoryTests.CreateRepository(firstDb);
        var secondRepository = AiConnectionRepositoryTests.CreateRepository(secondDb);
        var firstProfile = (await firstRepository.GetByIdAsync(this._firstId))!;
        var secondProfile = (await secondRepository.GetByIdAsync(this._secondId))!;
        var results = await Task.WhenAll(
            firstRepository.SelectPurposesAsync(this._clientId, Selection(firstProfile)),
            secondRepository.SelectPurposesAsync(this._clientId, Selection(secondProfile))).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, result => result.Applied);
        Assert.Single(results, result => result.Conflict);
        var expectedId = results[0].Applied ? firstProfile.Id : secondProfile.Id;
        await using var read = this.Context();
        var saved = AiConnectionRepositoryTests.CreateRepository(read);
        Assert.True((await saved.GetByIdAsync(expectedId))!.IsActive);
        foreach (var purpose in new[]
                 {
                     AiPurpose.ReviewDefault, AiPurpose.ReviewTriage, AiPurpose.ReviewVerification,
                     AiPurpose.ReviewLowEffort, AiPurpose.ReviewMediumEffort, AiPurpose.ReviewHighEffort,
                     AiPurpose.MemoryReconsideration, AiPurpose.EmbeddingDefault
                 })
            Assert.Equal(expectedId, (await saved.GetActiveBindingForPurposeAsync(this._clientId, purpose))!.Connection.Id);
    }

    private sealed class InjectedSaveException : Exception
    {
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new InjectedSaveException();
    }

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref this._arrived) == 2) this._ready.TrySetResult();
            await this._ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }
}
