// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Validators;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
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
        IReadOnlyList<MentionRepoFilterRequest>? repoFilters = null)
    {
        return new CreateMentionConfigRequest(
            OwnedClient,
            ScmProvider.AzureDevOps,
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

    private static AdminMentionConfigsController CreateController(
        IMentionConfigurationRepository repo,
        IUserRepository? users = null,
        bool isAdmin = false,
        IReadOnlyDictionary<Guid, ClientRole>? clientRoles = null,
        IClientAdoOrganizationScopeRepository? organizationScopes = null)
    {
        var clients = Substitute.For<IClientAdminService>();
        clients.ExistsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        var controller = new AdminMentionConfigsController(
            repo,
            users ?? Substitute.For<IUserRepository>(),
            clients,
            organizationScopes ?? ConfiguredOrganizations(),
            NullLogger<AdminMentionConfigsController>.Instance)
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
