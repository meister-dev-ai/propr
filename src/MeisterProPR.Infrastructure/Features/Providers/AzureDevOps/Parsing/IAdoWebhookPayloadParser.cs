// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Crawling.Webhooks.Models;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Parsing;

/// <summary>Provider-local parser for Azure DevOps webhook payloads.</summary>
public interface IAdoWebhookPayloadParser
{
    IncomingAdoWebhookDelivery Parse(string pathKey, JsonElement payload);
}
