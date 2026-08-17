// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Api.Validators;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Mentions.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     Who may see and change which client's mention configurations, and what the endpoint refuses.
///     Claiming a repository another client already claims is deliberately allowed: a client administrator
///     cannot see the other client, so refusing would be an error they could neither understand nor resolve.
/// </summary>
public sealed class AdminMentionConfigsControllerTests
{
    private static readonly Guid OwnedClient = Guid.Parse("aaaaaaaa-1111-4111-8111-111111111111");
    private static readonly Guid OtherClient = Guid.Parse("bbbbbbbb-2222-4222-8222-222222222222");

    [Fact]
    public async Task List_ClientAdministratorOfThatClient_ReadsIt()
    {
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.List(OwnedClient, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        await repo.Received(1).GetByClientAsync(OwnedClient, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_ClientAdministratorOfAnotherClient_IsRefused()
    {
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OtherClient));

        var result = await controller.List(OwnedClient, CancellationToken.None);

        Assert.IsNotType<OkObjectResult>(result);
        await repo.DidNotReceiveWithAnyArgs().GetByClientAsync(default, default);
    }

    [Fact]
    public async Task List_WithoutAClientFilter_DoesNotReturnWhatTheFilteredCallWouldRefuse()
    {
        // Asking broadly must not be a way around the role the narrow question enforces. The caller really
        // is assigned to the client and the repository really would return a configuration for it, so the
        // only thing that can keep the listing empty is the role check.
        var repo = CreateRepo();
        repo.GetByClientAsync(OwnedClient, Arg.Any<CancellationToken>()).Returns([Config()]);
        var controller = CreateController(
            repo,
            users: UserAssignedTo(OwnedClient),
            clientRoles: ReaderOf(OwnedClient));

        var result = await controller.List(null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Assert.IsType<List<MentionConfigResponse>>(ok.Value));
    }

    [Fact]
    public async Task List_WithoutAClientFilter_ReturnsClientsTheCallerAdministers()
    {
        // The mirror of the test above: with the administrator role the same assignment does come back, so
        // an empty result there is the role check and not the plumbing.
        var repo = CreateRepo();
        repo.GetByClientAsync(OwnedClient, Arg.Any<CancellationToken>()).Returns([Config()]);
        var controller = CreateController(
            repo,
            users: UserAssignedTo(OwnedClient),
            clientRoles: AdminOf(OwnedClient));

        var result = await controller.List(null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Single(Assert.IsType<List<MentionConfigResponse>>(ok.Value));
    }

    [Fact]
    public async Task List_AsSystemAdministrator_IncludesPausedConfigurations()
    {
        // A paused configuration no listing shows cannot be reactivated, and the uniqueness rule refuses
        // to replace it.
        var repo = CreateRepo();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([Config(isActive: false)]);
        var controller = CreateController(repo, isAdmin: true);

        var result = await controller.List(null, CancellationToken.None);

        // Asserting the paused configuration reached the response, not merely which repository call was
        // made: reading through the active-only method would satisfy the call check and still hide it.
        var listed = Assert.IsType<List<MentionConfigResponse>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(Assert.Single(listed).IsActive);
        await repo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
        await repo.DidNotReceive().GetAllActiveAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     An installation not entitled to answer mentions accepts no configuration for it. The worker holds
    ///     the same capability before it scans, which is what stops answers being posted, but the worker is not
    ///     a boundary a request passes: without this a configuration can be stored, and read back, on an
    ///     installation that will never act on it.
    /// </summary>
    [Fact]
    public async Task Create_WhenTheInstallationCannotAnswerMentions_IsRefusedBeforeAnythingIsStored()
    {
        var repo = CreateRepo();
        var controller = CreateController(
            repo,
            clientRoles: AdminOf(OwnedClient),
            licensing: UnavailableMentionAnswering());

        var result = await controller.Create(
            Request(),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsType<PremiumFeatureUnavailableResult>(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    /// <summary>Reading is held the same way, so the tab renders the refusal rather than an empty list.</summary>
    [Fact]
    public async Task List_WhenTheInstallationCannotAnswerMentions_IsRefused()
    {
        var controller = CreateController(
            CreateRepo(),
            clientRoles: AdminOf(OwnedClient),
            licensing: UnavailableMentionAnswering());

        var result = await controller.List(OwnedClient, CancellationToken.None);

        Assert.IsType<PremiumFeatureUnavailableResult>(result);
    }

    private static ILicensingCapabilityService UnavailableMentionAnswering()
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.GetCapabilityAsync(PremiumCapabilityKey.MentionAnswering, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new CapabilitySnapshot(
                        PremiumCapabilityKey.MentionAnswering,
                        "Mention answering",
                        true,
                        true,
                        PremiumCapabilityOverrideState.Disabled,
                        false,
                        "Mention answering is currently disabled for this installation.")));

        return licensing;
    }

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("http://127.0.0.1")]
    [InlineData("file:///etc")]
    public async Task Create_NamingAnOrganizationTheClientHasNotConfigured_IsRefused(string scopePath)
    {
        // An unconfigured organization has no credential behind it, and the runtime answers an absent
        // credential with the platform's own identity. Storing one would aim a scan carrying that identity
        // at whatever host was typed.
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Create(
            Request(scopePath: scopePath),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    [Fact]
    public async Task Create_NamingADisabledOrganization_IsRefused()
    {
        // Disabling an organization is how an operator withdraws it. It must not remain nameable here.
        var repo = CreateRepo();
        var scopes = Substitute.For<IClientAdoOrganizationScopeRepository>();
        scopes.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientAdoOrganizationScopeDto(
                    Guid.NewGuid(),
                    OwnedClient,
                    "https://dev.azure.com/org",
                    "org",
                    false,
                    AdoOrganizationVerificationStatus.Verified,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient), organizationScopes: scopes);

        var result = await controller.Create(
            Request(),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    /// <summary>
    ///     One rule, proven for each provider family. The check it replaced ran only for Azure DevOps, so
    ///     naming any other provider stored whatever URL was sent, and a scan later offered the platform's own
    ///     identity to it.
    /// </summary>
    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "https://dev.azure.com/somebody-else")]
    [InlineData(ScmProvider.GitHub, "https://github.enterprise.invalid")]
    [InlineData(ScmProvider.GitLab, "https://gitlab.invalid")]
    [InlineData(ScmProvider.Forgejo, "https://forgejo.invalid")]
    public async Task Create_NamingAScopePathTheClientHasNoConnectionFor_IsRefused(
        ScmProvider provider,
        string scopePath)
    {
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Create(
            Request(scopePath: scopePath, provider: provider),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, "https://github.com")]
    [InlineData(ScmProvider.GitLab, "https://gitlab.com")]
    [InlineData(ScmProvider.Forgejo, "https://codeberg.org")]
    public async Task Create_NamingAConnectionTheClientHolds_IsAccepted(ScmProvider provider, string scopePath)
    {
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Create(
            Request(scopePath: scopePath, provider: provider),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
    }

    /// <summary>A trailing separator and a difference in case are the same endpoint to every provider.</summary>
    [Theory]
    [InlineData("https://github.com/")]
    [InlineData("https://GitHub.com")]
    public async Task Create_NamingAConnectionSpelledDifferently_IsAccepted(string scopePath)
    {
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Create(
            Request(scopePath: scopePath, provider: ScmProvider.GitHub),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);
    }

    [Fact]
    public async Task Create_NamingADeactivatedConnection_IsRefused()
    {
        var repo = CreateRepo();
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([Connection(ScmProvider.GitHub, "https://github.com", isActive: false)]);
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient), connections: connections);

        var result = await controller.Create(
            Request(scopePath: "https://github.com", provider: ScmProvider.GitHub),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    /// <summary>
    ///     Two providers can sit at one host. The provider named in the request is what decides which
    ///     connection counts, so naming one and being scanned through the other is not possible.
    /// </summary>
    [Fact]
    public async Task Create_NamingAHostTheClientHoldsForAnotherProvider_IsRefused()
    {
        var repo = CreateRepo();
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([Connection(ScmProvider.Forgejo, "https://git.example.com")]);
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient), connections: connections);

        var result = await controller.Create(
            Request(scopePath: "https://git.example.com", provider: ScmProvider.GitHub),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    [Fact]
    public async Task Create_ForAProviderThisDeploymentCannotDiscover_IsRefusedSayingSo()
    {
        var repo = CreateRepo();
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.SupportsActivePullRequestDiscovery(Arg.Any<ScmProvider>()).Returns(false);
        registry.SupportsReviewThreadReply(Arg.Any<ScmProvider>()).Returns(true);
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient), providerRegistry: registry);

        var result = await controller.Create(
            Request(scopePath: "https://github.com", provider: ScmProvider.GitHub),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        var refusal = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(
            "discover pull requests",
            refusal.Value?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    /// <summary>
    ///     Answering needs both halves. A provider that can be read but offers no way to reply inside a review
    ///     conversation would take a configuration, scan on it, and answer nothing.
    /// </summary>
    [Fact]
    public async Task Create_ForAProviderThatCannotReplyInAConversation_IsRefusedSayingSo()
    {
        var repo = CreateRepo();
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.SupportsActivePullRequestDiscovery(Arg.Any<ScmProvider>()).Returns(true);
        registry.SupportsReviewThreadReply(Arg.Any<ScmProvider>()).Returns(false);
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient), providerRegistry: registry);

        var result = await controller.Create(
            Request(scopePath: "https://codeberg.org", provider: ScmProvider.Forgejo),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        var refusal = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(
            "reply",
            refusal.Value?.ToString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task Create_WithAWhitespaceScopePath_IsRefused(string scopePath)
    {
        // A blank scope path stored is a configuration that can never scan: it throws building a
        // ProviderHostRef, inside the scan, where nobody is told.
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Create(
            Request(scopePath: scopePath),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        AssertRefusedAsInvalid(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    [Fact]
    public async Task Create_WithANullRepositoryEntry_IsRefused()
    {
        // A JSON array carries a null element whatever its declared element type says, and FluentValidation
        // skips a null child rather than rejecting it, so without an explicit rule the null reaches the
        // controller and is dereferenced there.
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Create(
            Request(repoFilters: [null!]),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        AssertRefusedAsInvalid(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    [Theory]
    [InlineData(513, 0, 0, 0)]
    [InlineData(0, 257, 0, 0)]
    [InlineData(0, 0, 513, 0)]
    [InlineData(0, 0, 0, 65)]
    public async Task Create_WithAnOverlongRepositoryField_IsRefused(
        int repositoryIdLength,
        int displayNameLength,
        int canonicalRefLength,
        int providerLength)
    {
        // Each of these columns is bounded in the database. Refusing here is what turns an oversized entry
        // into a message the operator can act on rather than a write that fails underneath them.
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var filter = new MentionRepoFilterRequest(
            repositoryIdLength > 0 ? new string('r', repositoryIdLength) : "repo-guid",
            displayNameLength > 0 ? new string('d', displayNameLength) : null,
            canonicalRefLength > 0 ? new string('c', canonicalRefLength) : null,
            providerLength > 0 ? new string('p', providerLength) : null);

        var result = await controller.Create(
            Request(repoFilters: [filter]),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        AssertRefusedAsInvalid(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    [Fact]
    public async Task Create_NamingNoRepository_IsRefused()
    {
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Create(
            Request(repoFilters: []),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        AssertRefusedAsInvalid(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    [Fact]
    public async Task Create_WithAScopePathThatIsNotAUrl_IsRefused()
    {
        // The scan builds a provider host reference from this and throws where nobody sees it, so a
        // configuration saved with a bad value would look exactly like one that answers nothing.
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Create(
            Request(scopePath: "myorg"),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        AssertRefusedAsInvalid(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    [Fact]
    public async Task Create_ClaimingARepositoryAnotherClientAlreadyClaims_Succeeds()
    {
        // The other client is invisible here on purpose: naming it would disclose another tenant's setup,
        // and refusing would be unactionable. One answer per question is kept when the mention is answered.
        //
        // The other client's claim is real, and on the same repository id this request names. Only the
        // asking client's own configurations are consulted, which is what makes the overlap invisible.
        var repo = CreateRepo();
        repo.GetByClientAsync(OtherClient, Arg.Any<CancellationToken>())
            .Returns([Config(OtherClient)]);
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Create(
            Request(),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result);

        // What makes the overlap invisible is that the other client is never consulted. Asserting only that
        // creation succeeded would pass just as well with no other claim in existence.
        await repo.DidNotReceive().GetByClientAsync(OtherClient, Arg.Any<CancellationToken>());
        await repo.DidNotReceiveWithAnyArgs().GetAllAsync(default);
    }

    [Fact]
    public async Task Create_ForAClientTheCallerDoesNotAdminister_IsRefused()
    {
        var repo = CreateRepo();
        var controller = CreateController(repo, clientRoles: AdminOf(OtherClient));

        var result = await controller.Create(
            Request(),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsNotType<CreatedAtActionResult>(result);
        await repo.DidNotReceiveWithAnyArgs().AddAsync(default, default, default!, default!, default, default!);
    }

    [Fact]
    public async Task Create_WhenTheClientAlreadyAnswersInThatProject_ReturnsConflict()
    {
        var repo = CreateRepo();
        repo.GetByClientAsync(OwnedClient, Arg.Any<CancellationToken>())
            .Returns([Config()]);
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Create(
            Request(),
            new CreateMentionConfigRequestValidator(),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Patch_SendingAnEmptyRepositoryList_IsRefused()
    {
        var repo = CreateRepo();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Config());
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Patch(
            Config().Id,
            new PatchMentionConfigRequest(RepoFilters: []),
            new PatchMentionConfigRequestValidator(),
            CancellationToken.None);

        AssertRefusedAsInvalid(result);
        await repo.DidNotReceiveWithAnyArgs().UpdateAsync(default, default, default, default, default);
    }

    [Fact]
    public async Task Delete_ForAnotherClientsConfiguration_IsRefused()
    {
        var repo = CreateRepo();
        repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Config(OtherClient));
        var controller = CreateController(repo, clientRoles: AdminOf(OwnedClient));

        var result = await controller.Delete(Config().Id, CancellationToken.None);

        Assert.IsNotType<NoContentResult>(result);
        await repo.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
    }

    private static IMentionConfigurationRepository CreateRepo()
    {
        var repo = Substitute.For<IMentionConfigurationRepository>();
        repo.GetByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns([]);
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns([]);
        repo.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([]);
        repo.AddAsync(
                Arg.Any<Guid>(),
                Arg.Any<ScmProvider>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<IReadOnlyList<MentionRepoFilterDto>>(),
                Arg.Any<CancellationToken>())
            .Returns(Config());
        return repo;
    }

    private static MentionConfigurationDto Config(Guid? clientId = null, bool isActive = true)
    {
        return new MentionConfigurationDto(
            Guid.Parse("cccccccc-3333-4333-8333-333333333333"),
            clientId ?? OwnedClient,
            ScmProvider.AzureDevOps,
            "https://dev.azure.com/org",
            "proj",
            60,
            isActive,
            DateTimeOffset.UtcNow,
            [new MentionRepoFilterDto(Guid.NewGuid(), "repo-guid", ClaimedAt: DateTimeOffset.UtcNow)]);
    }

    private static CreateMentionConfigRequest Request(
        string scopePath = "https://dev.azure.com/org",
        IReadOnlyList<MentionRepoFilterRequest>? repoFilters = null,
        ScmProvider provider = ScmProvider.AzureDevOps)
    {
        return new CreateMentionConfigRequest(
            OwnedClient,
            provider,
            scopePath,
            "proj",
            repoFilters ?? [new MentionRepoFilterRequest("repo-guid")]);
    }

    /// <summary>A user really assigned to the client, so an empty listing can only be the role check.</summary>
    private static IUserRepository UserAssignedTo(Guid clientId)
    {
        var user = new AppUser { Id = Guid.NewGuid(), Username = "operator" };
        user.ClientAssignments.Add(
            new UserClientRole
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ClientId = clientId,
                Role = ClientRole.ClientAdministrator,
            });

        var users = Substitute.For<IUserRepository>();
        users.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(user);
        return users;
    }

    private static Dictionary<Guid, ClientRole> AdminOf(Guid clientId)
    {
        return new Dictionary<Guid, ClientRole> { [clientId] = ClientRole.ClientAdministrator };
    }

    private static Dictionary<Guid, ClientRole> ReaderOf(Guid clientId)
    {
        return new Dictionary<Guid, ClientRole> { [clientId] = ClientRole.ClientUser };
    }

    /// <summary>An organization the client really has set up, so a refusal can only be the scope check.</summary>
    private static IClientAdoOrganizationScopeRepository ConfiguredOrganizations(string organizationUrl = "https://dev.azure.com/org")
    {
        var scopes = Substitute.For<IClientAdoOrganizationScopeRepository>();
        scopes.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientAdoOrganizationScopeDto(
                    Guid.NewGuid(),
                    OwnedClient,
                    organizationUrl,
                    "org",
                    true,
                    AdoOrganizationVerificationStatus.Verified,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);
        return scopes;
    }

    /// <summary>
    ///     Asserts a request was refused as invalid, and refused by validation specifically. Checking only
    ///     for an ObjectResult would also accept a server error, which is the opposite of a request being
    ///     correctly turned away.
    /// </summary>
    /// <remarks>
    ///     The 400 itself is stamped on by the ProblemDetailsFactory during the real request pipeline, which
    ///     a controller holding a bare DefaultHttpContext never reaches, so Status is null here and asserting
    ///     it would only be testing the test. The payload type is the part this level can actually prove.
    /// </remarks>
    private static void AssertRefusedAsInvalid(IActionResult result)
    {
        Assert.IsType<ValidationProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
    }

    /// <summary>
    ///     Connections the client really holds, so a refusal for a provider other than Azure DevOps can only be
    ///     the scope check.
    /// </summary>
    private static IClientScmConnectionRepository ConfiguredConnections()
    {
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                Connection(ScmProvider.GitHub, "https://github.com"),
                Connection(ScmProvider.GitLab, "https://gitlab.com"),
                Connection(ScmProvider.Forgejo, "https://codeberg.org"),
            ]);
        return connections;
    }

    private static ClientScmConnectionDto Connection(ScmProvider provider, string hostBaseUrl, bool isActive = true)
    {
        return new ClientScmConnectionDto(
            Guid.NewGuid(),
            OwnedClient,
            provider,
            hostBaseUrl,
            ScmAuthenticationKind.PersonalAccessToken,
            provider.ToString(),
            isActive,
            "verified",
            DateTimeOffset.UtcNow,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    /// <summary>A deployment that can both discover pull requests and reply in them, for every provider.</summary>
    private static IScmProviderRegistry DiscoveryForEveryProvider()
    {
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.SupportsActivePullRequestDiscovery(Arg.Any<ScmProvider>()).Returns(true);
        registry.SupportsReviewThreadReply(Arg.Any<ScmProvider>()).Returns(true);
        return registry;
    }

    private static AdminMentionConfigsController CreateController(
        IMentionConfigurationRepository repo,
        IUserRepository? users = null,
        bool isAdmin = false,
        IReadOnlyDictionary<Guid, ClientRole>? clientRoles = null,
        IClientAdoOrganizationScopeRepository? organizationScopes = null,
        IClientScmConnectionRepository? connections = null,
        IScmProviderRegistry? providerRegistry = null,
        ILicensingCapabilityService? licensing = null)
    {
        var clients = Substitute.For<IClientAdminService>();
        clients.ExistsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        // The real rule, not a stand-in for it, so every endpoint test exercises what a request has to pass.
        var scopeValidator = new MentionConfigurationScopeValidator(
            connections ?? ConfiguredConnections(),
            providerRegistry ?? DiscoveryForEveryProvider(),
            organizationScopes ?? ConfiguredOrganizations());

        var controller = new AdminMentionConfigsController(
            repo,
            users ?? Substitute.For<IUserRepository>(),
            clients,
            scopeValidator,
            NullLogger<AdminMentionConfigsController>.Instance,
            null,
            licensing)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        controller.HttpContext.Items["UserId"] = Guid.NewGuid().ToString();

        if (isAdmin)
        {
            controller.HttpContext.Items["IsAdmin"] = true;
        }

        controller.HttpContext.Items["ClientRoles"] = clientRoles is null
            ? new Dictionary<Guid, ClientRole>()
            : new Dictionary<Guid, ClientRole>(clientRoles);

        return controller;
    }
}
