// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.DataProtection.AzureKeyVaultAddIn;

/// <summary>
///     Wraps the key ring with an Azure Key Vault key, and optionally moves the key store to blob storage.
/// </summary>
/// <remarks>
///     The unwrapping key never leaves the vault, so a copy of the key files reads nothing without access to
///     the vault. With a blob URI configured there is no key directory to hold at all, and the installation's
///     Azure identity is what a restored deployment needs.
/// </remarks>
public sealed class AzureKeyVaultKeyRingProtector : IKeyRingProtector
{
    /// <summary>The name an operator selects this protector by.</summary>
    public const string ProtectorName = "azure-key-vault";

    /// <summary>The variable naming the Key Vault key the ring is wrapped with.</summary>
    public const string KeyIdentifierKey = "MEISTER_DATA_PROTECTION_AZURE_KEY_VAULT_KEY_ID";

    /// <summary>The variable naming a blob the key ring is stored in, in place of a directory.</summary>
    public const string BlobUriKey = "MEISTER_DATA_PROTECTION_AZURE_BLOB_URI";

    /// <summary>The application id of the service principal this installation authenticates with.</summary>
    internal const string ClientIdKey = "AZURE_CLIENT_ID";

    /// <summary>The Microsoft Entra tenant of that service principal.</summary>
    internal const string TenantIdKey = "AZURE_TENANT_ID";

    /// <summary>The client secret of that service principal.</summary>
    internal const string ClientSecretKey = "AZURE_CLIENT_SECRET";

    /// <inheritdoc />
    public string Name => ProtectorName;

    /// <inheritdoc />
    public void Apply(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var keyIdentifier = ReadUri(configuration, KeyIdentifierKey, required: true)!;
        var credential = ResolveCredential(configuration);

        // The blob store is applied first, so it replaces the file-system store the shared set-up configured
        // when a key path is also present.
        var blobUri = ReadUri(configuration, BlobUriKey, required: false);
        if (blobUri is not null)
        {
            builder.PersistKeysToAzureBlobStorage(blobUri, credential);
        }

        builder.ProtectKeysWithAzureKeyVault(keyIdentifier, credential);
    }

    /// <summary>
    ///     The Azure credential this installation authenticates with: the service principal named by the
    ///     standard variables where all three are present, and the default credential chain otherwise, which
    ///     picks up a managed identity, a federated workload identity or a signed-in Azure CLI.
    /// </summary>
    /// <param name="configuration">The configuration the host was started with.</param>
    /// <remarks>
    ///     The same resolution the rest of the installation applies. It is restated here because an add-in
    ///     references the key-ring contract and nothing else of the product.
    ///     <para>
    ///         The variables carry the meanings the Azure SDK gives them. <see cref="ClientIdKey" /> on its own
    ///         names a user-assigned managed identity, which the default chain is given through
    ///         <see cref="DefaultAzureCredentialOptions.ManagedIdentityClientId" />; a tenant id and a client id
    ///         together with a federated token file are the workload-identity shape, which the default chain
    ///         reads itself.
    ///     </para>
    /// </remarks>
    internal static TokenCredential ResolveCredential(IConfiguration configuration)
    {
        var clientId = Stated(configuration[ClientIdKey]);
        var tenantId = Stated(configuration[TenantIdKey]);
        var clientSecret = Stated(configuration[ClientSecretKey]);

        if (clientSecret is not null)
        {
            var missing = new[]
                {
                    (Key: ClientIdKey, Value: clientId),
                    (Key: TenantIdKey, Value: tenantId),
                }
                .Where(variable => variable.Value is null)
                .Select(variable => variable.Key)
                .ToArray();

            // A secret without the ids that complete it is a mistake with a silent outcome: the default chain
            // picks up a managed identity or a signed-in developer, the host starts, and the key ring is
            // wrapped under an identity the operator did not choose. Start-up stops instead, naming what is
            // missing.
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"The '{ProtectorName}' key-ring protector reads {ClientSecretKey}, which names an Azure "
                    + $"service principal, and {string.Join(" and ", missing)} "
                    + $"{(missing.Length == 1 ? "is" : "are")} not set. Set {ClientIdKey} and {TenantIdKey} as "
                    + "well, or clear the secret to authenticate with the default Azure credential chain.");
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

    /// <summary>The absolute URI a variable holds.</summary>
    /// <param name="configuration">The configuration the host was started with.</param>
    /// <param name="key">The variable to read.</param>
    /// <param name="required">Whether an absent value fails start-up.</param>
    /// <remarks>
    ///     Both values are handed to an Azure client that authenticates with this installation's identity and
    ///     carries the key ring. Plain http would put that traffic on the wire unencrypted, and a value with no
    ///     host names no endpoint at all, so each is refused at start-up instead of at the first request.
    /// </remarks>
    internal static Uri? ReadUri(IConfiguration configuration, string key, bool required)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return required
                ? throw new InvalidOperationException($"The '{ProtectorName}' key-ring protector requires {key}.")
                : null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException($"{key} must be an https URL naming a host.");
        }

        return uri;
    }
}
