// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Claims;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

public sealed class ExtensionAuthorizationApiTests
{
    [Theory]
    [InlineData(false, "not_configured")]
    [InlineData(true, "database_required")]
    public void PublicMetadataWithoutDatabaseServicesIsDisabled(bool configured, string reason)
    {
        var options = configured
            ? new ExtensionAuthorizationOptions("https://propr.test/api", ["https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/propr"])
            : new ExtensionAuthorizationOptions(null, []);
        var services = new ServiceCollection().AddSingleton(options)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build()).BuildServiceProvider();
        var controller = ActivatorUtilities.CreateInstance<ExtensionAuthorizationController>(services);
        var value = controller.GetOptions().Value;
        Assert.NotNull(value);
        Assert.False(value.Enabled);
        Assert.Equal(reason, value.Reason);
        Assert.Null(value.TokenEndpoint);
    }

    [Fact]
    public async Task AuthorizationNeverRedirectsToAnHttpWebsite()
    {
        const string callback = "https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/propr";
        var auth = Substitute.For<IExtensionAuthorizationService>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MEISTER_PUBLIC_BASE_URL"] = "http://propr.test/api",
            }).Build();
        var controller = new ExtensionAuthorizationController(new("https://propr.test/api", [callback]), configuration, auth)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.Request.Scheme = "https";
        var response = Assert.IsType<ObjectResult>(await controller.Authorize("propr-lens", callback, "code", new string('a', 43), "S256", "state"));
        Assert.Equal(503, response.StatusCode);
        await auth.DidNotReceive().StartAsync(Arg.Any<ExtensionAuthorizationRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersonalAccessTokensCannotApproveWebsiteConsent()
    {
        var context = new DefaultHttpContext();
        context.Items["UserId"] = Guid.NewGuid().ToString();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new("sub", context.Items["UserId"]!.ToString()!)], "pat"));
        var service = Substitute.For<IExtensionAuthorizationService>();
        var controller = new ExtensionAuthorizationController(new(null, []), new ConfigurationBuilder().Build(), service)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        Assert.IsType<UnauthorizedResult>(await controller.Decide(new ExtensionDecisionRequest("request", true), default));
        await service.DidNotReceive().DecideAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Auth", "GetMe", true)]
    [InlineData("Auth", "Logout", false)]
    [InlineData("Auth", "Refresh", false)]
    [InlineData("ExtensionAuthorization", "Decide", false)]
    [InlineData("UserPats", "CreatePat", false)]
    [InlineData("Clients", "UpdateClient", false)]
    [InlineData("ReviewJobs", "SubmitReviewByCoordinates", true)]
    [InlineData("CodeQuality", "GetFindings", true)]
    public void ExtensionTokensHaveAPositiveActionBoundary(string controller, string action, bool expected)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(
            new Endpoint(null, new EndpointMetadataCollection(new ControllerActionDescriptor { ControllerName = controller, ActionName = action }), "test"));
        Assert.Equal(expected, ExtensionOperationPolicy.Authorize(context));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BearerResolutionRequiresTheBoundGrantAndWebsiteSession(bool active)
    {
        var userId = Guid.NewGuid();
        var grant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
            [
                new("sub", userId.ToString()), new("aud", "propr-lens"),
                new("extension_grant_id", grant.ToString()), new("extension_session_id", session.ToString())
            ], "test"));
        var jwt = Substitute.For<IJwtTokenService>();
        jwt.ValidateAccessToken("test-access").Returns(principal);
        var auth = Substitute.For<IExtensionAuthorizationService>();
        auth.IsAccessActiveAsync(userId, grant, session, Arg.Any<CancellationToken>()).Returns(active);
        var users = Substitute.For<IUserRepository>();
        users.GetByIdWithAssignmentsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new AppUser { Id = userId, Username = "alice", IsActive = true });
        var provider = new ServiceCollection().AddSingleton(jwt).AddSingleton(auth).AddSingleton(users).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Headers.Authorization = "Bearer test-access";
        var result = await CallerIdentityResolver.ResolveAsync(context, "test");
        Assert.Equal(active, result.Principal is not null);
        Assert.Equal(active, context.Items[ExtensionOperationPolicy.ItemKey] is true);
        Assert.Equal(active, context.Items.ContainsKey("UserId"));
    }

    [Fact]
    public async Task ExtensionAccessOfADisabledAccountIsRefusedAsDisabled()
    {
        var user = new AppUser { Id = Guid.NewGuid(), Username = "alice", IsActive = false };
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
            [
                new("sub", user.Id.ToString()), new("aud", "propr-lens"),
                new("extension_grant_id", Guid.NewGuid().ToString()), new("extension_session_id", Guid.NewGuid().ToString())
            ], "test"));
        var jwt = Substitute.For<IJwtTokenService>();
        jwt.ValidateAccessToken("test-access").Returns(principal);
        var auth = Substitute.For<IExtensionAuthorizationService>();
        var users = Substitute.For<IUserRepository>();
        users.GetByIdWithAssignmentsAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var context = new DefaultHttpContext
            { RequestServices = new ServiceCollection().AddSingleton(jwt).AddSingleton(auth).AddSingleton(users).BuildServiceProvider() };
        context.Request.Headers.Authorization = "Bearer test-access";
        var result = await CallerIdentityResolver.ResolveAsync(context, "test");
        Assert.True(result.AccountDisabled);
        Assert.Null(result.Principal);
        Assert.False(context.Items[ExtensionOperationPolicy.ItemKey] is true);
        Assert.False(context.Items.ContainsKey("UserId"));
    }

    [Fact]
    public async Task ExtensionAccessUsesTheCurrentGlobalRoleRatherThanAnOldAdminClaim()
    {
        var user = new AppUser { Id = Guid.NewGuid(), Username = "alice", IsActive = true, GlobalRole = AppUserRole.User };
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
            [
                new("sub", user.Id.ToString()), new("aud", "propr-lens"),
                new("global_role", "Admin"), new("extension_grant_id", Guid.NewGuid().ToString()), new("extension_session_id", Guid.NewGuid().ToString())
            ], "test"));
        var jwt = Substitute.For<IJwtTokenService>();
        jwt.ValidateAccessToken("test-access").Returns(principal);
        var auth = Substitute.For<IExtensionAuthorizationService>();
        auth.IsAccessActiveAsync(user.Id, Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var users = Substitute.For<IUserRepository>();
        users.GetByIdWithAssignmentsAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var context = new DefaultHttpContext
            { RequestServices = new ServiceCollection().AddSingleton(jwt).AddSingleton(auth).AddSingleton(users).BuildServiceProvider() };
        context.Request.Headers.Authorization = "Bearer test-access";
        Assert.NotNull((await CallerIdentityResolver.ResolveAsync(context, "test")).Principal);
        Assert.Equal(false, context.Items["IsAdmin"]);
    }
}
