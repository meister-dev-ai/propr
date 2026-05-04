// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>Unit tests for <see cref="AiConnectionRepository" /> using EF Core in-memory database.</summary>
public sealed class AiConnectionRepositoryTests
{
    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterProPR.AiConnectionRepositoryTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }

    private static AiConnectionRepository CreateRepository(
        MeisterProPRDbContext db,
        IDbContextFactory<MeisterProPRDbContext>? contextFactory = null,
        ISecretProtectionCodec? codec = null)
    {
        return new AiConnectionRepository(db, codec ?? CreateCodec(), contextFactory);
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MeisterProPRDbContext(options);
    }

    private static AiConnectionProfileRecord MakeProfile(
        Guid clientId,
        bool isActive = false,
        bool verified = true,
        string displayName = "Test Connection",
        params AiPurpose[] purposes)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid();
        var chatModelId = Guid.NewGuid();
        var embeddingModelId = Guid.NewGuid();
        var resolvedPurposes = purposes.Length == 0
            ?
            [
                AiPurpose.ReviewDefault,
                AiPurpose.ReviewLowEffort,
                AiPurpose.ReviewMediumEffort,
                AiPurpose.ReviewHighEffort,
                AiPurpose.MemoryReconsideration,
                AiPurpose.EmbeddingDefault,
            ]
            : purposes;

        var profile = new AiConnectionProfileRecord
        {
            Id = profileId,
            ClientId = clientId,
            DisplayName = displayName,
            ProviderKind = AiProviderKind.AzureOpenAi.ToString(),
            BaseUrl = "https://my-openai.openai.azure.com/",
            AuthMode = AiAuthMode.AzureIdentity.ToString(),
            DiscoveryMode = AiDiscoveryMode.ManualOnly.ToString(),
            DefaultHeaders = [],
            DefaultQueryParams = [],
            IsActive = isActive,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            ConfiguredModels =
            [
                new AiConfiguredModelRecord
                {
                    Id = chatModelId,
                    ConnectionProfileId = profileId,
                    RemoteModelId = "gpt-4o",
                    DisplayName = "gpt-4o",
                    OperationKinds = [AiOperationKind.Chat.ToString()],
                    SupportedProtocolModes =
                    [
                        AiProtocolMode.Auto.ToString(),
                        AiProtocolMode.Responses.ToString(),
                        AiProtocolMode.ChatCompletions.ToString(),
                    ],
                    SupportsStructuredOutput = true,
                    SupportsToolUse = true,
                    Source = AiConfiguredModelSource.Manual.ToString(),
                },
                new AiConfiguredModelRecord
                {
                    Id = embeddingModelId,
                    ConnectionProfileId = profileId,
                    RemoteModelId = "text-embedding-3-large",
                    DisplayName = "text-embedding-3-large",
                    OperationKinds = [AiOperationKind.Embedding.ToString()],
                    SupportedProtocolModes =
                    [
                        AiProtocolMode.Auto.ToString(),
                        AiProtocolMode.Embeddings.ToString(),
                    ],
                    TokenizerName = "cl100k_base",
                    MaxInputTokens = 8192,
                    EmbeddingDimensions = 3072,
                    SupportsStructuredOutput = false,
                    SupportsToolUse = false,
                    Source = AiConfiguredModelSource.Manual.ToString(),
                },
            ],
            VerificationSnapshot = new AiVerificationSnapshotRecord
            {
                ConnectionProfileId = profileId,
                Status = (verified ? AiVerificationStatus.Verified : AiVerificationStatus.NeverVerified).ToString(),
                Summary = verified ? "Verified" : "Never verified",
                CheckedAt = createdAt,
                Warnings = [],
            },
        };

        profile.PurposeBindings = resolvedPurposes.Select(purpose => new AiPurposeBindingRecord
        {
            Id = Guid.NewGuid(),
            ConnectionProfileId = profileId,
            ConfiguredModelId = purpose == AiPurpose.EmbeddingDefault ? embeddingModelId : chatModelId,
            Purpose = purpose.ToString(),
            ProtocolMode = (purpose == AiPurpose.EmbeddingDefault ? AiProtocolMode.Embeddings : AiProtocolMode.Auto).ToString(),
            IsEnabled = true,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        })
            .ToList();

        return profile;
    }

    private static AiConnectionWriteRequestDto CreateWriteRequest(
        string displayName = "Updated Connection",
        string baseUrl = "https://updated.openai.azure.com/",
        AiAuthMode authMode = AiAuthMode.AzureIdentity,
        string? secret = null,
        string chatModelId = "gpt-4o",
        string embeddingModelId = "text-embedding-3-large")
    {
        var chatModel = new AiConfiguredModelDto(
            Guid.Empty,
            chatModelId,
            chatModelId,
            [AiOperationKind.Chat],
            [AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions],
            null,
            null,
            null,
            true,
            true);

        var embeddingModel = new AiConfiguredModelDto(
            Guid.Empty,
            embeddingModelId,
            embeddingModelId,
            [AiOperationKind.Embedding],
            [AiProtocolMode.Auto, AiProtocolMode.Embeddings],
            "cl100k_base",
            8192,
            3072);

        return new AiConnectionWriteRequestDto(
            displayName,
            AiProviderKind.AzureOpenAi,
            baseUrl,
            authMode,
            AiDiscoveryMode.ManualOnly,
            [chatModel, embeddingModel],
            [
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewDefault, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewLowEffort, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewMediumEffort, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewHighEffort, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.MemoryReconsideration, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.EmbeddingDefault, null, embeddingModelId, AiProtocolMode.Embeddings),
            ],
            null,
            null,
            secret);
    }

    [Fact]
    public async Task GetActiveForClientAsync_NoActiveConnection_ReturnsNull()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        db.AiConnectionProfiles.Add(MakeProfile(clientId));
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.GetActiveForClientAsync(clientId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveForClientAsync_OneActiveConnection_ReturnsIt()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var activeProfile = MakeProfile(clientId, true, displayName: "Active Profile");
        db.AiConnectionProfiles.Add(activeProfile);
        db.AiConnectionProfiles.Add(MakeProfile(clientId, displayName: "Draft Profile"));
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.GetActiveForClientAsync(clientId);

        Assert.NotNull(result);
        Assert.Equal(activeProfile.Id, result.Id);
        Assert.True(result.IsActive);
        Assert.Equal("gpt-4o", result.GetBoundModelId(AiPurpose.ReviewDefault));
    }

    [Fact]
    public async Task GetActiveForClientAsync_WithDbContextFactory_ReturnsIt()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new MeisterProPRDbContext(options);
        var factory = new PooledDbContextFactory<MeisterProPRDbContext>(options);
        var clientId = Guid.NewGuid();
        var activeProfile = MakeProfile(clientId, true);
        db.AiConnectionProfiles.Add(activeProfile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db, factory);
        var result = await repo.GetActiveForClientAsync(clientId);

        Assert.NotNull(result);
        Assert.Equal(activeProfile.Id, result.Id);
        Assert.Equal("gpt-4o", result.GetBoundModelId(AiPurpose.ReviewDefault));
    }

    [Fact]
    public async Task ActivateAsync_VerifiedProfileWithRequiredBindings_ActivatesAndDeactivatesPrevious()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();

        var profileA = MakeProfile(clientId, true, displayName: "Primary");
        var profileB = MakeProfile(clientId, displayName: "Secondary");
        db.AiConnectionProfiles.AddRange(profileA, profileB);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.ActivateAsync(profileB.Id);

        Assert.True(result);

        var refreshedA = await db.AiConnectionProfiles.FindAsync(profileA.Id);
        var refreshedB = await db.AiConnectionProfiles.FindAsync(profileB.Id);

        Assert.NotNull(refreshedA);
        Assert.False(refreshedA.IsActive);

        Assert.NotNull(refreshedB);
        Assert.True(refreshedB.IsActive);
    }

    [Fact]
    public async Task ActivateAsync_VerifiedProfileWithMinimalRequiredBindings_Activates()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();

        var profile = MakeProfile(
            clientId,
            purposes:
            [
                AiPurpose.ReviewDefault,
                AiPurpose.MemoryReconsideration,
                AiPurpose.EmbeddingDefault,
            ]);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.ActivateAsync(profile.Id);

        Assert.True(result);

        var refreshed = await db.AiConnectionProfiles.FindAsync(profile.Id);
        Assert.NotNull(refreshed);
        Assert.True(refreshed.IsActive);
    }

    [Fact]
    public async Task ActivateAsync_UnverifiedProfile_ReturnsFalse()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId, verified: false);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.ActivateAsync(profile.Id);

        Assert.False(result);
    }

    [Fact]
    public async Task ActivateAsync_ConnectionNotFound_ReturnsFalse()
    {
        await using var db = CreateContext();
        var repo = CreateRepository(db);

        var result = await repo.ActivateAsync(Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task DeactivateAsync_ActiveConnection_SetsInactive()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId, true);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.DeactivateAsync(profile.Id);

        Assert.True(result);

        var refreshed = await db.AiConnectionProfiles.FindAsync(profile.Id);
        Assert.NotNull(refreshed);
        Assert.False(refreshed.IsActive);
    }

    [Fact]
    public async Task DeleteAsync_ExistingConnection_Removes()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.DeleteAsync(profile.Id);

        Assert.True(result);
        Assert.Equal(0, await db.AiConnectionProfiles.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_ConnectionNotFound_ReturnsFalse()
    {
        await using var db = CreateContext();
        var repo = CreateRepository(db);

        var result = await repo.DeleteAsync(Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task ActivateAsync_DoesNotOverwriteJobSnapshot_SC003()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();

        var profileA = MakeProfile(clientId, true, displayName: "Primary");
        var profileB = MakeProfile(clientId, displayName: "Secondary");
        db.AiConnectionProfiles.AddRange(profileA, profileB);

        var job = new ReviewJob(
            Guid.NewGuid(),
            clientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            1);
        job.SetAiConfig(profileA.Id, "gpt-4o");
        db.ReviewJobs.Add(job);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        await repo.ActivateAsync(profileB.Id);

        var jobAfter = await db.ReviewJobs.FindAsync(job.Id);
        Assert.NotNull(jobAfter);
        Assert.Equal(profileA.Id, jobAfter.AiConnectionId);
        Assert.Equal("gpt-4o", jobAfter.AiModel);
    }

    [Fact]
    public async Task GetForTierAsync_TierConnectionExists_ReturnsIt()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var activeProfile = MakeProfile(clientId, true, true, "Test Connection", AiPurpose.ReviewHighEffort);
        db.AiConnectionProfiles.Add(activeProfile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.GetForTierAsync(clientId, AiConnectionModelCategory.HighEffort);

        Assert.NotNull(result);
        Assert.Equal(activeProfile.Id, result.Id);
    }

    [Fact]
    public async Task GetForTierAsync_NoTierOrReviewDefaultBinding_ReturnsNull()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var activeProfile = MakeProfile(
            clientId,
            true,
            true,
            "Test Connection",
            AiPurpose.MemoryReconsideration,
            AiPurpose.EmbeddingDefault);
        db.AiConnectionProfiles.Add(activeProfile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.GetForTierAsync(clientId, AiConnectionModelCategory.LowEffort);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetForTierAsync_MissingEffortBinding_FallsBackToReviewDefault()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var activeProfile = MakeProfile(
            clientId,
            true,
            true,
            "Test Connection",
            AiPurpose.ReviewDefault,
            AiPurpose.MemoryReconsideration,
            AiPurpose.EmbeddingDefault);
        db.AiConnectionProfiles.Add(activeProfile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.GetForTierAsync(clientId, AiConnectionModelCategory.LowEffort);

        Assert.NotNull(result);
        Assert.Equal(activeProfile.Id, result.Id);
        Assert.Equal("gpt-4o", result.GetBoundModelId(AiPurpose.ReviewDefault));
    }

    [Fact]
    public async Task UpdateAsync_ConnectivityChange_ResetsVerificationAndBlocksActivationUntilReverified()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);

        var updated = await repo.UpdateAsync(
            profile.Id,
            CreateWriteRequest(baseUrl: "https://updated.openai.azure.com/"));

        Assert.True(updated);

        var reloaded = await repo.GetByIdAsync(profile.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(AiVerificationStatus.NeverVerified, reloaded.Verification.Status);

        var activated = await repo.ActivateAsync(profile.Id);
        Assert.False(activated);
    }

    [Fact]
    public async Task UpdateAsync_AuthModelAndBindingChanges_ResetVerification()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);

        var updated = await repo.UpdateAsync(
            profile.Id,
            CreateWriteRequest(
                authMode: AiAuthMode.ApiKey,
                secret: "updated-secret",
                chatModelId: "gpt-4.1"));

        Assert.True(updated);

        var reloaded = await repo.GetByIdAsync(profile.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(AiVerificationStatus.NeverVerified, reloaded.Verification.Status);
        Assert.Equal("gpt-4.1", reloaded.GetBoundModelId(AiPurpose.ReviewDefault));
    }

    [Fact]
    public async Task AddAsync_WithPricingMetadata_PersistsCapabilityRates()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var repo = CreateRepository(db);

        var created = await repo.AddAsync(
            clientId,
            new AiConnectionWriteRequestDto(
                "Embedding Connection",
                AiProviderKind.AzureOpenAi,
                "https://my-openai.openai.azure.com/",
                AiAuthMode.ApiKey,
                AiDiscoveryMode.ManualOnly,
                [
                    new AiConfiguredModelDto(
                        Guid.Empty,
                        "text-embedding-3-small",
                        "text-embedding-3-small",
                        [AiOperationKind.Embedding],
                        [AiProtocolMode.Auto, AiProtocolMode.Embeddings],
                        "cl100k_base",
                        8192,
                        1536,
                        false,
                        false,
                        AiConfiguredModelSource.Manual,
                        null,
                        0.2m,
                        0.4m),
                ],
                [
                    new AiPurposeBindingDto(Guid.Empty, AiPurpose.EmbeddingDefault, null, "text-embedding-3-small", AiProtocolMode.Embeddings),
                ],
                null,
                null,
                "secret"));

        var reloaded = await repo.GetByIdAsync(created.Id);

        Assert.NotNull(reloaded);
        var configuredModel = Assert.Single(reloaded.ConfiguredModels);
        Assert.Equal(0.2m, configuredModel.InputCostPer1MUsd);
        Assert.Equal(0.4m, configuredModel.OutputCostPer1MUsd);
    }

    [Fact]
    public async Task AddAsync_WithApiKey_PersistsProtectedValueAndReturnsPlaintext()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var codec = CreateCodec();
        var repo = CreateRepository(db, codec: codec);

        var created = await repo.AddAsync(
            clientId,
            new AiConnectionWriteRequestDto(
                "Protected Connection",
                AiProviderKind.AzureOpenAi,
                "https://my-openai.openai.azure.com/",
                AiAuthMode.ApiKey,
                AiDiscoveryMode.ManualOnly,
                [
                    new AiConfiguredModelDto(
                        Guid.Empty,
                        "gpt-4o",
                        "gpt-4o",
                        [AiOperationKind.Chat],
                        [AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions],
                        null,
                        null,
                        null,
                        true,
                        true),
                ],
                [
                    new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewDefault, null, "gpt-4o"),
                ],
                null,
                null,
                "secret-api-key"));

        var record = await db.AiConnectionProfiles.FirstAsync(profile => profile.Id == created.Id);

        Assert.Equal("secret-api-key", created.Secret);
        Assert.NotEqual("secret-api-key", record.ProtectedSecret);
        Assert.False(string.IsNullOrWhiteSpace(record.ProtectedSecret));
    }
}
