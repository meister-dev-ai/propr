// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     What the connection profile does with the non-secret configuration values a provider family declares.
/// </summary>
public sealed class AiConnectionDeclaredSettingsTests
{
    private const string Family = "meisterdev/openAiCompatible";

    [Fact]
    public async Task AConnectionWithNoDeclaredValuesStoresNoDocument()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, DeclaringProviderFamilies.Declaring(Family, UrlField()));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest());

        var stored = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == saved.Id);
        Assert.Null(stored.ProviderSettings);
    }

    // Every shape the closed field vocabulary can produce reaches the column as the operator entered it. A value
    // coerced on the way in or out is a value the family reads back as something else than what was configured,
    // and an empty one has to survive as a value rather than being read as absent.
    [Fact]
    public async Task DeclaredValuesRoundTripUnchanged()
    {
        var entered = new Dictionary<string, string>
        {
            ["endpoint"] = "https://api.example.com/v1",
            ["region"] = string.Empty,
            ["useCache"] = "true",
            ["port"] = "1455",
            ["headers"] = "a\nb",
        };

        await using var db = CreateContext();
        var repository = CreateRepository(
            db,
            DeclaringProviderFamilies.Declaring(
                Family,
                UrlField(),
                new ProviderDeclaredField("region", "Region", ProviderFieldKind.String),
                new ProviderDeclaredField("useCache", "Use cache", ProviderFieldKind.Bool),
                new ProviderDeclaredField("port", "Port", ProviderFieldKind.Int),
                new ProviderDeclaredField("headers", "Headers", ProviderFieldKind.StringList)));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest(providerSettings: entered));
        var reread = await repository.GetByIdAsync(saved.Id);

        Assert.Equal(entered, reread!.ProviderSettings);
    }

    // A family version that drops a field, or is rolled back to one that never had it, must not take the
    // operator's value with it. The host reads back what the family declares today and leaves the rest alone.
    [Fact]
    public async Task AValueForAFieldTheFamilyNoLongerDeclaresStaysStoredAndIsNotReturned()
    {
        await using var db = CreateContext();
        var withBothFields = CreateRepository(
            db,
            DeclaringProviderFamilies.Declaring(
                Family,
                UrlField(),
                new ProviderDeclaredField("retired", "Retired", ProviderFieldKind.String)));

        var saved = await withBothFields.AddAsync(
            Guid.NewGuid(),
            WriteRequest(
                providerSettings: new Dictionary<string, string>
                {
                    ["endpoint"] = "https://api.example.com/v1",
                    ["retired"] = "kept",
                }));

        var withOneField = CreateRepository(db, DeclaringProviderFamilies.Declaring(Family, UrlField()));
        await withOneField.UpdateAsync(
            saved.Id,
            WriteRequest(providerSettings: new Dictionary<string, string> { ["endpoint"] = "https://api.example.com/v2" }));

        var reread = await withOneField.GetByIdAsync(saved.Id);
        Assert.Equal(new Dictionary<string, string> { ["endpoint"] = "https://api.example.com/v2" }, reread!.ProviderSettings);

        var stored = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == saved.Id);
        Assert.Equal("kept", stored.ProviderSettings!["retired"]);
    }

    // A field the form did not submit — one hidden by its visibility condition, for instance — keeps what was
    // stored. Treating an absent name as a cleared value would lose the entry the moment another field hid it.
    [Fact]
    public async Task AnUpdateThatOmitsAFieldKeepsItsStoredValue()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(
            db,
            DeclaringProviderFamilies.Declaring(
                Family,
                UrlField(),
                new ProviderDeclaredField("region", "Region", ProviderFieldKind.String)));

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(
                providerSettings: new Dictionary<string, string>
                {
                    ["endpoint"] = "https://api.example.com/v1",
                    ["region"] = "eu-central-1",
                }));

        await repository.UpdateAsync(
            saved.Id,
            WriteRequest(providerSettings: new Dictionary<string, string> { ["endpoint"] = "https://api.example.com/v2" }));

        var reread = await repository.GetByIdAsync(saved.Id);
        Assert.Equal("eu-central-1", reread!.ProviderSettings!["region"]);
    }

    // The declared default is what an unset connection uses, applied where it is read rather than written once at
    // creation, so a family that changes its default changes what those connections use.
    [Fact]
    public async Task ADeclaredDefaultStandsInWhereNothingIsStored()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(
            db,
            DeclaringProviderFamilies.Declaring(
                Family,
                new ProviderDeclaredField("port", "Port", ProviderFieldKind.Int) { DefaultValue = "1455" }));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest());
        var reread = await repository.GetByIdAsync(saved.Id);

        Assert.Equal("1455", reread!.ProviderSettings!["port"]);
        var stored = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == saved.Id);
        Assert.Null(stored.ProviderSettings);
    }

    // A connection whose family this build cannot name is still listed so an operator can repoint it. Nothing
    // declares its fields, so it reports no declared configuration rather than failing to project.
    [Fact]
    public async Task AConnectionWhoseFamilyIsAbsentReportsNoDeclaredConfiguration()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, DeclaringProviderFamilies.None());

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(providerSettings: new Dictionary<string, string> { ["endpoint"] = "https://api.example.com/v1" }));

        var reread = await repository.GetByIdAsync(saved.Id);
        Assert.Null(reread!.ProviderSettings);
    }

    // The same egress rules the request path applies are applied again where the value is stored, so a caller
    // that reaches the store by another route cannot write an address the installation refuses.
    [Fact]
    public async Task AnAddressTheInstallationRefusesIsRefusedAtTheStore()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, DeclaringProviderFamilies.Declaring(Family, UrlField()));

        var refusal = await Assert.ThrowsAsync<ProviderDeclaredValueRefusedException>(() => repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(providerSettings: new Dictionary<string, string> { ["endpoint"] = "http://169.254.169.254/latest" })));

        Assert.Equal("endpoint", refusal.FieldName);
        Assert.Empty(await db.AiConnectionProfiles.ToListAsync());
    }

    // An update refused at the store leaves the stored profile as it was, rather than half rewritten.
    [Fact]
    public async Task AnUpdateRefusedAtTheStoreChangesNothing()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, DeclaringProviderFamilies.Declaring(Family, UrlField()));

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(providerSettings: new Dictionary<string, string> { ["endpoint"] = "https://api.example.com/v1" }));

        await Assert.ThrowsAsync<ProviderDeclaredValueRefusedException>(() => repository.UpdateAsync(
            saved.Id,
            WriteRequest(
                displayName: "Renamed",
                providerSettings: new Dictionary<string, string> { ["endpoint"] = "http://10.0.0.5/v1" })));

        var stored = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == saved.Id);
        Assert.Equal("https://api.example.com/v1", stored.ProviderSettings!["endpoint"]);
        Assert.Equal("Declared", stored.DisplayName);
    }

    // A field a family declared as free text is not an address to the host, so nothing about it is checked as one.
    [Fact]
    public async Task AValueInAFieldDeclaredAsPlainTextIsStoredWithoutAnAddressCheck()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(
            db,
            DeclaringProviderFamilies.Declaring(
                Family,
                new ProviderDeclaredField("endpoint", "Endpoint", ProviderFieldKind.String)));

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(providerSettings: new Dictionary<string, string> { ["endpoint"] = "http://169.254.169.254/latest" }));

        var reread = await repository.GetByIdAsync(saved.Id);
        Assert.Equal("http://169.254.169.254/latest", reread!.ProviderSettings!["endpoint"]);
    }

    private static ProviderDeclaredField UrlField()
    {
        return new ProviderDeclaredField("endpoint", "Endpoint", ProviderFieldKind.Url);
    }

    private static AiConnectionWriteRequestDto WriteRequest(
        IReadOnlyDictionary<string, string>? providerSettings = null,
        string displayName = "Declared")
    {
        var chatModel = new AiConfiguredModelDto(
            Guid.Empty,
            "gpt-4o",
            "gpt-4o",
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto, Family + ":ChatCompletions"]);

        return new AiConnectionWriteRequestDto(
            displayName,
            Family,
            "https://api.example.com/v1",
            Family + ":ApiKey",
            AiDiscoveryMode.ManualOnly,
            [chatModel],
            [new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewDefault, null, "gpt-4o")],
            null,
            null,
            null,
            providerSettings);
    }

    private static AiConnectionRepository CreateRepository(
        MeisterProPRDbContext db,
        IAiProviderDriverRegistry providerDrivers)
    {
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TenantProviderPolicy.Unrestricted);
        policies.GetForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TenantProviderPolicy.Unrestricted);

        return new AiConnectionRepository(db, CreateCodec(), policies, providerDrivers, EgressUrlPolicy.Locked);
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.AiConnectionDeclaredSettingsTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        return new SecretProtectionCodec(services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>());
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MeisterProPRDbContext(options);
    }
}
