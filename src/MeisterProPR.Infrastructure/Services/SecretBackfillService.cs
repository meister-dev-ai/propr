// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Services;

/// <summary>Rewrites legacy plaintext secret rows into protected-at-rest values.</summary>
public sealed class SecretBackfillService(
    MeisterProPRDbContext dbContext,
    ISecretProtectionCodec secretProtectionCodec)
{
    private const string ClientAdoPurpose = "ClientAdoCredentials";
    private const string AiConnectionPurpose = "AiConnectionApiKey";

    public async Task BackfillAsync(CancellationToken ct = default)
    {
        var changesMade = false;

        var clients = await dbContext.Clients
            .Where(client => !string.IsNullOrWhiteSpace(client.AdoClientSecret))
            .ToListAsync(ct);

        foreach (var client in clients)
        {
            if (client.AdoClientSecret is null || secretProtectionCodec.IsProtected(client.AdoClientSecret))
            {
                continue;
            }

            client.AdoClientSecret = secretProtectionCodec.Protect(client.AdoClientSecret, ClientAdoPurpose);
            changesMade = true;
        }

        var aiConnections = await dbContext.AiConnections
            .Where(connection => !string.IsNullOrWhiteSpace(connection.ApiKey))
            .ToListAsync(ct);

        foreach (var connection in aiConnections)
        {
            if (connection.ApiKey is null || secretProtectionCodec.IsProtected(connection.ApiKey))
            {
                continue;
            }

            connection.ApiKey = secretProtectionCodec.Protect(connection.ApiKey, AiConnectionPurpose);
            changesMade = true;
        }

        if (changesMade)
        {
            await dbContext.SaveChangesAsync(ct);
        }
    }
}
