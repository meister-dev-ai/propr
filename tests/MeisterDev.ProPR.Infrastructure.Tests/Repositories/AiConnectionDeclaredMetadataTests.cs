// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     What the connection read reports about the fields a provider family declares, and what it computes for the
///     read-only ones.
/// </summary>
public sealed class AiConnectionDeclaredMetadataTests
{
    private const string Family = "meisterdev/openAiCompatible";

    [Fact]
    public async Task TheConnectionReadCarriesTheFamilysDeclaredFields()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(
            db,
            DeclaringProviderFamilies.Declaring(
                Family,
                new ProviderDeclaredField("endpoint", "Endpoint", ProviderFieldKind.Url)
                {
                    IsRequired = true,
                    Hint = "Where this provider is reached.",
                    Placeholder = "https://api.example.com/v1",
                },
                new ProviderDeclaredField("mode", "Account type", ProviderFieldKind.Choice)
                {
                    Choices = ["apiKey", "subscription"],
                    DefaultValue = "apiKey",
                },
                new ProviderDeclaredField("clientSecret", "Client secret", ProviderFieldKind.Secret)
                {
                    VisibleWhen = new ProviderFieldVisibility("mode", "subscription"),
                }));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest());
        var reread = await repository.GetByIdAsync(saved.Id);

        var declared = reread!.DeclaredFields;
        Assert.Equal(["endpoint", "mode", "clientSecret"], declared.Select(field => field.Name));

        var endpoint = declared[0];
        Assert.Equal("Endpoint", endpoint.Label);
        Assert.Equal(ProviderFieldKind.Url, endpoint.Kind);
        Assert.True(endpoint.IsRequired);
        Assert.Equal("Where this provider is reached.", endpoint.Hint);
        Assert.Equal("https://api.example.com/v1", endpoint.Placeholder);

        Assert.Equal(["apiKey", "subscription"], declared[1].Choices!);
        Assert.Equal("apiKey", declared[1].DefaultValue);

        Assert.True(declared[2].IsSecret);
        Assert.Equal("mode", declared[2].VisibleWhen!.FieldName);
        Assert.Equal("subscription", declared[2].VisibleWhen!.EqualsValue);
    }

    [Fact]
    public async Task AFamilyWithNoDeclaredFieldsReportsAnEmptySet()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, DeclaringProviderFamilies.Declaring(Family));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest());
        var reread = await repository.GetByIdAsync(saved.Id);

        Assert.Empty(reread!.DeclaredFields);
        Assert.Empty(reread.ComputedFields);
    }

    // Every string in the metadata was written by the family, so a declaration that carries a whole document
    // where a label belongs is cut short rather than rendered whole.
    [Fact]
    public void ALabelLongerThanTheHostRendersIsCapped()
    {
        var declaration = DeclaringProviderFamilies.DeclarationWith(
            Family,
            new ProviderDeclaredField("endpoint", new string('x', 5000), ProviderFieldKind.Url));

        var described = ProviderDeclaredFieldProjection.Describe(declaration);

        Assert.Equal(ProviderHostLimits.MaximumFieldTextLength, Assert.Single(described).Label.Length);
    }

    // A family that echoes a credential back through a field description does not get to publish it.
    [Fact]
    public void AHintCarryingACredentialTheHostHoldsIsScrubbed()
    {
        var declaration = DeclaringProviderFamilies.DeclarationWith(
            Family,
            new ProviderDeclaredField("endpoint", "Endpoint", ProviderFieldKind.Url)
            {
                Hint = "Use the key sk-live-0123456789.",
            });

        var described = ProviderDeclaredFieldProjection.Describe(declaration, ["sk-live-0123456789"]);

        Assert.DoesNotContain("sk-live-0123456789", Assert.Single(described).Hint!, StringComparison.Ordinal);
    }

    // A field's visibility condition is two family-authored strings like every other one, and a family is free to
    // name a secret field as the one that decides. The value it compares against then holds the credential.
    [Fact]
    public void AVisibilityConditionCarryingACredentialTheHostHoldsIsScrubbedAndCapped()
    {
        var declaration = DeclaringProviderFamilies.DeclarationWith(
            Family,
            new ProviderDeclaredField("clientSecret", "Client secret", ProviderFieldKind.Secret)
            {
                VisibleWhen = new ProviderFieldVisibility(new string('x', 5000), "sk-live-0123456789"),
            });

        var visibility = Assert.Single(ProviderDeclaredFieldProjection.Describe(declaration, ["sk-live-0123456789"]))
            .VisibleWhen;

        Assert.NotNull(visibility);
        Assert.DoesNotContain("sk-live-0123456789", visibility.EqualsValue, StringComparison.Ordinal);
        Assert.Equal(ProviderHostLimits.MaximumFieldTextLength, visibility.FieldName.Length);
    }

    // A computed value is derived for the read it is shown on, so changing the field it derives from changes it
    // without anything having to refresh a stored copy — there is none.
    [Fact]
    public async Task AComputedValueFollowsTheFieldItIsComposedFromAndIsNeverStored()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, DeclaringRedirect());

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(providerSettings: new Dictionary<string, string> { ["port"] = "1455" }));

        var first = await repository.GetByIdAsync(saved.Id);
        Assert.Equal("https://callback.example.com:1455/auth", Assert.Single(first!.ComputedFields).Value);

        await repository.UpdateAsync(
            saved.Id,
            WriteRequest(providerSettings: new Dictionary<string, string> { ["port"] = "1456" }));

        var second = await repository.GetByIdAsync(saved.Id);
        Assert.Equal("https://callback.example.com:1456/auth", Assert.Single(second!.ComputedFields).Value);

        var stored = await db.AiConnectionProfiles.SingleAsync(profile => profile.Id == saved.Id);
        Assert.DoesNotContain("redirectUri", stored.ProviderSettings!.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("redirectUri", second.ProviderSettings!.Keys, StringComparer.Ordinal);
    }

    // A computed address the installation would refuse is reported with its reason, and the other fields of the
    // form still arrive, because an operator who cannot see the form cannot fix what is wrong with it.
    [Fact]
    public async Task AComputedAddressTheInstallationRefusesIsReportedAtRenderWithoutBlockingTheRest()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, DeclaringRedirect(scheme: "http"));

        var saved = await repository.AddAsync(
            Guid.NewGuid(),
            WriteRequest(providerSettings: new Dictionary<string, string> { ["port"] = "1455" }));

        var reread = await repository.GetByIdAsync(saved.Id);

        var computed = Assert.Single(reread!.ComputedFields);
        Assert.Equal("http://callback.example.com:1455/auth", computed.Value);
        Assert.Contains("must use https", computed.Refusal!, StringComparison.Ordinal);
        Assert.Equal(["port", "redirectUri"], reread.DeclaredFields.Select(field => field.Name));
    }

    private static IAiProviderDriverRegistry DeclaringRedirect(string scheme = "https")
    {
        var declaration = DeclaringProviderFamilies.DeclarationWith(
            Family,
            new ProviderDeclaredField("port", "Port", ProviderFieldKind.Int) { DefaultValue = "1455" },
            new ProviderDeclaredField("redirectUri", "Redirect URI", ProviderFieldKind.Url) { IsComputed = true });

        var driver = DeclaringProviderFamilies.DriverFor(declaration);
        driver.ComputeDeclaredValues(Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(call => new Dictionary<string, string>
            {
                ["redirectUri"] = $"{scheme}://callback.example.com:{call.Arg<IReadOnlyDictionary<string, string>>()["port"]}/auth",
            });

        return DeclaringProviderFamilies.Declaring(Family, driver);
    }

    private static AiConnectionWriteRequestDto WriteRequest(IReadOnlyDictionary<string, string>? providerSettings = null)
    {
        var chatModel = new AiConfiguredModelDto(
            Guid.Empty,
            "gpt-4o",
            "gpt-4o",
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto, Family + ":ChatCompletions"]);

        return new AiConnectionWriteRequestDto(
            "Declared",
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
            $"MeisterDev.ProPR.AiConnectionDeclaredMetadataTests.{Guid.NewGuid():N}");
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
