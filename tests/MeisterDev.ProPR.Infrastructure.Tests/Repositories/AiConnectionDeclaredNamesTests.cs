// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
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
///     Saving a connection of a family that declares its own identity key and qualifies the modes it owns.
/// </summary>
/// <remarks>
///     A family's rows are rewritten onto its declared names once, by a migration. Everything after that is an
///     ordinary edit, and an edit that wrote the product's own member names back would undo the migration on the
///     first save and invalidate a verification an operator would then have to repeat. Both spellings are read
///     while a family is between the two, so both are exercised here.
/// </remarks>
public sealed class AiConnectionDeclaredNamesTests
{
    private const string DeclaredKey = "meisterdev/googleVertex";
    private const string DeclaredAuthMode = DeclaredKey + ":GcpAdc";
    private const string DeclaredProtocolMode = DeclaredKey + ":GoogleGenerateContent";
    private const string DeclaredApiKeyMode = DeclaredKey + ":ApiKey";

    // A wire shape neither this family nor the host claims, which a binding holds after the family
    // withdraws a shape.
    private const string UndeclaredProtocolMode = DeclaredKey + ":ChatCompletions";

    [Fact]
    public async Task AConnectionStoredUnderTheDeclaredNamesReadsBackAsTheFamilyAndItsModes()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(db, DeclaredKey, DeclaredAuthMode, DeclaredProtocolMode);

        var dto = await CreateRepository(db).GetByIdAsync(connectionId);

        Assert.NotNull(dto);
        Assert.Equal("meisterdev/googleVertex", dto.ProviderKind);
        Assert.Equal(DeclaredAuthMode, dto.AuthMode);
        Assert.Equal(
            [ProviderDeclaredProtocolModes.Auto, DeclaredProtocolMode],
            dto.ConfiguredModels.Single().SupportedProtocolModes);
        Assert.Equal(DeclaredProtocolMode, dto.PurposeBindings.Single().ProtocolMode);
        Assert.Equal(AiConnectionAvailabilityState.Available, dto.Availability.State);
    }

    [Fact]
    public async Task SavingAConnectionStoredUnderTheDeclaredNamesLeavesEveryStoredSpellingAsItIs()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(db, DeclaredKey, DeclaredAuthMode, DeclaredProtocolMode);
        var repository = CreateRepository(db);

        var read = await repository.GetByIdAsync(connectionId);
        Assert.NotNull(read);

        Assert.True(await repository.UpdateAsync(connectionId, WriteRequestFrom(read, displayName: "Renamed")));

        var stored = await ReadStoredAsync(db, connectionId);
        Assert.Equal("Renamed", stored.DisplayName);
        Assert.Equal(DeclaredKey, stored.ProviderKind);
        Assert.Equal(DeclaredAuthMode, stored.AuthMode);
        Assert.Equal(
            [ProviderDeclaredProtocolModes.Auto, DeclaredProtocolMode],
            stored.ConfiguredModels.Single().SupportedProtocolModes);
        Assert.Equal(DeclaredProtocolMode, stored.PurposeBindings.Single().ProtocolMode);
    }

    [Fact]
    public async Task SavingAConnectionStoredUnderTheDeclaredNamesKeepsItsVerification()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(db, DeclaredKey, DeclaredAuthMode, DeclaredProtocolMode);
        var repository = CreateRepository(db);

        var read = await repository.GetByIdAsync(connectionId);
        Assert.NotNull(read);
        Assert.Equal(AiVerificationStatus.Verified, read.Verification.Status);

        Assert.True(await repository.UpdateAsync(connectionId, WriteRequestFrom(read, displayName: "Renamed")));

        var stored = await ReadStoredAsync(db, connectionId);
        Assert.Equal(nameof(AiVerificationStatus.Verified), stored.VerificationSnapshot!.Status);
    }

    // A request carries a spelling of its own, and a caller that is not the console can submit the one the
    // family superseded. It names the shape the row was verified against, so the verification stays.
    [Fact]
    public async Task SavingWithTheSupersededCredentialShapeKeepsTheVerification()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(db, DeclaredKey, DeclaredAuthMode, DeclaredProtocolMode);
        var repository = CreateRepository(db);

        var read = await repository.GetByIdAsync(connectionId);
        Assert.NotNull(read);

        Assert.True(await repository.UpdateAsync(connectionId, WriteRequestFrom(read) with { AuthMode = "GcpAdc" }));

        var stored = await ReadStoredAsync(db, connectionId);
        Assert.Equal(nameof(AiVerificationStatus.Verified), stored.VerificationSnapshot!.Status);

        // And the row keeps the spelling it held, because both name the same shape.
        Assert.Equal(DeclaredAuthMode, stored.AuthMode);
    }

    // The same edit on a row that still holds what the family superseded: the spelling a row holds is the
    // spelling it keeps, whichever of the two it is.
    [Fact]
    public async Task SavingAConnectionStillHoldingTheSupersededNamesLeavesThemAsTheyAre()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(
            db,
            "GoogleVertex",
            "GcpAdc",
            "GoogleGenerateContent");
        var repository = CreateRepository(db);

        var read = await repository.GetByIdAsync(connectionId);
        Assert.NotNull(read);

        Assert.True(await repository.UpdateAsync(connectionId, WriteRequestFrom(read, displayName: "Renamed")));

        var stored = await ReadStoredAsync(db, connectionId);
        Assert.Equal("GoogleVertex", stored.ProviderKind);
        Assert.Equal("GcpAdc", stored.AuthMode);
        Assert.Equal("GoogleGenerateContent", stored.PurposeBindings.Single().ProtocolMode);
        Assert.Equal(nameof(AiVerificationStatus.Verified), stored.VerificationSnapshot!.Status);
    }

    // The verification still goes when what was verified changes. Comparing what the stored values mean rather
    // than how they are spelled must not turn the reset off.
    [Fact]
    public async Task ChangingWhereTheConnectionGoesStillInvalidatesItsVerification()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(db, DeclaredKey, DeclaredAuthMode, DeclaredProtocolMode);
        var repository = CreateRepository(db);

        var read = await repository.GetByIdAsync(connectionId);
        Assert.NotNull(read);

        Assert.True(
            await repository.UpdateAsync(
                connectionId,
                WriteRequestFrom(read) with { BaseUrl = "https://us-east1-aiplatform.googleapis.com" }));

        var stored = await ReadStoredAsync(db, connectionId);
        Assert.Equal(nameof(AiVerificationStatus.NeverVerified), stored.VerificationSnapshot!.Status);
    }

    [Fact]
    public async Task ChangingTheCredentialShapeStillInvalidatesItsVerification()
    {
        await using var db = CreateContext();
        var connectionId = await SeedAsync(db, DeclaredKey, DeclaredAuthMode, DeclaredProtocolMode);
        var repository = CreateRepository(db);

        var read = await repository.GetByIdAsync(connectionId);
        Assert.NotNull(read);

        Assert.True(
            await repository.UpdateAsync(
                connectionId,
                WriteRequestFrom(read) with { AuthMode = DeclaredApiKeyMode }));

        var stored = await ReadStoredAsync(db, connectionId);
        Assert.Equal(nameof(AiVerificationStatus.NeverVerified), stored.VerificationSnapshot!.Status);

        // And the new shape is stored as the family names it, because the stored one named a different shape.
        Assert.Equal(DeclaredApiKeyMode, stored.AuthMode);
    }

    // Nothing is stored to keep, so the family is named as it names itself now.
    [Fact]
    public async Task CreatingAConnectionStoresTheNamesTheFamilyDeclares()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db);

        var created = await repository.AddAsync(
            Guid.NewGuid(),
            new AiConnectionWriteRequestDto(
                "New Connection",
                "meisterdev/googleVertex",
                "https://europe-west4-aiplatform.googleapis.com",
                DeclaredAuthMode,
                AiDiscoveryMode.ManualOnly,
                [ChatModel([ProviderDeclaredProtocolModes.Auto, DeclaredProtocolMode])],
                [
                    new AiPurposeBindingDto(
                        Guid.Empty,
                        AiPurpose.ReviewDefault,
                        null,
                        "gemini-2.5-pro",
                        DeclaredProtocolMode)
                ]));

        var stored = await ReadStoredAsync(db, created.Id);
        Assert.Equal(DeclaredKey, stored.ProviderKind);
        Assert.Equal(DeclaredAuthMode, stored.AuthMode);

        // A wire shape no family owns persists unqualified, beside one the family declared.
        Assert.Equal(
            [ProviderDeclaredProtocolModes.Auto, DeclaredProtocolMode],
            stored.ConfiguredModels.Single().SupportedProtocolModes);
        Assert.Equal(DeclaredProtocolMode, stored.PurposeBindings.Single().ProtocolMode);
    }

    // A family qualifies the modes it declared and nothing else. A wire shape it does not serve keeps the
    // product's own member name, because a value qualified by a family that never declared that mode names
    // nothing and would read back as unresolvable.
    [Fact]
    public async Task AWireShapeTheFamilyDoesNotDeclareIsStoredAsSubmittedAndReported()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db);

        var created = await repository.AddAsync(
            Guid.NewGuid(),
            new AiConnectionWriteRequestDto(
                "New Connection",
                "meisterdev/googleVertex",
                "https://europe-west4-aiplatform.googleapis.com",
                DeclaredAuthMode,
                AiDiscoveryMode.ManualOnly,
                [ChatModel([ProviderDeclaredProtocolModes.Auto, UndeclaredProtocolMode])],
                [
                    new AiPurposeBindingDto(
                        Guid.Empty,
                        AiPurpose.ReviewDefault,
                        null,
                        "gemini-2.5-pro",
                        UndeclaredProtocolMode)
                ]));

        var stored = await ReadStoredAsync(db, created.Id);
        Assert.Equal(
            [ProviderDeclaredProtocolModes.Auto, UndeclaredProtocolMode],
            stored.ConfiguredModels.Single().SupportedProtocolModes);
        Assert.Equal(UndeclaredProtocolMode, stored.PurposeBindings.Single().ProtocolMode);

        // And it is reported rather than replaced: the set of wire shapes is what the loaded families declare,
        // so a shape none of them declares names nothing the host can resolve, and the operator is told which
        // value to correct.
        var read = await repository.GetByIdAsync(created.Id);
        Assert.NotNull(read);
        Assert.Equal(AiConnectionAvailabilityState.Unavailable, read.Availability.State);
        Assert.Equal(AiConnectionUnavailableReason.StoredValueUnresolved, read.Availability.Reason);
        Assert.Contains(
            read.Availability.UnresolvedValues,
            value => value.Value == UndeclaredProtocolMode);
    }

    private static AiConnectionWriteRequestDto WriteRequestFrom(AiConnectionDto read, string? displayName = null)
    {
        return new AiConnectionWriteRequestDto(
            displayName ?? read.DisplayName,
            read.ProviderKind,
            read.BaseUrl,
            read.AuthMode,
            read.DiscoveryMode,
            read.ConfiguredModels,
            read.PurposeBindings,
            read.DefaultHeaders,
            read.DefaultQueryParams);
    }

    private static AiConfiguredModelDto ChatModel(IReadOnlyList<string> protocolModes)
    {
        return new AiConfiguredModelDto(
            Guid.Empty,
            "gemini-2.5-pro",
            "Gemini 2.5 Pro",
            [AiOperationKind.Chat],
            protocolModes,
            null,
            null,
            null,
            true,
            true);
    }

    private static Task<AiConnectionProfileRecord> ReadStoredAsync(MeisterProPRDbContext db, Guid connectionId)
    {
        return db.AiConnectionProfiles
            .Include(profile => profile.ConfiguredModels)
            .Include(profile => profile.PurposeBindings)
            .Include(profile => profile.VerificationSnapshot)
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == connectionId);
    }

    private static async Task<Guid> SeedAsync(
        MeisterProPRDbContext db,
        string storedIdentity,
        string storedAuthMode,
        string storedProtocolMode)
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
                ProviderKind = storedIdentity,
                BaseUrl = "https://europe-west4-aiplatform.googleapis.com",
                AuthMode = storedAuthMode,
                DiscoveryMode = nameof(AiDiscoveryMode.ManualOnly),
                DefaultHeaders = [],
                DefaultQueryParams = [],
                CreatedAt = now,
                UpdatedAt = now,
                VerificationSnapshot = new AiVerificationSnapshotRecord
                {
                    ConnectionProfileId = connectionId,
                    Status = nameof(AiVerificationStatus.Verified),
                    CheckedAt = now,
                },
                ConfiguredModels =
                [
                    new AiConfiguredModelRecord
                    {
                        Id = modelId,
                        ConnectionProfileId = connectionId,
                        RemoteModelId = "gemini-2.5-pro",
                        DisplayName = "Gemini 2.5 Pro",
                        OperationKinds = [nameof(AiOperationKind.Chat)],
                        SupportedProtocolModes = [ProviderDeclaredProtocolModes.Auto, storedProtocolMode],
                        Source = nameof(AiConfiguredModelSource.Manual),
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
                        ProtocolMode = storedProtocolMode,
                        IsEnabled = true,
                        CreatedAt = now,
                        UpdatedAt = now,
                    },
                ],
            });

        await db.SaveChangesAsync();
        return connectionId;
    }

    // The declaration the family ships: its own key, the modes it owns, and the unqualified spellings each of
    // those supersedes.
    private static IAiProviderDriverRegistry DeclaringFamily()
    {
        var driver = DeclaringProviderFamilies.DriverFor(
            new ProviderDeclaration
            {
                Key = DeclaredKey,
                Label = "Google Vertex AI",
                Version = "1.0",
                ContractVersion = ProviderContract.Version,
                LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
                    ["GoogleVertex"],
                    [DeclaredApiKeyMode, DeclaredAuthMode],
                    [DeclaredProtocolMode]),
                AuthModes =
                [
                    new ProviderDeclaredAuthMode(DeclaredApiKeyMode, [AiCredentialFieldSupport.ApiKey]),
                    new ProviderDeclaredAuthMode(DeclaredAuthMode, [AiCredentialFieldSupport.ApiKey]),
                ],
                ProtocolModes = new ProviderDeclaredProtocolModes(
                [
                    ProviderDeclaredProtocolModes.Auto,
                    DeclaredProtocolMode,
                    ProviderDeclaredProtocolModes.Embeddings,
                ]),
                ConformanceInputs = new ProviderConformanceInputs(DeclaredApiKeyMode),
            });

        return DeclaringProviderFamilies.Declaring("meisterdev/googleVertex", driver);
    }

    private static AiConnectionRepository CreateRepository(MeisterProPRDbContext db)
    {
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TenantProviderPolicy.Unrestricted);
        policies.GetForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TenantProviderPolicy.Unrestricted);

        return new AiConnectionRepository(db, CreateCodec(), policies, DeclaringFamily(), EgressUrlPolicy.Locked);
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.AiConnectionDeclaredNamesTests.{Guid.NewGuid():N}");
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
