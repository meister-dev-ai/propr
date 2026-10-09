// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.ProPR.Api.Features.Crawling.Configuration.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.TestSupport;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Api.Tests.Features.Crawling;

public sealed class ReviewTargetPolicyBindingTests
{
    [Theory]
    [InlineData("\"expectedTargetBranchPatterns\":[],\"targetBranchPatterns\":[]", HttpStatusCode.OK, "valid")]
    [InlineData("\"expectedTargetBranchPatterns\":[]", HttpStatusCode.BadRequest, "valid")]
    [InlineData("\"targetBranchPatterns\":[]", HttpStatusCode.BadRequest, "valid")]
    [InlineData("\"expectedTargetBranchPatterns\":null,\"targetBranchPatterns\":[]", HttpStatusCode.BadRequest, "valid")]
    [InlineData("\"expectedTargetBranchPatterns\":[],\"targetBranchPatterns\":null", HttpStatusCode.BadRequest, "valid")]
    [InlineData("\"expectedTargetBranchPatterns\":[],\"targetBranchPatterns\":[]", HttpStatusCode.BadRequest, "missing")]
    [InlineData("\"expectedTargetBranchPatterns\":[],\"targetBranchPatterns\":[]", HttpStatusCode.BadRequest, "null")]
    public async Task Patch_RequiresArraysAndBindsEmptyAllThroughMvc(string arrays, HttpStatusCode expectedStatus, string connectionKind)
    {
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var target = new CrawlConfigurationDto(
            Guid.NewGuid(), clientId, ScmProvider.GitHub,
            "https://github.example.test", "owner", 60, false, DateTimeOffset.UtcNow,
            [new CrawlRepoFilterDto(Guid.NewGuid(), "repo", [], new CanonicalSourceReferenceDto("GitHub", "repo-id"))]);
        var configurations = Substitute.For<ICrawlConfigurationRepository>();
        configurations.GetByClientAsync(clientId, Arg.Any<CancellationToken>()).Returns([target]);
        configurations.GetReviewTargetPolicySnapshotAsync(target.Id, clientId, Arg.Any<CancellationToken>()).Returns(target);
        configurations.UpdateReviewTargetPolicyAsync(
            target, clientId, Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(true);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByIdAsync(clientId, connectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionDto(
                connectionId, clientId, ScmProvider.GitHub, "https://github.example.test",
                ScmAuthenticationKind.PersonalAccessToken, "Fixture", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(ClientReviewTargetsController).Assembly);
        builder.Services.AddSingleton(configurations);
        builder.Services.AddSingleton(connections);
        var providers = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        providers.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
        builder.Services.AddSingleton(providers);
        await using var application = builder.Build();
        application.Use((context, next) =>
        {
            context.Items["IsAdmin"] = true;
            return next();
        });
        application.MapControllers();
        await application.StartAsync();
        using var client = application.GetTestClient();
        var selectedConnection =
            connectionKind == "missing" ? "" : "\"connectionId\":" + (connectionKind == "null" ? "null" : "\"" + connectionId + "\"") + ",";
        using var body = new StringContent("{" + selectedConnection + arrays + "}", Encoding.UTF8, "application/json");
        using var response = await client.PatchAsync($"/clients/{clientId}/review-targets/{target.Id}", body);
        Assert.Equal(expectedStatus, response.StatusCode);
        if (expectedStatus != HttpStatusCode.OK)
        {
            await configurations.DidNotReceiveWithAnyArgs().UpdateReviewTargetPolicyAsync(default!, default, default!, default!);
        }
    }
}
