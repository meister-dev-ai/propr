// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.


using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Security;

/// <summary>Resolves native Azure DevOps startup credentials without acquiring a token.</summary>
internal static class AdoCredentialResolver
{
    /// <summary>
    ///     Resolves the credential used by Azure DevOps infrastructure from configuration. AZURE_CLIENT_ID, AZURE_TENANT_ID and
    ///     AZURE_CLIENT_SECRET together select a <see cref="ClientSecretCredential" />. Other valid configurations
    ///     select <see cref="DefaultAzureCredential" />, which supports managed identity, a
    ///     federated workload identity or an Azure CLI login.
    /// </summary>
    /// <param name="configuration">The configuration the host was started with.</param>
    /// <remarks>
    ///     Azure DevOps infrastructure uses this credential. The Azure Key Vault add-in has a separate resolver
    ///     that follows the same AZURE_* configuration conventions.
    ///     <para>
    ///         The variables carry the meanings the Azure SDK gives them, so a deployment that sets them the way
    ///         Azure documents keeps working. AZURE_CLIENT_ID on its own names a user-assigned managed identity,
    ///         which the default chain is given through <see cref="DefaultAzureCredentialOptions.ManagedIdentityClientId" />;
    ///         a tenant id and a client id together with a federated token file are the workload-identity shape,
    ///         which the default chain reads itself.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     AZURE_CLIENT_SECRET is set without both AZURE_CLIENT_ID and AZURE_TENANT_ID. Startup fails
    ///     because client-secret authentication requires both identifiers.
    /// </exception>
    internal static TokenCredential Resolve(IConfiguration configuration)
    {
        var clientId = Stated(configuration["AZURE_CLIENT_ID"]);
        var tenantId = Stated(configuration["AZURE_TENANT_ID"]);
        var clientSecret = Stated(configuration["AZURE_CLIENT_SECRET"]);

        if (clientSecret is not null)
        {
            var missing = new[]
                {
                    (Key: "AZURE_CLIENT_ID", Value: clientId),
                    (Key: "AZURE_TENANT_ID", Value: tenantId),
                }
                .Where(variable => variable.Value is null)
                .Select(variable => variable.Key)
                .ToArray();

            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    "AZURE_CLIENT_SECRET is set, which names a service principal, and "
                    + $"{string.Join(" and ", missing)} {(missing.Length == 1 ? "is" : "are")} not set. Set "
                    + "AZURE_CLIENT_ID and AZURE_TENANT_ID as well, or clear the secret to authenticate with "
                    + "the default Azure credential chain.");
            }

            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }

        return clientId is null
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId });
    }

    /// <summary>The value of a variable, or <see langword="null" /> where it holds nothing usable.</summary>
    /// <param name="value">What configuration answered with.</param>
    /// <remarks>
    ///     Whitespace is not a value: a variable an environment file left blank would otherwise name a service
    ///     principal with an empty secret.
    /// </remarks>
    private static string? Stated(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
