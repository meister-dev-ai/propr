// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>Unit tests for <see cref="AiConnectionRepository" /> using EF Core in-memory database.</summary>
public sealed class AiConnectionRepositoryTests
{
    private const string AzureKey = "meisterdev/azureOpenAi";

    private const string AzureApiKey = AzureKey + ":ApiKey";

    private const string AzureIdentityAuth = AzureKey + ":AzureIdentity";

    private const string AzureResponses = AzureKey + ":Responses";

    private const string AzureChatCompletions = AzureKey + ":ChatCompletions";

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.AiConnectionRepositoryTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }

    // A tenant that has stated no policy is unrestricted, and every repository built for a test that is not about
    // the policy states that assumption instead of leaving it to a default.
    private static ITenantProviderPolicyProvider UnrestrictedPolicies()
    {
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TenantProviderPolicy.Unrestricted);
        policies.GetForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TenantProviderPolicy.Unrestricted);
        return policies;
    }

    // A tenant that permits only the families named here, and nothing else.
    private static ITenantProviderPolicyProvider PoliciesAllowing(params string[] allowedKinds)
    {
        return PoliciesFor(new TenantProviderPolicy(allowedKinds));
    }

    private static ITenantProviderPolicyProvider PoliciesFor(TenantProviderPolicy policy)
    {
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(policy);
        policies.GetForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(policy);
        return policies;
    }

    private static AiConnectionRepository CreateRepository(
        MeisterProPRDbContext db,
        IDbContextFactory<MeisterProPRDbContext>? contextFactory = null,
        ISecretProtectionCodec? codec = null,
        ITenantProviderPolicyProvider? providerPolicies = null,
        IAiProviderDriverRegistry? providerDrivers = null)
    {
        return new AiConnectionRepository(
            db,
            codec ?? CreateCodec(),
            providerPolicies ?? UnrestrictedPolicies(),
            providerDrivers ?? StoredFamily(),
            EgressUrlPolicy.Locked,
            contextFactory);
    }

    // The family the fixture profiles are stored against: it declares the key those rows would carry after its
    // migration, and supersedes the spelling they carry before it. A test about a family nothing claims passes
    // DeclaringProviderFamilies.None() instead.
    private static IAiProviderDriverRegistry StoredFamily()
    {
        return DeclaringProviderFamilies.Superseding("meisterdev/azureOpenAi", "AzureOpenAi");
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // ActivateAsync wraps its writes in a transaction; the InMemory provider ignores
            // transactions and otherwise throws TransactionIgnoredWarning.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
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
                AiPurpose.ProRVPrefilter,
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
            ProviderKind = "AzureOpenAi",
            BaseUrl = "https://my-openai.openai.azure.com/",
            AuthMode = "AzureIdentity",
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
                        ProviderDeclaredProtocolModes.Auto,
                        "Responses",
                        "ChatCompletions",
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
                        ProviderDeclaredProtocolModes.Auto.ToString(),
                        ProviderDeclaredProtocolModes.Embeddings.ToString(),
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
                ProtocolMode =
                    (purpose == AiPurpose.EmbeddingDefault ? ProviderDeclaredProtocolModes.Embeddings : ProviderDeclaredProtocolModes.Auto).ToString(),
                IsEnabled = true,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            })
            .ToList();

        return profile;
    }

    // Deleting a connection a logical model maps to (by connection id) is blocked, naming the referrer.
    [Fact]
    public async Task DeleteAsync_BlockedWhenLogicalModelMapsToConnection()
    {
        await using var db = CreateContext();
        var (connectionId, modelId) = SeedConnectionWithModel(db);
        db.LogicalModels.Add(NewLogicalModel("deep", connectionId, modelId));
        await db.SaveChangesAsync();
        var repo = CreateRepository(db);

        var ex = await Assert.ThrowsAsync<LogicalModelReferenceInUseException>(() => repo.DeleteAsync(connectionId));
        Assert.Contains("deep", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(await db.AiConnectionProfiles.FindAsync(connectionId));
    }

    // The block also fires when the logical model references one of the connection's configured models
    // (even via a different connection id on the mapping).
    [Fact]
    public async Task DeleteAsync_BlockedWhenLogicalModelMapsToConfiguredModel()
    {
        await using var db = CreateContext();
        var (connectionId, modelId) = SeedConnectionWithModel(db);
        db.LogicalModels.Add(NewLogicalModel("sec", Guid.NewGuid(), modelId));
        await db.SaveChangesAsync();
        var repo = CreateRepository(db);

        var ex = await Assert.ThrowsAsync<LogicalModelReferenceInUseException>(() => repo.DeleteAsync(connectionId));
        Assert.Contains("sec", ex.Message, StringComparison.Ordinal);
    }

    // Once the referencing logical model is removed, the delete succeeds.
    [Fact]
    public async Task DeleteAsync_SucceedsAfterLogicalModelRemoved()
    {
        await using var db = CreateContext();
        var (connectionId, modelId) = SeedConnectionWithModel(db);
        var logicalModel = NewLogicalModel("deep", connectionId, modelId);
        db.LogicalModels.Add(logicalModel);
        await db.SaveChangesAsync();
        var repo = CreateRepository(db);

        await Assert.ThrowsAsync<LogicalModelReferenceInUseException>(() => repo.DeleteAsync(connectionId));

        db.LogicalModels.Remove(logicalModel);
        await db.SaveChangesAsync();

        Assert.True(await repo.DeleteAsync(connectionId));
    }

    // Updating a connection to drop a configured model a logical model maps to is blocked.
    [Fact]
    public async Task UpdateAsync_BlockedWhenDroppingReferencedModel()
    {
        await using var db = CreateContext();
        var repo = CreateRepository(db);
        var clientId = Guid.NewGuid();
        var created = await repo.AddAsync(clientId, CreateWriteRequest());
        var chatModel = created.ConfiguredModels.First(model => model.SupportsChat);
        db.LogicalModels.Add(NewLogicalModel("deep", created.Id, chatModel.Id));
        await db.SaveChangesAsync();

        // A request that keeps only the embedding model — dropping the referenced chat model.
        var dropChat = new AiConnectionWriteRequestDto(
            "Updated",
            "meisterdev/azureOpenAi",
            "https://updated.openai.azure.com/",
            AzureApiKey,
            AiDiscoveryMode.ManualOnly,
            [
                new AiConfiguredModelDto(
                    Guid.Empty,
                    "text-embedding-3-large",
                    "text-embedding-3-large",
                    [AiOperationKind.Embedding],
                    [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings],
                    "cl100k_base",
                    8192,
                    3072),
            ],
            [],
            null,
            null,
            "secret");

        var ex = await Assert.ThrowsAsync<LogicalModelReferenceInUseException>(() => repo.UpdateAsync(created.Id, dropChat));
        Assert.Contains("deep", ex.Message, StringComparison.Ordinal);

        // The block precedes any mutation, so the connection still has the chat model (no partial write).
        var reloaded = await repo.GetByIdAsync(created.Id);
        Assert.Contains(reloaded!.ConfiguredModels, model => model.SupportsChat);
    }

    private static (Guid ConnectionId, Guid ModelId) SeedConnectionWithModel(MeisterProPRDbContext db, Guid? clientId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var connectionId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        db.AiConnectionProfiles.Add(
            new AiConnectionProfileRecord
            {
                Id = connectionId,
                ClientId = clientId ?? Guid.NewGuid(),
                DisplayName = "conn",
                ProviderKind = "AzureOpenAi",
                BaseUrl = "https://x",
                AuthMode = "ApiKey",
                DiscoveryMode = "ManualOnly",
                CreatedAt = now,
                UpdatedAt = now,
                ConfiguredModels =
                [
                    new AiConfiguredModelRecord
                    {
                        Id = modelId,
                        ConnectionProfileId = connectionId,
                        RemoteModelId = "gpt-x",
                        DisplayName = "gpt-x",
                        OperationKinds = ["Chat"],
                        SupportedProtocolModes = ["Auto"],
                        Source = "Manual",
                    },
                ],
            });
        return (connectionId, modelId);
    }

    private static LogicalModelRecord NewLogicalModel(string name, Guid connectionId, Guid modelId)
    {
        var now = DateTimeOffset.UtcNow;
        return new LogicalModelRecord
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Name = name,
            Capability = AiOperationKind.Chat,
            ConnectionId = connectionId,
            ConfiguredModelId = modelId,
            ReasoningEffort = ReviewReasoningEffort.None,
            ProtocolMode = ProviderDeclaredProtocolModes.Auto,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static AiConnectionWriteRequestDto CreateWriteRequest(
        string displayName = "Updated Connection",
        string baseUrl = "https://updated.openai.azure.com/",
        string authMode = AzureIdentityAuth,
        string? secret = null,
        string chatModelId = "gpt-4o",
        string embeddingModelId = "text-embedding-3-large")
    {
        var chatModel = new AiConfiguredModelDto(
            Guid.Empty,
            chatModelId,
            chatModelId,
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto, AzureResponses, AzureChatCompletions],
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
            [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings],
            "cl100k_base",
            8192,
            3072);

        return new AiConnectionWriteRequestDto(
            displayName,
            "meisterdev/azureOpenAi",
            baseUrl,
            authMode,
            AiDiscoveryMode.ManualOnly,
            [chatModel, embeddingModel],
            [
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewDefault, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.ProRVPrefilter, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewLowEffort, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewMediumEffort, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewHighEffort, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.MemoryReconsideration, null, chatModelId),
                new AiPurposeBindingDto(Guid.Empty, AiPurpose.EmbeddingDefault, null, embeddingModelId, ProviderDeclaredProtocolModes.Embeddings),
            ],
            null,
            null,
            secret);
    }

    // The column holds the identity the API reports. A spelling a loaded family claims is reported as that
    // family's key, which everything else compares against; an identity no loaded family claims is
    // reported exactly as it is stored, so a profile whose add-in is absent can still be seen and corrected
    // rather than being dropped or rewritten to another family.
    [Theory]
    [InlineData("meisterdev/azureOpenAi", "meisterdev/azureOpenAi")]
    [InlineData("AzureOpenAi", "meisterdev/azureOpenAi")]
    [InlineData("contoso/llm", "contoso/llm")]
    public async Task AProfileIsReadBackUnderTheIdentityItWasStoredAgainst(string storedIdentity, string reported)
    {
        await using var db = CreateContext();
        var profile = MakeProfile(Guid.NewGuid());
        profile.ProviderKind = storedIdentity;
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var read = await CreateRepository(db).GetByIdAsync(profile.Id);

        Assert.NotNull(read);
        Assert.Equal(reported, read.ProviderKind);
    }

    // A profile saved by a build that had a family this one does not is listed, because an operator cannot
    // install the missing family for a connection they cannot see. The identity is carried on the availability
    // state: the family field is enum-typed and has no member to put it in. Both spellings a row can hold are
    // reported the same way — the member name a row written before its family declared a key still holds, and
    // the declared key a row carries once that family's rows have been rewritten.
    [Theory]
    [InlineData("SomeExternalFamily")]
    [InlineData("meisterdev/someExternalFamily")]
    public async Task AProfileStoredAgainstAFamilyThisBuildCannotNameIsUnavailableNamingTheIdentity(string storedIdentity)
    {
        await using var db = CreateContext();
        var profile = MakeProfile(Guid.NewGuid());
        profile.ProviderKind = storedIdentity;
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var read = await CreateRepository(db).GetByIdAsync(profile.Id);

        Assert.NotNull(read);
        Assert.Equal(AiConnectionAvailabilityState.Unavailable, read.Availability.State);
        Assert.Equal(AiConnectionUnavailableReason.ProviderFamilyAbsent, read.Availability.Reason);
        Assert.Equal(storedIdentity, read.Availability.ProviderIdentity);
    }

    // A family the tenant does not permit is a different fault with a different remedy: the family is installed,
    // and the allow-list has to be amended. Reporting both as one state would send the operator to the wrong one.
    [Fact]
    public async Task AProfileWhoseFamilyTheTenantDoesNotPermitIsUnavailableWithTheAllowListReason()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var read = await CreateRepository(db, providerPolicies: PoliciesAllowing("meisterdev/anthropic"))
            .GetByIdAsync(profile.Id);

        Assert.NotNull(read);
        Assert.Equal(AiConnectionAvailabilityState.Unavailable, read.Availability.State);
        Assert.Equal(AiConnectionUnavailableReason.ProviderFamilyNotPermitted, read.Availability.Reason);
        Assert.Equal("AzureOpenAi", read.Availability.ProviderIdentity);
        Assert.Empty(read.Availability.UnresolvedValues);
    }

    // A tenant can narrow its endpoint allow-list after a profile was saved. The write-time refusal cannot see
    // that, and runtime resolution refuses the profile, so the list has to report it instead of advertising a
    // connection that will not run.
    [Fact]
    public async Task AProfileWhoseEndpointTheTenantNoLongerPermitsIsUnavailableWithTheEndpointReason()
    {
        await using var db = CreateContext();
        var profile = MakeProfile(Guid.NewGuid());
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var narrowed = new TenantProviderPolicy([], ["api.vendor.example"]);

        var read = await CreateRepository(db, providerPolicies: PoliciesFor(narrowed)).GetByIdAsync(profile.Id);

        Assert.NotNull(read);
        Assert.Equal(AiConnectionAvailabilityState.Unavailable, read.Availability.State);
        Assert.Equal(AiConnectionUnavailableReason.EndpointNotPermitted, read.Availability.Reason);
    }

    // A profile whose family this build has and the tenant permits, holding only values this build can read, is
    // usable, and says so rather than leaving the caller to infer it from an absent reason.
    [Fact]
    public async Task AProfileThisBuildCanReadAndTheTenantPermitsIsAvailable()
    {
        await using var db = CreateContext();
        var profile = MakeProfile(Guid.NewGuid());
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var read = await CreateRepository(db, providerPolicies: PoliciesAllowing("meisterdev/azureOpenAi"))
            .GetByIdAsync(profile.Id);

        Assert.NotNull(read);
        Assert.Equal(AiConnectionAvailabilityState.Available, read.Availability.State);
        Assert.Null(read.Availability.Reason);
        Assert.Null(read.Availability.ProviderIdentity);
        Assert.Empty(read.Availability.UnresolvedValues);
    }

    // A stored value the build cannot read leaves the profile usable in no sense, but the remedy is to correct
    // the row rather than to install or permit a family, so it is reported under its own reason.
    [Fact]
    public async Task AProfileHoldingAnUnresolvedStoredValueIsUnavailableUnderItsOwnReason()
    {
        await using var db = CreateContext();
        var profile = MakeProfile(Guid.NewGuid());
        profile.AuthMode = "SomeExternalCredentialShape";
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var read = await CreateRepository(db).GetByIdAsync(profile.Id);

        Assert.NotNull(read);
        Assert.Equal(AiConnectionAvailabilityState.Unavailable, read.Availability.State);
        Assert.Equal(AiConnectionUnavailableReason.StoredValueUnresolved, read.Availability.Reason);
        Assert.Equal(
            new AiUnresolvedValueDto(AiConnectionVocabularyField.AuthMode, "SomeExternalCredentialShape"),
            Assert.Single(read.Availability.UnresolvedValues));
    }

    // The console reaches one profile through a list and through the profile's own address. Two answers for one
    // row would have an operator fix a connection on one screen that the other reports as usable.
    [Fact]
    public async Task EveryReadReportsTheSameAvailabilityForTheSameRow()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId, isActive: true);
        profile.ProviderKind = "SomeExternalFamily";
        profile.DiscoveryMode = "SomeExternalDiscovery";
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();
        var repository = CreateRepository(db);

        var fromList = Assert.Single(await repository.GetByClientAsync(clientId));
        var fromId = await repository.GetByIdAsync(profile.Id);
        var fromActive = await repository.GetActiveForClientAsync(clientId);

        Assert.NotNull(fromId);
        Assert.NotNull(fromActive);
        foreach (var availability in new[] { fromList.Availability, fromId.Availability, fromActive.Availability })
        {
            Assert.Equal(AiConnectionAvailabilityState.Unavailable, availability.State);
            Assert.Equal(AiConnectionUnavailableReason.ProviderFamilyAbsent, availability.Reason);
            Assert.Equal("SomeExternalFamily", availability.ProviderIdentity);

            // The identity names no loaded family, so the credential and wire shapes it carries are nobody's to
            // claim either, and every one of them is reported beside the discovery mode.
            Assert.Equal(
                [
                    new AiUnresolvedValueDto(AiConnectionVocabularyField.ProtocolMode, "Responses"),
                    new AiUnresolvedValueDto(AiConnectionVocabularyField.ProtocolMode, "ChatCompletions"),
                    new AiUnresolvedValueDto(AiConnectionVocabularyField.AuthMode, "AzureIdentity"),
                    new AiUnresolvedValueDto(AiConnectionVocabularyField.DiscoveryMode, "SomeExternalDiscovery"),
                ],
                availability.UnresolvedValues);
        }
    }

    // Every reason for unavailability is reported through a list as well, because a row that is left out of the
    // list is a connection the operator has no way to reach.
    [Fact]
    public async Task GetByClientAsync_ListsAProfileStoredAgainstAFamilyThisBuildCannotName()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var absentFamily = MakeProfile(clientId, displayName: "Absent Family");
        absentFamily.ProviderKind = "SomeExternalFamily";
        var readable = MakeProfile(clientId, displayName: "Readable Profile");
        db.AiConnectionProfiles.Add(absentFamily);
        db.AiConnectionProfiles.Add(readable);
        await db.SaveChangesAsync();

        var connections = await CreateRepository(db).GetByClientAsync(clientId);

        Assert.Equal(2, connections.Count);
        Assert.Equal(
            AiConnectionUnavailableReason.ProviderFamilyAbsent,
            connections.Single(connection => connection.Id == absentFamily.Id).Availability.Reason);
        Assert.Equal(
            AiConnectionAvailabilityState.Available,
            connections.Single(connection => connection.Id == readable.Id).Availability.State);
    }

    // Every vocabulary column on a profile holds unconstrained text, so a build that declared a member this one
    // does not has already written values this one cannot read. The profile is returned and names each value it
    // could not resolve: an enum parse in the middle of the projection throws and takes out every profile being
    // projected with it.
    [Fact]
    public async Task AProfileHoldingValuesThisBuildCannotNameIsReturnedNamingEachOfThem()
    {
        await using var db = CreateContext();
        var profile = MakeProfile(Guid.NewGuid());
        profile.AuthMode = "SomeExternalCredentialShape";
        profile.DiscoveryMode = "SomeExternalDiscovery";
        var model = profile.ConfiguredModels.First();
        model.OperationKinds = ["SomeExternalOperation"];
        model.SupportedProtocolModes = ["SomeExternalModelProtocol"];
        model.Source = "SomeExternalSource";
        var binding = profile.PurposeBindings.First();
        binding.Purpose = "SomeExternalPurpose";
        binding.ProtocolMode = "SomeExternalBindingProtocol";
        profile.VerificationSnapshot!.Status = "SomeExternalStatus";
        profile.VerificationSnapshot.FailureCategory = "SomeExternalCategory";
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var read = await CreateRepository(db).GetByIdAsync(profile.Id);

        Assert.NotNull(read);
        Assert.Equal(
            new HashSet<AiUnresolvedValueDto>
            {
                new(AiConnectionVocabularyField.AuthMode, "SomeExternalCredentialShape"),
                new(AiConnectionVocabularyField.DiscoveryMode, "SomeExternalDiscovery"),
                new(AiConnectionVocabularyField.OperationKind, "SomeExternalOperation"),
                new(AiConnectionVocabularyField.ProtocolMode, "SomeExternalModelProtocol"),
                new(AiConnectionVocabularyField.ConfiguredModelSource, "SomeExternalSource"),
                new(AiConnectionVocabularyField.Purpose, "SomeExternalPurpose"),
                new(AiConnectionVocabularyField.ProtocolMode, "SomeExternalBindingProtocol"),
                new(AiConnectionVocabularyField.VerificationStatus, "SomeExternalStatus"),
                new(AiConnectionVocabularyField.VerificationFailureCategory, "SomeExternalCategory"),
            },
            read.Availability.UnresolvedValues.ToHashSet());
    }

    // Two rows of one profile store the same vocabulary. Two different values that fail are two things for an
    // operator to fix, and the same value twice is one, so it is named once.
    [Fact]
    public async Task EachDistinctUnresolvedValueIsNamedOnce()
    {
        await using var db = CreateContext();
        var profile = MakeProfile(Guid.NewGuid());
        var models = profile.ConfiguredModels.ToList();
        models[0].SupportedProtocolModes = ["SomeExternalProtocol", "SomeExternalProtocol"];
        models[1].SupportedProtocolModes = ["SomeExternalProtocol", "AnotherExternalProtocol"];
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var read = await CreateRepository(db).GetByIdAsync(profile.Id);

        Assert.NotNull(read);
        Assert.Equal(
            new HashSet<AiUnresolvedValueDto>
            {
                new(AiConnectionVocabularyField.ProtocolMode, "SomeExternalProtocol"),
                new(AiConnectionVocabularyField.ProtocolMode, "AnotherExternalProtocol"),
            },
            read.Availability.UnresolvedValues.ToHashSet());
        Assert.Equal(2, read.Availability.UnresolvedValues.Count);
    }

    // The client list projects every row client-side, so one unreadable value used to fail the call and leave
    // the operator with no connections at all. The unreadable row is listed with the rest, and the rest are
    // projected exactly as they were.
    [Fact]
    public async Task GetByClientAsync_ListsEveryRowWhenOneHoldsAValueThisBuildCannotName()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var unreadable = MakeProfile(clientId, displayName: "Unreadable Profile");
        unreadable.AuthMode = "SomeExternalCredentialShape";
        var readable = MakeProfile(clientId, displayName: "Readable Profile");
        db.AiConnectionProfiles.Add(unreadable);
        db.AiConnectionProfiles.Add(readable);
        await db.SaveChangesAsync();

        var connections = await CreateRepository(db).GetByClientAsync(clientId);

        Assert.Equal(2, connections.Count);
        Assert.Equal(
            new AiUnresolvedValueDto(AiConnectionVocabularyField.AuthMode, "SomeExternalCredentialShape"),
            Assert.Single(connections.Single(connection => connection.Id == unreadable.Id).Availability.UnresolvedValues));

        var readableDto = connections.Single(connection => connection.Id == readable.Id);
        Assert.Empty(readableDto.Availability.UnresolvedValues);
        Assert.Equal(AzureIdentityAuth, readableDto.AuthMode);
        Assert.Equal("gpt-4o", readableDto.GetBoundModelId(AiPurpose.ReviewDefault));
    }

    // The tenant list projects the same way the client list does, so it drops a row for the same reason if it is
    // left alone.
    [Fact]
    public async Task GetByTenantAsync_ListsEveryRowWhenOneHoldsAValueThisBuildCannotName()
    {
        await using var db = CreateContext();
        var tenantId = Guid.NewGuid();
        var unreadable = MakeTenantProfile(tenantId, "Unreadable Profile");
        unreadable.DiscoveryMode = "SomeExternalDiscovery";
        var readable = MakeTenantProfile(tenantId, "Readable Profile");
        db.AiConnectionProfiles.Add(unreadable);
        db.AiConnectionProfiles.Add(readable);
        await db.SaveChangesAsync();

        var connections = await CreateRepository(db).GetByTenantAsync(tenantId);

        Assert.Equal(2, connections.Count);
        Assert.Equal(
            new AiUnresolvedValueDto(AiConnectionVocabularyField.DiscoveryMode, "SomeExternalDiscovery"),
            Assert.Single(connections.Single(connection => connection.Id == unreadable.Id).Availability.UnresolvedValues));
        Assert.Empty(connections.Single(connection => connection.Id == readable.Id).Availability.UnresolvedValues);
    }

    // The tolerant read stands where an enum parse used to, so a profile that holds only values this build
    // recognises has to project to what it projected before: every position is read back as stored, and nothing
    // is reported as unresolved.
    [Fact]
    public async Task AProfileHoldingOnlyRecognisedValuesProjectsEveryPositionAsStored()
    {
        await using var db = CreateContext();
        var profile = MakeProfile(Guid.NewGuid());
        profile.VerificationSnapshot!.Status = AiVerificationStatus.Failed.ToString();
        profile.VerificationSnapshot.FailureCategory = AiVerificationFailureCategory.Credentials.ToString();
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var read = await CreateRepository(db).GetByIdAsync(profile.Id);

        Assert.NotNull(read);
        Assert.Empty(read.Availability.UnresolvedValues);
        Assert.Equal(AzureIdentityAuth, read.AuthMode);
        Assert.Equal(AiDiscoveryMode.ManualOnly, read.DiscoveryMode);

        var chatModel = read.ConfiguredModels.Single(model => model.RemoteModelId == "gpt-4o");
        Assert.Equal(new[] { AiOperationKind.Chat }, chatModel.OperationKinds);
        Assert.Equal(
            new[] { ProviderDeclaredProtocolModes.Auto, AzureResponses, AzureChatCompletions },
            chatModel.SupportedProtocolModes);
        Assert.Equal(AiConfiguredModelSource.Manual, chatModel.Source);

        var embeddingBinding = read.PurposeBindings.Single(binding => binding.Purpose == AiPurpose.EmbeddingDefault);
        Assert.Equal(ProviderDeclaredProtocolModes.Embeddings, embeddingBinding.ProtocolMode);

        Assert.Equal(AiVerificationStatus.Failed, read.Verification.Status);
        Assert.Equal(AiVerificationFailureCategory.Credentials, read.Verification.FailureCategory);
    }

    private static AiConnectionProfileRecord MakeTenantProfile(Guid tenantId, string displayName)
    {
        var profile = MakeProfile(Guid.NewGuid(), displayName: displayName);
        profile.ClientId = null;
        profile.TenantId = tenantId;
        return profile;
    }

    // A row carrying neither owner is read as unrestricted, which both write guards do with an empty
    // identifier. Looking a policy up for Guid.Empty would describe the row against whatever that lookup
    // happened to answer with, so one read and the guard beside it could disagree about the same row.
    [Fact]
    public async Task GetByIdAsync_ForARowWithNeitherOwner_DoesNotLookUpAPolicy()
    {
        await using var db = CreateContext();
        var profile = MakeProfile(Guid.NewGuid());
        profile.ClientId = null;
        profile.TenantId = null;
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        // Refuses this profile's family if it is ever asked.
        var policies = PoliciesAllowing("meisterdev/anthropic");

        var read = await CreateRepository(db, providerPolicies: policies).GetByIdAsync(profile.Id);

        Assert.NotNull(read);
        Assert.Equal(AiConnectionAvailabilityState.Available, read.Availability.State);
        await policies.DidNotReceive().GetForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await policies.DidNotReceive().GetForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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

    // Active means "in use", not "the one". A client mixing providers keeps several profiles active and decides
    // which model serves which role through logical models, so activating one must leave the others alone.
    [Fact]
    public async Task ActivateAsync_LeavesAnAlreadyActiveProfileActive()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();

        var profileA = MakeProfile(clientId, true, displayName: "Primary");
        var profileB = MakeProfile(clientId, displayName: "Secondary");
        db.AiConnectionProfiles.AddRange(profileA, profileB);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.ActivateAsync(profileB.Id);

        Assert.True(result.Activated);

        var refreshedA = await db.AiConnectionProfiles.FindAsync(profileA.Id);
        var refreshedB = await db.AiConnectionProfiles.FindAsync(profileB.Id);

        Assert.NotNull(refreshedA);
        Assert.True(refreshedA.IsActive);

        Assert.NotNull(refreshedB);
        Assert.True(refreshedB.IsActive);
    }

    // Purpose bindings are the legacy selection layer and the editor no longer creates them, so requiring them
    // for activation made every profile created today unactivatable. A verified profile with none activates.
    [Fact]
    public async Task ActivateAsync_VerifiedProfileWithNoPurposeBindings_Activates()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();

        var profile = MakeProfile(clientId, purposes: []);
        profile.PurposeBindings.Clear();
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.ActivateAsync(profile.Id);

        Assert.True(result.Activated);

        var refreshed = await db.AiConnectionProfiles.FindAsync(profile.Id);
        Assert.NotNull(refreshed);
        Assert.True(refreshed.IsActive);
    }

    [Fact]
    public async Task ActivateAsync_UnverifiedProfile_RefusesAndSaysWhy()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId, verified: false);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.ActivateAsync(profile.Id);

        Assert.False(result.Activated);
        Assert.Contains("verified", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivateAsync_ConnectionNotFound_RefusesAndSaysWhy()
    {
        await using var db = CreateContext();
        var repo = CreateRepository(db);

        var result = await repo.ActivateAsync(Guid.NewGuid());

        Assert.False(result.Activated);
        Assert.Contains("no longer exists", result.Reason!, StringComparison.Ordinal);
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
    public async Task GetActiveBindingForPurposeAsync_MissingProRvPrefilterBinding_FallsBackToReviewDefault()
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
        var result = await repo.GetActiveBindingForPurposeAsync(clientId, AiPurpose.ProRVPrefilter);

        Assert.NotNull(result);
        Assert.Equal(AiPurpose.ReviewDefault, result.Binding.Purpose);
        Assert.Equal("gpt-4o", result.Model.RemoteModelId);
    }

    [Fact]
    public async Task GetActiveBindingForPurposeAsync_DedicatedProRvPrefilterBinding_UsesDedicatedBinding()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var activeProfile = MakeProfile(clientId, true, true, "Test Connection", AiPurpose.ReviewDefault, AiPurpose.ProRVPrefilter);
        db.AiConnectionProfiles.Add(activeProfile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.GetActiveBindingForPurposeAsync(clientId, AiPurpose.ProRVPrefilter);

        Assert.NotNull(result);
        Assert.Equal(AiPurpose.ProRVPrefilter, result.Binding.Purpose);
    }

    [Fact]
    public async Task GetModelBindingAsync_ChatModel_ReturnsSynthesizedBinding()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId, true);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();
        var chatModelId = profile.ConfiguredModels.First(model => model.RemoteModelId == "gpt-4o").Id;

        var repo = CreateRepository(db);
        var result = await repo.GetModelBindingAsync(clientId, chatModelId);

        Assert.NotNull(result);
        Assert.Equal("gpt-4o", result!.Model.RemoteModelId);
        Assert.Equal(chatModelId, result.Binding.ConfiguredModelId);
        Assert.True(result.Model.SupportsChat);
    }

    [Fact]
    public async Task GetModelBindingAsync_EmbeddingModel_ReturnsNull()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId, true);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();
        var embeddingModelId = profile.ConfiguredModels.First(model => model.RemoteModelId == "text-embedding-3-large").Id;

        var repo = CreateRepository(db);
        var result = await repo.GetModelBindingAsync(clientId, embeddingModelId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetModelBindingAsync_UnknownModel_ReturnsNull()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var profile = MakeProfile(clientId, true);
        db.AiConnectionProfiles.Add(profile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.GetModelBindingAsync(clientId, Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveBindingForPurposeAsync_MissingTriageBinding_FallsBackToReviewLowEffort()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var activeProfile = MakeProfile(
            clientId,
            true,
            true,
            "Test Connection",
            AiPurpose.ReviewDefault,
            AiPurpose.ReviewLowEffort,
            AiPurpose.MemoryReconsideration,
            AiPurpose.EmbeddingDefault);
        db.AiConnectionProfiles.Add(activeProfile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.GetActiveBindingForPurposeAsync(clientId, AiPurpose.ReviewTriage);

        // Triage has no dedicated binding -> resolves to the cheap low-effort model, not the size heuristic.
        Assert.NotNull(result);
        Assert.Equal(AiPurpose.ReviewLowEffort, result.Binding.Purpose);
    }

    [Fact]
    public async Task GetActiveBindingForPurposeAsync_MissingTriageAndLowEffortBinding_FallsBackToReviewDefault()
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
        var result = await repo.GetActiveBindingForPurposeAsync(clientId, AiPurpose.ReviewTriage);

        // ReviewTriage -> ReviewLowEffort (missing) -> ReviewDefault.
        Assert.NotNull(result);
        Assert.Equal(AiPurpose.ReviewDefault, result.Binding.Purpose);
    }

    [Fact]
    public async Task GetActiveBindingForPurposeAsync_DedicatedTriageBinding_UsesDedicatedBinding()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var activeProfile = MakeProfile(
            clientId,
            true,
            true,
            "Test Connection",
            AiPurpose.ReviewDefault,
            AiPurpose.ReviewLowEffort,
            AiPurpose.ReviewTriage);
        db.AiConnectionProfiles.Add(activeProfile);
        await db.SaveChangesAsync();

        var repo = CreateRepository(db);
        var result = await repo.GetActiveBindingForPurposeAsync(clientId, AiPurpose.ReviewTriage);

        // An explicit triage binding wins over the fallback chain.
        Assert.NotNull(result);
        Assert.Equal(AiPurpose.ReviewTriage, result.Binding.Purpose);
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
        Assert.False(activated.Activated);
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
                authMode: AzureApiKey,
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
                "meisterdev/azureOpenAi",
                "https://my-openai.openai.azure.com/",
                AzureApiKey,
                AiDiscoveryMode.ManualOnly,
                [
                    new AiConfiguredModelDto(
                        Guid.Empty,
                        "text-embedding-3-small",
                        "text-embedding-3-small",
                        [AiOperationKind.Embedding],
                        [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings],
                        "cl100k_base",
                        8192,
                        1536,
                        false,
                        false,
                        AiConfiguredModelSource.Manual,
                        null,
                        0.2m,
                        0.4m,
                        200_000,
                        0.1m),
                ],
                [
                    new AiPurposeBindingDto(Guid.Empty, AiPurpose.EmbeddingDefault, null, "text-embedding-3-small", ProviderDeclaredProtocolModes.Embeddings),
                ],
                null,
                null,
                "secret"));

        var reloaded = await repo.GetByIdAsync(created.Id);

        Assert.NotNull(reloaded);
        var configuredModel = Assert.Single(reloaded.ConfiguredModels);
        Assert.Equal(0.2m, configuredModel.InputCostPer1MUsd);
        Assert.Equal(0.4m, configuredModel.OutputCostPer1MUsd);
        Assert.Equal(200_000, configuredModel.MaxContextTokens);
        Assert.Equal(0.1m, configuredModel.CachedInputCostPer1MUsd);
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
                "meisterdev/azureOpenAi",
                "https://my-openai.openai.azure.com/",
                AzureApiKey,
                AiDiscoveryMode.ManualOnly,
                [
                    new AiConfiguredModelDto(
                        Guid.Empty,
                        "gpt-4o",
                        "gpt-4o",
                        [AiOperationKind.Chat],
                        [ProviderDeclaredProtocolModes.Auto, AzureResponses, AzureChatCompletions],
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

    // The stored blob holds an envelope, so a provider whose credential is three fields rather than one needs no
    // schema change. What a driver receives is unchanged: the single value, for a mode whose credential is one.
    [Fact]
    public async Task AddAsync_StoresTheCredentialAsAnEnvelopeAndReadsBackThePlainKey()
    {
        await using var db = CreateContext();
        var codec = CreateCodec();
        var repo = CreateRepository(db, codec: codec);

        var created = await repo.AddAsync(Guid.NewGuid(), CreateWriteRequest(secret: "sk-envelope-check"));

        var record = await db.AiConnectionProfiles.FirstAsync(profile => profile.Id == created.Id);
        var storedInside = codec.Unprotect(record.ProtectedSecret!, "AiConnectionApiKey");

        Assert.Equal("sk-envelope-check", created.Secret);
        Assert.Contains(ProviderSecretEnvelope.ApiKeyField, storedInside, StringComparison.Ordinal);
        Assert.Contains("\"v\":1", storedInside, StringComparison.Ordinal);
    }

    // The configuration half of the allow-list. Refusing at write time means an operator learns while looking at
    // the form, instead of a review failing later against a provider the tenant had already forbidden.
    [Fact]
    public async Task AddAsync_RefusesAProviderTheTenantDoesNotPermit()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy(["meisterdev/openAiCompatible"]));
        var repo = new AiConnectionRepository(db, CreateCodec(), policies, DeclaringProviderFamilies.None(), EgressUrlPolicy.Locked);

        // CreateWriteRequest builds an AzureOpenAi profile, which this tenant does not permit.
        var failure = await Assert.ThrowsAsync<ProviderKindNotPermittedException>(() => repo.AddAsync(clientId, CreateWriteRequest()));

        Assert.Equal("meisterdev/azureOpenAi", failure.ProviderKind);
        Assert.Contains("meisterdev/openAiCompatible", failure.Message, StringComparison.Ordinal);
        Assert.Empty(db.AiConnectionProfiles);
    }

    // An allow-list this build can no longer read permits nothing, so a write is refused too. The refusal names
    // the entry, because "permitted: none" on its own gives an operator nothing to correct.
    [Fact]
    public async Task AddAsync_RefusesUnderAnAllowListNamingNoKnownFamily()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        // The policy is read before the substitute is configured: building one inside Returns() would attach
        // the return value to the substitute's own call rather than to the policy read.
        var unreadable = TenantProviderPolicy.FromStored(["Acme.Llm"], [], DeclaringProviderFamilies.None());
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(clientId, Arg.Any<CancellationToken>()).Returns(unreadable);
        var repo = new AiConnectionRepository(db, CreateCodec(), policies, DeclaringProviderFamilies.None(), EgressUrlPolicy.Locked);

        var failure = await Assert.ThrowsAsync<ProviderKindNotPermittedException>(() => repo.AddAsync(clientId, CreateWriteRequest()));

        Assert.Contains("Acme.Llm", failure.Message, StringComparison.Ordinal);
        Assert.Empty(db.AiConnectionProfiles);
    }

    [Fact]
    public async Task AddAsync_AllowsAProviderTheTenantPermits()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy(["meisterdev/azureOpenAi"]));
        var repo = new AiConnectionRepository(db, CreateCodec(), policies, DeclaringProviderFamilies.None(), EgressUrlPolicy.Locked);

        var created = await repo.AddAsync(clientId, CreateWriteRequest());

        Assert.Equal("meisterdev/azureOpenAi", created.ProviderKind);
    }

    // A tenant that has stated no policy is unrestricted, so nothing changes for it.
    [Fact]
    public async Task AddAsync_WithNoStatedPolicy_IsUnaffected()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(TenantProviderPolicy.Unrestricted);
        var repo = new AiConnectionRepository(db, CreateCodec(), policies, DeclaringProviderFamilies.None(), EgressUrlPolicy.Locked);

        Assert.NotNull(await repo.AddAsync(clientId, CreateWriteRequest()));
    }

    // The destination half of the tenant policy, refused where the operator can see it. A profile pointed at a
    // host the tenant has not permitted is the case a provider-family list cannot catch on its own.
    [Fact]
    public async Task AddAsync_RefusesAnEndpointTheTenantDoesNotPermit()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([], ["opencode.ai"]));
        var repo = new AiConnectionRepository(db, CreateCodec(), policies, DeclaringProviderFamilies.None(), EgressUrlPolicy.Locked);

        // CreateWriteRequest points at an Azure host, which this tenant has not permitted.
        var failure = await Assert.ThrowsAsync<ProviderKindNotPermittedException>(() => repo.AddAsync(clientId, CreateWriteRequest()));

        Assert.Contains("opencode.ai", failure.Message, StringComparison.Ordinal);
        Assert.Empty(db.AiConnectionProfiles);
    }

    [Fact]
    public async Task AddAsync_AllowsAnEndpointTheTenantPermits()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(new TenantProviderPolicy([], [".openai.azure.com"]));
        var repo = new AiConnectionRepository(db, CreateCodec(), policies, DeclaringProviderFamilies.None(), EgressUrlPolicy.Locked);

        Assert.NotNull(await repo.AddAsync(clientId, CreateWriteRequest()));
    }

    // Rows written before the envelope existed hold a bare protected string. They have to keep resolving, or
    // adopting the envelope would quietly break every profile already configured.
    [Fact]
    public async Task GetAsync_StillReadsACredentialStoredBeforeTheEnvelopeExisted()
    {
        await using var db = CreateContext();
        var codec = CreateCodec();
        var repo = CreateRepository(db, codec: codec);
        var created = await repo.AddAsync(Guid.NewGuid(), CreateWriteRequest(secret: "sk-will-be-replaced"));

        // Rewrite the column the way the previous build wrote it: the raw key, protected, with no envelope.
        var record = await db.AiConnectionProfiles.FirstAsync(profile => profile.Id == created.Id);
        record.ProtectedSecret = codec.Protect("sk-legacy-format", "AiConnectionApiKey");
        await db.SaveChangesAsync();

        var reloaded = await repo.GetByIdAsync(created.Id);

        Assert.Equal("sk-legacy-format", reloaded!.Secret);
    }
}
