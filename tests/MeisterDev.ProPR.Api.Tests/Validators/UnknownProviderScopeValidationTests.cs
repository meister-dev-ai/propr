// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Crawling.Webhooks.Validators;
using MeisterDev.ProPR.Api.Validators;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Validators;

public sealed class UnknownProviderScopeValidationTests
{
    [Theory]
    [InlineData(false, false, null)]
    [InlineData(false, true, null)]
    [InlineData(false, false, "  ")]
    [InlineData(false, true, "  ")]
    [InlineData(false, false, "https://provider.example/team")]
    [InlineData(false, true, "https://provider.example/team")]
    [InlineData(true, false, null)]
    [InlineData(true, true, null)]
    [InlineData(true, false, "  ")]
    [InlineData(true, true, "  ")]
    [InlineData(true, false, "https://provider.example/team")]
    [InlineData(true, true, "https://provider.example/team")]
    public void UnknownProviderRetainsScopePredicateAndModelError(bool webhook, bool savedScope, string? path)
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(_ => throw new InvalidOperationException("Invalid-family lookup"));
        var selection = new ReviewConfigurationSelectionService(registry);
        var scopeId = savedScope ? Guid.NewGuid() : (Guid?)null;
        var result = webhook
            ? new CreateAdminWebhookConfigRequestValidator(selection).Validate(
                new CreateAdminWebhookConfigRequest(Guid.NewGuid(), (WebhookProviderType)999, scopeId, path, "project", [WebhookEventType.PullRequestCreated]))
            : new CreateAdminCrawlConfigRequestValidator(selection).Validate(
                new CreateAdminCrawlConfigRequest(Guid.NewGuid(), "project", (ScmProvider)999, scopeId, path));

        if (string.IsNullOrWhiteSpace(path))
        {
            var error = Assert.Single(result.Errors);
            Assert.Equal("", error.PropertyName);
            Assert.Equal("Select a connection and scope, or supply an explicit provider and its manual scope coordinates.", error.ErrorMessage);
        }
        else
        {
            Assert.True(result.IsValid);
        }

        registry.DidNotReceiveWithAnyArgs().GetReviewSourcePolicy(default);
    }
}
