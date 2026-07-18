// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Features.Reviewing.Contracts;
using MeisterProPR.Api.Features.Reviewing.Intake.Controllers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.Reviewing.Intake;

public sealed class BlockedPullRequestsControllerTests
{
    private const string Scope = "https://dev.azure.com/org";
    private const string Project = "proj";
    private const string Repo = "repo";
    private const int Pr = 42;

    [Fact]
    public async Task GetBlockedPullRequests_WithoutClientRole_ReturnsForbidden()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IBlockedPullRequestStore>();
        var controller = CreateController(store, clientId, null);

        var result = await controller.GetBlockedPullRequests(clientId, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task GetBlockedPullRequests_AsClientUser_ReturnsBlocks()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IBlockedPullRequestStore>();
        store.ListForClientAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
                (IReadOnlyList<BlockedPullRequest>)
                [
                    new BlockedPullRequest(Guid.NewGuid(), clientId, Scope, Project, Repo, Pr, Guid.NewGuid(), "too large"),
                ]);
        var controller = CreateController(store, clientId, ClientRole.ClientUser);

        var result = await controller.GetBlockedPullRequests(clientId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dtos = Assert.IsAssignableFrom<IReadOnlyList<BlockedPullRequestDto>>(ok.Value);
        Assert.Single(dtos);
        Assert.Equal(Pr, dtos[0].PullRequestId);
    }

    [Fact]
    public async Task BlockPullRequest_WithoutAdminRole_ReturnsForbidden()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IBlockedPullRequestStore>();
        var controller = CreateController(store, clientId, ClientRole.ClientUser);

        var result = await controller.BlockPullRequest(
            clientId,
            new BlockPullRequestRequest(Scope, Project, Repo, Pr),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        await store.DidNotReceive().BlockAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockPullRequest_AsAdmin_BlocksWithCallerIdentity()
    {
        var clientId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var store = Substitute.For<IBlockedPullRequestStore>();
        var controller = CreateController(store, clientId, ClientRole.ClientAdministrator, userId);

        var result = await controller.BlockPullRequest(
            clientId,
            new BlockPullRequestRequest(Scope, Project, Repo, Pr, "too large"),
            CancellationToken.None);

        Assert.IsType<OkResult>(result);
        await store.Received(1).BlockAsync(clientId, Scope, Project, Repo, Pr, userId, "too large", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockPullRequest_WithBlankRepository_ReturnsBadRequest()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IBlockedPullRequestStore>();
        var controller = CreateController(store, clientId, ClientRole.ClientAdministrator, Guid.NewGuid());

        var result = await controller.BlockPullRequest(
            clientId,
            new BlockPullRequestRequest(Scope, Project, "  ", Pr),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await store.DidNotReceive().BlockAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnblockPullRequest_AsAdmin_Unblocks()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IBlockedPullRequestStore>();
        var controller = CreateController(store, clientId, ClientRole.ClientAdministrator, Guid.NewGuid());

        var result = await controller.UnblockPullRequest(
            clientId,
            new UnblockPullRequestRequest(Scope, Project, Repo, Pr),
            CancellationToken.None);

        Assert.IsType<OkResult>(result);
        await store.Received(1).UnblockAsync(clientId, Scope, Project, Repo, Pr, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BlockPullRequest_WithAdminRoleButNoCallerIdentity_IsRejectedWithoutBlocking()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IBlockedPullRequestStore>();
        var controller = CreateControllerWithoutUserId(store, clientId, ClientRole.ClientAdministrator);

        var result = await controller.BlockPullRequest(
            clientId,
            new BlockPullRequestRequest(Scope, Project, Repo, Pr),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        await store.DidNotReceive().BlockAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnblockPullRequest_WithAdminRoleButNoCallerIdentity_IsRejectedWithoutUnblocking()
    {
        var clientId = Guid.NewGuid();
        var store = Substitute.For<IBlockedPullRequestStore>();
        var controller = CreateControllerWithoutUserId(store, clientId, ClientRole.ClientAdministrator);

        var result = await controller.UnblockPullRequest(
            clientId,
            new UnblockPullRequestRequest(Scope, Project, Repo, Pr),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        await store.DidNotReceive().UnblockAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    private static BlockedPullRequestsController CreateControllerWithoutUserId(
        IBlockedPullRequestStore store,
        Guid clientId,
        ClientRole role)
    {
        var controller = new BlockedPullRequestsController(store, NullLogger<BlockedPullRequestsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        // Administrator role granted, but no caller identity (UserId) on the request.
        controller.HttpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole> { [clientId] = role };

        return controller;
    }

    private static BlockedPullRequestsController CreateController(
        IBlockedPullRequestStore store,
        Guid? clientId,
        ClientRole? role,
        Guid? userId = null)
    {
        var controller = new BlockedPullRequestsController(store, NullLogger<BlockedPullRequestsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };

        if (clientId.HasValue)
        {
            controller.HttpContext.Items["UserId"] = (userId ?? Guid.NewGuid()).ToString();
        }

        if (clientId.HasValue && role.HasValue)
        {
            controller.HttpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>
                { [clientId.Value] = role.Value };
        }

        return controller;
    }
}
