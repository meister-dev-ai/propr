// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Crawling.Webhooks.Ports;

/// <summary>Generates one-time plaintext secrets for webhook registrations.</summary>
public interface IWebhookSecretGenerator
{
    /// <summary>Generates a new plaintext secret.</summary>
    string GenerateSecret();
}
