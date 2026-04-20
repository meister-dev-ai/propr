// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;

namespace MeisterProPR.Infrastructure.Features.Crawling.Webhooks.Security;

/// <summary>Cryptographically strong webhook secret generator.</summary>
public sealed class WebhookSecretGenerator : IWebhookSecretGenerator
{
    /// <inheritdoc />
    public string GenerateSecret()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }
}
