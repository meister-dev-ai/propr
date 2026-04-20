// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Crawling.Webhooks.Commands.HandleProviderWebhookDelivery;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>
///     Receives provider-scoped webhook deliveries and routes supported providers through the shared provider-neutral
///     handler.
/// </summary>
[ApiController]
public sealed partial class ProviderWebhookReceiverController(
    HandleProviderWebhookDeliveryHandler providerDeliveryHandler,
    ILogger<ProviderWebhookReceiverController> logger) : ControllerBase
{
    /// <summary>Receives one provider-scoped webhook delivery for a resolved webhook configuration.</summary>
    /// <param name="provider">The normalized provider family segment or compatibility alias.</param>
    /// <param name="pathKey">Opaque public path key that resolves the target webhook configuration.</param>
    /// <param name="payload">Raw provider webhook payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Delivery was accepted for downstream processing or intentionally ignored.</response>
    /// <response code="400">Payload was malformed or missing required fields.</response>
    /// <response code="401">Provider verification material was missing or invalid.</response>
    /// <response code="404">The provider segment or path key was unknown.</response>
    [HttpPost("/webhooks/v1/providers/{provider}/{pathKey}")]
    [ProducesResponseType(typeof(WebhookDeliveryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Receive(
        string provider,
        string pathKey,
        [FromBody] JsonElement payload,
        CancellationToken ct = default)
    {
        if (!TryMapSupportedProvider(provider, out var mappedProvider))
        {
            LogRejectedUnsupportedProvider(logger, provider, pathKey);
            return this.NotFound();
        }

        var headers = this.Request.Headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var providerDecision = await providerDeliveryHandler.HandleAsync(
            new HandleProviderWebhookDeliveryCommand(mappedProvider, pathKey, headers, payload.GetRawText()),
            ct);

        if (providerDecision.ResponseStatus is not null)
        {
            LogAcknowledgedWebhookDelivery(logger, mappedProvider, pathKey, providerDecision.ResponseStatus);
            return this.StatusCode(
                providerDecision.HttpStatusCode,
                new WebhookDeliveryResponse(providerDecision.ResponseStatus));
        }

        LogRejectedWebhookDelivery(logger, mappedProvider, pathKey, providerDecision.HttpStatusCode);
        return this.StatusCode(providerDecision.HttpStatusCode);
    }

    private static bool TryMapSupportedProvider(string provider, out ScmProvider mappedProvider)
    {
        if (string.Equals(provider, "ado", StringComparison.OrdinalIgnoreCase))
        {
            mappedProvider = ScmProvider.AzureDevOps;
            return true;
        }

        return Enum.TryParse(provider, true, out mappedProvider);
    }

    [LoggerMessage(
        EventId = 2812,
        Level = LogLevel.Information,
        Message = "Acknowledged provider webhook delivery for {Provider} path key {PathKey} with status {Status}.")]
    private static partial void LogAcknowledgedWebhookDelivery(
        ILogger logger,
        ScmProvider provider,
        string pathKey,
        string status);

    [LoggerMessage(
        EventId = 2813,
        Level = LogLevel.Warning,
        Message =
            "Rejected provider webhook delivery for {Provider} path key {PathKey} with HTTP status {StatusCode}.")]
    private static partial void LogRejectedWebhookDelivery(
        ILogger logger,
        ScmProvider provider,
        string pathKey,
        int statusCode);

    [LoggerMessage(
        EventId = 2814,
        Level = LogLevel.Warning,
        Message =
            "Rejected provider webhook delivery for unsupported provider segment {Provider} at path key {PathKey}.")]
    private static partial void LogRejectedUnsupportedProvider(ILogger logger, string provider, string pathKey);
}

/// <summary>Response payload for acknowledged webhook deliveries.</summary>
public sealed record WebhookDeliveryResponse(string Status);
