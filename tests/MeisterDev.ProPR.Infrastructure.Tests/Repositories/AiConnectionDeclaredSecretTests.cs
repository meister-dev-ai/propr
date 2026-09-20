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
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Where a secret-marked declared value is kept, and who may read it back.
/// </summary>
public sealed class AiConnectionDeclaredSecretTests
{
    private const string Family = "first/family";
    private const string OtherFamily = "second/family";

    [Fact]
    public async Task ASecretMarkedValueIsKeptInTheCredentialEnvelopeAndNotInTheSettingsDocument()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, Declaring("first/family"));

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(declaredSecrets: new Dictionary<string, string> { ["clientSecret"] = "s3cret-value" }));

        var stored = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == saved.Id);
        Assert.True(stored.ProviderSettings is null || !stored.ProviderSettings.ContainsKey("clientSecret"));
        Assert.NotNull(stored.ProtectedSecret);
    }

    // What is in the column is the Data-Protection-wrapped envelope, so reading the row without the host's
    // protection yields nothing usable.
    [Fact]
    public async Task TheStoredValueIsNotReadableWithoutTheHostsProtection()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, Declaring("first/family"));

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(declaredSecrets: new Dictionary<string, string> { ["clientSecret"] = "s3cret-value" }));

        var stored = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == saved.Id);
        Assert.DoesNotContain("s3cret-value", stored.ProtectedSecret!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretMarkedValueReadsBackToTheFamilyUnderItsDeclaredName()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, Declaring("first/family"));

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(declaredSecrets: new Dictionary<string, string> { ["clientSecret"] = "s3cret-value" }));

        var reread = await repository.GetByIdAsync(saved.Id);
        Assert.Equal("s3cret-value", reread!.ToProviderEndpoint().DeclaredValues["clientSecret"]);
    }

    // A field name is the declaring family's to choose, so two families are free to choose the same one. The
    // stored value names the family that wrote it, and a family reading a value another one stored gets nothing.
    // The row is moved onto the second family by hand, which a repoint leaves behind before the clearing
    // reaches the envelope.
    [Fact]
    public async Task TwoFamiliesDeclaringOneFieldNameDoNotReadEachOthersValue()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, BothFamilies());
        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(declaredSecrets: new Dictionary<string, string> { ["clientSecret"] = "first-value" }));

        var moved = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == saved.Id);
        moved.ProviderKind = OtherFamily;
        await db.SaveChangesAsync();

        var reread = await repository.GetByIdAsync(saved.Id);

        Assert.Empty(reread!.DeclaredSecrets);
    }

    // An edit that changes anything else leaves the secret boxes empty, and the stored value has to survive that,
    // which the credential boxes already do.
    [Fact]
    public async Task AnEditThatReEntersNothingKeepsTheStoredValue()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, Declaring("first/family"));

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(declaredSecrets: new Dictionary<string, string> { ["clientSecret"] = "s3cret-value" }));

        await repository.UpdateAsync(saved.Id, WriteRequest(displayName: "Renamed"));

        var reread = await repository.GetByIdAsync(saved.Id);
        Assert.Equal("s3cret-value", reread!.DeclaredSecrets["clientSecret"]);
    }

    // The credential and the declared secrets are two namespaces inside one envelope, so a family declaring a
    // secret does not change how its credential is read back.
    [Fact]
    public async Task ACredentialStoredBesideADeclaredSecretStillReadsBackAsTheCredential()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, Declaring("first/family"));

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(
                secret: "the-api-key",
                declaredSecrets: new Dictionary<string, string> { ["clientSecret"] = "s3cret-value" }));

        var reread = await repository.GetByIdAsync(saved.Id);
        Assert.Equal("the-api-key", reread!.Secret);
        Assert.Equal("s3cret-value", reread.DeclaredSecrets["clientSecret"]);
    }

    // Rows written before declared values existed carry a bare credential and no declared section, and they have
    // to keep reading back as the credential they are.
    [Fact]
    public async Task AnExistingStoredCredentialIsReadableUnchanged()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, Declaring("first/family"));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest(secret: "the-api-key"));
        var reread = await repository.GetByIdAsync(saved.Id);

        Assert.Equal("the-api-key", reread!.Secret);
        Assert.Empty(reread.DeclaredSecrets);
    }

    // Nothing the host returns to a caller carries the plain value. The connection read reports which fields hold
    // one so a console can say "set" and offer to replace it.
    [Fact]
    public async Task TheConnectionReadReportsThatASecretIsSetWithoutReturningIt()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, Declaring("first/family"));

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(declaredSecrets: new Dictionary<string, string> { ["clientSecret"] = "s3cret-value" }));

        var reread = await repository.GetByIdAsync(saved.Id);
        Assert.Equal(["clientSecret"], reread!.DeclaredSecretNames);
        Assert.DoesNotContain("s3cret-value", reread.ToString(), StringComparison.Ordinal);
    }

    private static IAiProviderDriverRegistry Declaring(string key)
    {
        return DeclaringProviderFamilies.Declaring(key, SecretDeclaring(key));
    }

    // Both families declare the same secret-marked field name, and that makes reading one family's value
    // back under another family's declaration something the envelope has to refuse rather than something the
    // field names already prevent.
    private static IAiProviderDriverRegistry BothFamilies()
    {
        return new AiProviderRegistry(
        [
            DeclaringProviderFamilies.DriverFor(SecretDeclaring(Family)),
            DeclaringProviderFamilies.DriverFor(SecretDeclaring(OtherFamily)),
        ]);
    }

    private static ProviderDeclaration SecretDeclaring(string key)
    {
        return DeclaringProviderFamilies.DeclarationWith(
            key,
            new ProviderDeclaredField("clientSecret", "Client secret", ProviderFieldKind.Secret));
    }

    private static AiConnectionWriteRequestDto WriteRequest(
        string displayName = "Declared",
        string? secret = null,
        IReadOnlyDictionary<string, string>? declaredSecrets = null)
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
            secret is null ? null : ProviderSecretEnvelope.ForApiKey(Family + ":ApiKey", secret).Encode(),
            null,
            declaredSecrets);
    }

    private static AiConnectionRepository CreateRepository(
        MeisterProPRDbContext db,
        IAiProviderDriverRegistry providerDrivers,
        ISecretProtectionCodec? codec = null)
    {
        var policies = Substitute.For<ITenantProviderPolicyProvider>();
        policies.GetForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TenantProviderPolicy.Unrestricted);
        policies.GetForTenantAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(TenantProviderPolicy.Unrestricted);

        return new AiConnectionRepository(db, codec ?? CreateCodec(), policies, providerDrivers, EgressUrlPolicy.Locked);
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.AiConnectionDeclaredSecretTests.{Guid.NewGuid():N}");
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
