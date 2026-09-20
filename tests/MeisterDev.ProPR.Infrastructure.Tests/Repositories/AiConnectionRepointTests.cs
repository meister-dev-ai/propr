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
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Moving a connection to a different provider family, and reading one whose family still holds the spelling
///     it superseded.
/// </summary>
public sealed class AiConnectionRepointTests
{
    private const string DepartingKey = "tests/departing";
    private const string ArrivingKey = "tests/arriving";

    private const string DepartingApiKey = DepartingKey + ":ApiKey";

    private const string DepartingAdc = DepartingKey + ":GcpAdc";

    private const string DepartingResponses = DepartingKey + ":Responses";

    private const string DepartingGenerateContent = DepartingKey + ":GoogleGenerateContent";

    private const string ArrivingApiKey = ArrivingKey + ":ApiKey";

    [Fact]
    public async Task RepointingClearsTheDepartingFamilysSettingsAndCredential()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(
            db,
            DepartingKey,
            settings: new Dictionary<string, string> { ["project"] = "departing-project" },
            credential: "departing-service-account");

        var repository = CreateRepository(db);

        Assert.True(await repository.UpdateAsync(connectionId, WriteRequest(ArrivingKey)));

        var record = await db.AiConnectionProfiles
            .Include(profile => profile.ConfiguredModels)
            .Include(profile => profile.PurposeBindings)
            .SingleAsync(profile => profile.Id == connectionId);

        // The stored values named the family that left, so all three are written as the arriving family names
        // them: its declared key, and its credential shape qualified by that key.
        Assert.Equal(ArrivingKey, record.ProviderKind);
        Assert.Equal(ArrivingApiKey, record.AuthMode);
        Assert.Null(record.ProviderSettings);

        // The envelope is empty, so the connection reports that it needs a credential rather than presenting one
        // the departing family wrote.
        Assert.Null(record.ProtectedSecret);

        // The rows survive the move; only the vocabulary on them belonged to the family that left.
        Assert.NotEmpty(record.ConfiguredModels);
        Assert.NotEmpty(record.PurposeBindings);
        Assert.All(
            record.ConfiguredModels,
            model => Assert.Equal([ProviderDeclaredProtocolModes.Auto], model.SupportedProtocolModes));
        Assert.All(
            record.PurposeBindings,
            binding => Assert.Equal(ProviderDeclaredProtocolModes.Auto, binding.ProtocolMode));
    }

    [Fact]
    public async Task RepointingBackToTheOriginalFamilyClearsTheIntermediateFamilysValuesTheSameWay()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(
            db,
            DepartingKey,
            settings: new Dictionary<string, string> { ["project"] = "original-project" },
            credential: "original-credential");

        var repository = CreateRepository(db);

        Assert.True(await repository.UpdateAsync(connectionId, WriteRequest(ArrivingKey)));

        // The intermediate family writes its own values before the move back.
        var intermediate = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == connectionId);
        intermediate.ProviderSettings = new Dictionary<string, string> { ["region"] = "intermediate-region" };
        intermediate.ProtectedSecret = "intermediate-credential";
        await db.SaveChangesAsync();

        Assert.True(await repository.UpdateAsync(connectionId, WriteRequest(DepartingKey)));

        var record = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == connectionId);

        Assert.Null(record.ProviderSettings);
        Assert.Null(record.ProtectedSecret);
    }

    [Fact]
    public async Task AnEditThatKeepsTheFamilyLeavesTheSettingsAndCredentialAlone()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(
            db,
            DepartingKey,
            settings: new Dictionary<string, string> { ["project"] = "kept-project" },
            credential: "kept-credential");

        var repository = CreateRepository(db);

        Assert.True(
            await repository.UpdateAsync(
                connectionId,
                WriteRequest(DepartingKey, displayName: "Renamed")));

        var record = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == connectionId);

        Assert.Equal("Renamed", record.DisplayName);
        Assert.Equal("kept-project", record.ProviderSettings!["project"]);
        Assert.Equal("kept-credential", record.ProtectedSecret);
    }

    // The post-condition on a move: nothing about to be written may carry the key of the family being left.
    // A request cannot carry one today, because it names its modes as host enumerations; this is the check that
    // holds once a family supplies its own vocabulary, and it names where the value sits because that is what
    // an operator has to clear.
    [Fact]
    public void AValueStillQualifiedByTheDepartingFamilyIsRefusedNamingWhereItSits()
    {
        var model = new AiConfiguredModelRecord
        {
            Id = Guid.NewGuid(),
            RemoteModelId = "gpt-4o",
            DisplayName = "gpt-4o",
            OperationKinds = [nameof(AiOperationKind.Chat)],
            SupportedProtocolModes = [DepartingResponses],
            Source = "Manual",
        };

        var refusal = Assert.Throws<ProviderRepointNotClearedException>(() =>
            AiConnectionRepository.RefuseDepartingVocabulary(
                DepartingKey,
                "ApiKey",
                [model],
                []));

        Assert.Equal(DepartingKey, refusal.DepartingKey);
        Assert.Contains("gpt-4o", refusal.Location, StringComparison.Ordinal);
        Assert.Contains(DepartingKey, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueQualifiedByTheArrivingFamilyOrByNobodyIsNotRefused()
    {
        var binding = new AiPurposeBindingRecord
        {
            Id = Guid.NewGuid(),
            Purpose = nameof(AiPurpose.ReviewDefault),
            ProtocolMode = ProviderDeclaredProtocolModes.Auto,
        };

        var model = new AiConfiguredModelRecord
        {
            Id = Guid.NewGuid(),
            RemoteModelId = "gpt-4o",
            DisplayName = "gpt-4o",
            OperationKinds = [nameof(AiOperationKind.Chat)],
            SupportedProtocolModes = [ProviderVocabulary.Compose(ArrivingKey, ProviderDeclaredProtocolModes.Auto)],
            Source = "Manual",
        };

        AiConnectionRepository.RefuseDepartingVocabulary(
            DepartingKey,
            ArrivingApiKey,
            [model],
            [binding]);
    }

    // Between a family declaring its key and its rows being rewritten, a connection holds the old spelling on
    // three axes. Each one resolves through the family that superseded it, and the row keeps what it holds.
    [Fact]
    public async Task AConnectionHoldingSupersededSpellingsResolvesAndIsNotRewritten()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(db, DepartingKey, storedIdentity: "LegacyVertex");

        var record = await db.AiConnectionProfiles
            .Include(profile => profile.ConfiguredModels)
            .Include(profile => profile.PurposeBindings)
            .SingleAsync(profile => profile.Id == connectionId);
        record.AuthMode = "LegacyVertexKey";
        record.ConfiguredModels.Single().SupportedProtocolModes = ["LegacyVertexProtocol"];
        record.PurposeBindings.Single().ProtocolMode = "LegacyVertexProtocol";
        await db.SaveChangesAsync();

        var repository = CreateRepository(db);
        var dto = await repository.GetByIdAsync(connectionId);

        Assert.NotNull(dto);
        Assert.Equal(DepartingKey, dto.ProviderKind);
        Assert.Equal(DepartingAdc, dto.AuthMode);
        Assert.Equal([DepartingGenerateContent], dto.ConfiguredModels.Single().SupportedProtocolModes);
        Assert.Equal(DepartingGenerateContent, dto.PurposeBindings.Single().ProtocolMode);
        Assert.Equal(AiConnectionAvailabilityState.Available, dto.Availability.State);

        // Reading through an alias writes nothing back.
        var stored = await db.AiConnectionProfiles
            .Include(profile => profile.ConfiguredModels)
            .Include(profile => profile.PurposeBindings)
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == connectionId);

        Assert.Equal("LegacyVertex", stored.ProviderKind);
        Assert.Equal("LegacyVertexKey", stored.AuthMode);
        Assert.Equal(["LegacyVertexProtocol"], stored.ConfiguredModels.Single().SupportedProtocolModes);
        Assert.Equal("LegacyVertexProtocol", stored.PurposeBindings.Single().ProtocolMode);
    }

    [Fact]
    public async Task AStoredValueMatchingNoCurrentNameAndNoSupersededOneIsReportedUnavailable()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(db, DepartingKey, storedIdentity: "someone/elsesFamily");

        var dto = await CreateRepository(db).GetByIdAsync(connectionId);

        Assert.NotNull(dto);
        Assert.Equal(AiConnectionAvailabilityState.Unavailable, dto.Availability.State);
        Assert.Equal(AiConnectionUnavailableReason.ProviderFamilyAbsent, dto.Availability.Reason);
        Assert.Equal("someone/elsesFamily", dto.Availability.ProviderIdentity);
    }

    // The refusal has to read the values the request carried, so it runs before the protocol modes are reset. A
    // request naming a mode the departing family qualified is refused, and the stored rows are left as they were.
    [Fact]
    public async Task ARepointCarryingTheDepartingFamilysProtocolModeIsRefusedAndWritesNothing()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(
            db,
            DepartingKey,
            settings: new Dictionary<string, string> { ["project"] = "departing-project" });

        var repository = CreateRepository(db);

        var refusal = await Assert.ThrowsAsync<ProviderRepointNotClearedException>(() => repository.UpdateAsync(
            connectionId, WriteRequest(ArrivingKey, protocolMode: DepartingResponses)));

        Assert.Equal(DepartingKey, refusal.DepartingKey);
        Assert.Contains("gpt-4o", refusal.Location, StringComparison.Ordinal);

        var stored = await db.AiConnectionProfiles.AsNoTracking().SingleAsync(profile => profile.Id == connectionId);

        Assert.Equal(DepartingKey, stored.ProviderKind);
        Assert.Equal("departing-project", stored.ProviderSettings!["project"]);
    }

    private static AiConnectionWriteRequestDto WriteRequest(
        string providerKind,
        string displayName = "Repointed Connection",
        string protocolMode = ProviderDeclaredProtocolModes.Auto)
    {
        var model = new AiConfiguredModelDto(
            Guid.Empty,
            "gpt-4o",
            "gpt-4o",
            [AiOperationKind.Chat],
            [protocolMode],
            null,
            null,
            null,
            true,
            true);

        return new AiConnectionWriteRequestDto(
            displayName,
            providerKind,
            "https://api.example.com/v1",
            ProviderVocabulary.Compose(providerKind, "ApiKey"),
            AiDiscoveryMode.ManualOnly,
            [model],
            [new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewDefault, null, "gpt-4o", protocolMode)],
            null,
            null,
            null);
    }

    private static async Task<Guid> SeedAsync(
        MeisterProPRDbContext db,
        string providerKind,
        string? storedIdentity = null,
        IReadOnlyDictionary<string, string>? settings = null,
        string? credential = null)
    {
        var connectionId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.AiConnectionProfiles.Add(
            new AiConnectionProfileRecord
            {
                Id = connectionId,
                ClientId = Guid.NewGuid(),
                DisplayName = "Seeded Connection",
                ProviderKind = storedIdentity ?? providerKind.ToString(),
                BaseUrl = "https://api.example.com/v1",
                AuthMode = "ApiKey",
                DiscoveryMode = nameof(AiDiscoveryMode.ManualOnly),
                DefaultHeaders = [],
                DefaultQueryParams = [],
                ProviderSettings = settings?.ToDictionary(StringComparer.Ordinal),
                ProtectedSecret = credential,
                CreatedAt = now,
                UpdatedAt = now,
                ConfiguredModels =
                [
                    new AiConfiguredModelRecord
                    {
                        Id = modelId,
                        ConnectionProfileId = connectionId,
                        RemoteModelId = "gpt-4o",
                        DisplayName = "gpt-4o",
                        OperationKinds = [nameof(AiOperationKind.Chat)],
                        SupportedProtocolModes = [ProviderDeclaredProtocolModes.Auto],
                        Source = "Manual",
                    },
                ],
                PurposeBindings =
                [
                    new AiPurposeBindingRecord
                    {
                        Id = Guid.NewGuid(),
                        ConnectionProfileId = connectionId,
                        ConfiguredModelId = modelId,
                        Purpose = nameof(AiPurpose.ReviewDefault),
                        ProtocolMode = ProviderDeclaredProtocolModes.Auto,
                        IsEnabled = true,
                        CreatedAt = now,
                        UpdatedAt = now,
                    },
                ],
            });

        await db.SaveChangesAsync();
        return connectionId;
    }

    // Two families: the one being left, which supersedes the spellings the seeded rows hold, and the one being
    // moved to.
    private static IAiProviderDriverRegistry TwoFamilies()
    {
        var departing = DeclaringProviderFamilies.DriverFor(
            new ProviderDeclaration
            {
                Key = DepartingKey,
                Label = "Departing",
                Version = "1.0",
                ContractVersion = ProviderContract.Version,
                LegacyNames = ProviderLegacyNames.Create(
                    ["LegacyVertex"],
                    new Dictionary<string, string> { [DepartingAdc] = "LegacyVertexKey" },
                    new Dictionary<string, string>
                    {
                        [DepartingGenerateContent] = "LegacyVertexProtocol",
                    }),
                AuthModes =
                [
                    new ProviderDeclaredAuthMode(DepartingApiKey, [AiCredentialFieldSupport.ApiKey]),
                    new ProviderDeclaredAuthMode(DepartingAdc, [AiCredentialFieldSupport.ApiKey]),
                ],
                ProtocolModes = new ProviderDeclaredProtocolModes(
                [
                    ProviderDeclaredProtocolModes.Auto,
                    DepartingResponses,
                    DepartingGenerateContent,
                ]),
                ConformanceInputs = new ProviderConformanceInputs(DepartingApiKey),
            });

        var arriving = DeclaringProviderFamilies.DriverFor(
            new ProviderDeclaration
            {
                Key = ArrivingKey,
                Label = "Arriving",
                Version = "1.0",
                ContractVersion = ProviderContract.Version,
                AuthModes = [new ProviderDeclaredAuthMode(ArrivingApiKey, [AiCredentialFieldSupport.ApiKey])],
                ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
                ConformanceInputs = new ProviderConformanceInputs(ArrivingApiKey),
            });

        var registry = Substitute.For<IAiProviderDriverRegistry>();
        registry.RegisteredKinds.Returns([DepartingKey, ArrivingKey]);
        registry.IsRegistered(Arg.Any<string>()).Returns(call =>
            call.Arg<string>() is DepartingKey or ArrivingKey);
        registry.GetRequired(DepartingKey).Returns(departing);
        registry.GetRequired(ArrivingKey).Returns(arriving);
        return registry;
    }

    private static AiConnectionRepository CreateRepository(MeisterProPRDbContext db)
    {
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TenantProviderPolicy.Unrestricted);
        policies.GetForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TenantProviderPolicy.Unrestricted);

        return new AiConnectionRepository(db, CreateCodec(), policies, TwoFamilies(), EgressUrlPolicy.Locked);
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.AiConnectionRepointTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        return new SecretProtectionCodec(
            services.BuildServiceProvider()
                .GetRequiredService<IDataProtectionProvider>());
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
