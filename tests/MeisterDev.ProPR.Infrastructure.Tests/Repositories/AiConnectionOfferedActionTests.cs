// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Which of a family's declared actions the connection read reports as offered, and what the family is told
///     about the connection when it decides.
/// </summary>
/// <remarks>
///     The declaration says which operations a family has; this says which of them apply to one connection. A
///     family whose credential is written by its own sign-in offers the sign-in before that has happened and the
///     disconnect after, and both come from the same declaration.
/// </remarks>
public sealed class AiConnectionOfferedActionTests
{
    private const string Family = "meisterdev/openAiCompatible";

    private const string ConnectAction = "connect";

    private const string DisconnectAction = "disconnect";

    // Every family that says nothing is offered everything it declares, which they all did before there
    // was anything to say.
    [Fact]
    public async Task AFamilyThatSaysNothingOffersEveryActionItDeclares()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(db, DeclaringProviderFamilies.Declaring(Family, WithActions()));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest());
        var reread = await repository.GetByIdAsync(saved.Id);

        Assert.Equal([ConnectAction, DisconnectAction], reread!.OfferedActionIds);
        Assert.Equal([ConnectAction, DisconnectAction], reread.DeclaredActions.Select(action => action.Id));
    }

    [Fact]
    public async Task AFamilyThatOffersASubsetReportsThatSubsetAndKeepsTheWholeDeclaration()
    {
        await using var db = CreateContext();
        var family = new DecidingFamily(_ => [DisconnectAction]);
        var repository = CreateRepository(db, DeclaringProviderFamilies.Declaring(Family, family));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest());
        var reread = await repository.GetByIdAsync(saved.Id);

        Assert.Equal([DisconnectAction], reread!.OfferedActionIds);
        Assert.Equal([ConnectAction, DisconnectAction], reread.DeclaredActions.Select(action => action.Id));
    }

    [Fact]
    public async Task AFamilyThatOffersNothingLeavesTheConnectionWithNoOfferedAction()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(
            db,
            DeclaringProviderFamilies.Declaring(Family, new DecidingFamily(_ => [])));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest());

        Assert.Empty((await repository.GetByIdAsync(saved.Id))!.OfferedActionIds);
    }

    // The console renders from the declaration, so an id that is not in it has no label, no inputs and nothing
    // to dispatch against.
    [Fact]
    public async Task AnIdTheFamilyNeverDeclaredIsDropped()
    {
        await using var db = CreateContext();
        var repository = CreateRepository(
            db,
            DeclaringProviderFamilies.Declaring(Family, new DecidingFamily(_ => [ConnectAction, "rotate"])));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest());

        Assert.Equal([ConnectAction], (await repository.GetByIdAsync(saved.Id))!.OfferedActionIds);
    }

    // The three facts the family decides from, read from the row this projection already loaded.
    [Fact]
    public async Task TheFamilyIsToldWhetherACredentialIsStored()
    {
        await using var db = CreateContext();
        var family = new DecidingFamily(state => state.HasStoredCredential ? [DisconnectAction] : [ConnectAction]);
        var repository = CreateRepository(db, DeclaringProviderFamilies.Declaring(Family, family));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest());
        Assert.Equal([ConnectAction], (await repository.GetByIdAsync(saved.Id))!.OfferedActionIds);
        Assert.False(family.Asked[^1].HasStoredCredential);

        await repository.UpdateAsync(saved.Id, WriteRequest(secret: "a-stored-grant"));

        Assert.Equal([DisconnectAction], (await repository.GetByIdAsync(saved.Id))!.OfferedActionIds);
        Assert.True(family.Asked[^1].HasStoredCredential);
    }

    [Fact]
    public async Task TheFamilyIsToldTheVerificationOutcomeAndTheResolvedCredentialHealth()
    {
        await using var db = CreateContext();
        var family = new DecidingFamily(_ => [ConnectAction]);
        var repository = CreateRepository(db, DeclaringProviderFamilies.Declaring(Family, family));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest(secret: "a-stored-grant"));
        await repository.SaveVerificationAsync(
            saved.Id,
            new AiVerificationResultDto(
                AiVerificationStatus.Failed,
                AiVerificationFailureCategory.Credentials,
                "The provider refused the grant.",
                null,
                DateTimeOffset.UtcNow));

        await repository.GetByIdAsync(saved.Id);

        var asked = family.Asked[^1];
        Assert.Equal(AiVerificationStatus.Failed, asked.VerificationStatus);

        // A failed verification says the credential did not work; needing re-authorization is the remedy an
        // operator can act on, and it is what the host resolves that into.
        Assert.Equal(AiCredentialHealth.NeedsReauthorization, asked.CredentialHealth);
    }

    // A family whose declaration says its connections have no credential health has none, which is the rule the
    // health store applies too: a key an operator typed has nothing to report.
    [Fact]
    public async Task AFamilyWithoutCredentialHealthIsToldNothingAboutIt()
    {
        await using var db = CreateContext();
        var family = new DecidingFamily(_ => [ConnectAction], hasCredentialHealth: false);
        var repository = CreateRepository(db, DeclaringProviderFamilies.Declaring(Family, family));

        var saved = await repository.AddAsync(Guid.NewGuid(), WriteRequest(secret: "a-typed-key"));
        await repository.SaveVerificationAsync(
            saved.Id,
            new AiVerificationResultDto(AiVerificationStatus.Verified, null, "Accepted.", null, DateTimeOffset.UtcNow));

        await repository.GetByIdAsync(saved.Id);

        Assert.Equal(AiVerificationStatus.Verified, family.Asked[^1].VerificationStatus);
        Assert.Equal(AiCredentialHealth.Unreported, family.Asked[^1].CredentialHealth);
    }

    private static ProviderDeclaration WithActions(bool hasCredentialHealth = true)
    {
        return DeclaringProviderFamilies.DeclarationWith(Family) with
        {
            HasCredentialHealth = hasCredentialHealth,
            Actions =
            [
                new ProviderDeclaredAction(ConnectAction, "Connect account", []),
                new ProviderDeclaredAction(DisconnectAction, "Disconnect account", []),
            ],
        };
    }

    private static AiConnectionWriteRequestDto WriteRequest(string? secret = null)
    {
        var chatModel = new AiConfiguredModelDto(
            Guid.Empty,
            "gpt-4o",
            "gpt-4o",
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto, Family + ":ChatCompletions"]);

        return new AiConnectionWriteRequestDto(
            "Offered",
            Family,
            "https://api.example.com/v1",
            Family + ":ApiKey",
            AiDiscoveryMode.ManualOnly,
            [chatModel],
            [new AiPurposeBindingDto(Guid.Empty, AiPurpose.ReviewDefault, null, "gpt-4o")],
            null,
            null,
            secret);
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
            $"MeisterDev.ProPR.AiConnectionOfferedActionTests.{Guid.NewGuid():N}");
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

    /// <summary>
    ///     A family that answers with whatever a test decided, and keeps what it was told about each connection.
    /// </summary>
    /// <remarks>
    ///     A substitute cannot stand in here: the member carries a default implementation, so what is under test
    ///     is a real type that either states an answer or leaves the default to answer for it.
    /// </remarks>
    /// <param name="offering">What this family offers for the connection it is told about.</param>
    /// <param name="hasCredentialHealth">Whether its connections have credential health at all.</param>
    private sealed class DecidingFamily(
        Func<ProviderConnectionState, IReadOnlyList<string>> offering,
        bool hasCredentialHealth = true) : IAiProviderDriver, IAiProviderActions
    {
        /// <summary>What the host told this family, in the order it was asked.</summary>
        public List<ProviderConnectionState> Asked { get; } = [];

        public ProviderDeclaration Declaration { get; } = WithActions(hasCredentialHealth);

        public IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields =>
            this.Declaration.CredentialFields;

        public IReadOnlyList<string> SupportedAuthModes => this.Declaration.SupportedAuthModes;

        public IReadOnlyList<string> SupportedProtocolModes => this.Declaration.ProtocolModes.Supported;

        public IReadOnlyList<string> OfferedActionIds(ProviderConnectionState connection)
        {
            this.Asked.Add(connection);

            return offering(connection);
        }

        public Task<ProviderActionResult> InvokeAsync(
            ProviderEndpoint endpoint,
            string actionId,
            IReadOnlyDictionary<string, string> inputs,
            IProviderActionContext context)
        {
            return Task.FromResult(ProviderActionResult.Completed("Done."));
        }

        public string? ValidateProbeTarget(AiProbeTarget target)
        {
            return null;
        }

        public Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
            ProviderEndpoint endpoint,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ProviderModelDiscoveryResult("succeeded", true, [], []));
        }

        public Task<ProviderVerificationResult> VerifyAsync(
            ProviderEndpoint endpoint,
            CancellationToken ct = default)
        {
            return Task.FromResult(DriverFailureMapper.Verified("Verified."));
        }

        public IChatClient CreateChatClient(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode)
        {
            throw new NotSupportedException("This family exists for its actions and serves no model.");
        }

        public ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode)
        {
            return ProviderRuntimeCapabilities.None;
        }

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
            ProviderEndpoint endpoint,
            ProviderModelDescriptor model,
            string protocolMode,
            int dimensions)
        {
            throw new NotSupportedException("This family exists for its actions and serves no model.");
        }
    }
}
