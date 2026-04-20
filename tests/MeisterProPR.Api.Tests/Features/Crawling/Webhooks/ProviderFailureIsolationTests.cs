// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Tests.Features.Crawling.Webhooks;

public sealed class ProviderFailureIsolationTests(WebhookReviewActivationIntegrationTests.WebhookReceiverApiFactory factory)
    : IClassFixture<WebhookReviewActivationIntegrationTests.WebhookReceiverApiFactory>
{
    [Fact]
    public async Task UnsupportedProviderDelivery_DoesNotBlockLaterAzureDevOpsDelivery()
    {
        await factory.ResetDeliveryLogsAsync();
        factory.ConfigureActivationScenario();
        var client = factory.CreateClient();

        using var failingPayload = AdoWebhookPayloadFactory.PullRequestUpdated();
        using var failingRequest = new HttpRequestMessage(HttpMethod.Post, "/webhooks/v1/providers/svn/path-key")
        {
            Content = new StringContent(failingPayload.RootElement.GetRawText(), Encoding.UTF8, "application/json"),
        };
        failingRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("ado-webhook:secret")));

        var failingResponse = await client.SendAsync(failingRequest);

        Assert.Equal(HttpStatusCode.NotFound, failingResponse.StatusCode);
        Assert.Null(await factory.GetLatestDeliveryAsync());

        using var acceptedPayload = AdoWebhookPayloadFactory.PullRequestUpdated();
        using var acceptedRequest = new HttpRequestMessage(HttpMethod.Post, "/webhooks/v1/providers/ado/path-key")
        {
            Content = new StringContent(acceptedPayload.RootElement.GetRawText(), Encoding.UTF8, "application/json"),
        };
        acceptedRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("ado-webhook:secret")));

        var acceptedResponse = await client.SendAsync(acceptedRequest);

        Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);

        var persisted = await factory.GetLatestDeliveryAsync();
        Assert.NotNull(persisted);
        Assert.Equal(WebhookDeliveryOutcome.Accepted, persisted!.DeliveryOutcome);
        Assert.Contains(
            persisted.ActionSummaries,
            summary => summary.Contains("Submitted review intake job", StringComparison.OrdinalIgnoreCase));
    }
}
