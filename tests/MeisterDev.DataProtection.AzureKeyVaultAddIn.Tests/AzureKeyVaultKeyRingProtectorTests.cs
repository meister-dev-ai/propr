// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using MeisterDev.Tests.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeisterDev.DataProtection.AzureKeyVaultAddIn.Tests;

/// <summary>
///     What the Key Vault protector configures, and what it reports when a setting it needs is absent. A live
///     vault is out of reach here, so what is asserted is the wiring the set-up leaves behind.
/// </summary>
public sealed class AzureKeyVaultKeyRingProtectorTests
{
    private const string KeyIdentifier = "https://contoso-vault.vault.azure.net/keys/propr-key-ring/9e1f";

    private const string BlobUri = "https://contoso.blob.core.windows.net/propr/keys.xml";

    [Fact]
    public void TheProtectorWrapsTheKeyRingWithTheConfiguredVaultKey()
    {
        var options = ComposeAndReadKeyManagement(
            new Dictionary<string, string?>
            {
                [AzureKeyVaultKeyRingProtector.KeyIdentifierKey] = KeyIdentifier,
            });

        Assert.NotNull(options.XmlEncryptor);
        Assert.Contains("AzureKeyVault", options.XmlEncryptor.GetType().FullName!, StringComparison.Ordinal);

        // The key the ring is wrapped with is the configured one: read from the member the encryptor wraps
        // with, so a copy of the value kept elsewhere on it cannot stand in for it.
        Assert.Equal(KeyIdentifier, ConfiguredKeyIdentifier(options.XmlEncryptor));
    }

    // The host configures the key store, and the protector replaces it only where a blob is configured.
    [Fact]
    public void TheKeyStoreStaysWhereTheHostPutItWithNoBlobConfigured()
    {
        var hostRepository = new FileSystemXmlRepository(new DirectoryInfo(Path.GetTempPath()), NullLoggerFactory.Instance);
        var services = new ServiceCollection();
        var builder = services.AddDataProtection();
        builder.Services.Configure<KeyManagementOptions>(options => options.XmlRepository = hostRepository);

        new AzureKeyVaultKeyRingProtector().Apply(
            builder,
            Configuration(
                new Dictionary<string, string?>
                {
                    [AzureKeyVaultKeyRingProtector.KeyIdentifierKey] = KeyIdentifier,
                }));

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            hostRepository,
            provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
    }

    [Fact]
    public void ABlobUriMovesTheKeyStoreToBlobStorage()
    {
        var options = ComposeAndReadKeyManagement(
            new Dictionary<string, string?>
            {
                [AzureKeyVaultKeyRingProtector.KeyIdentifierKey] = KeyIdentifier,
                [AzureKeyVaultKeyRingProtector.BlobUriKey] = BlobUri,
            });

        Assert.NotNull(options.XmlRepository);
        Assert.Contains("Blob", options.XmlRepository.GetType().FullName!, StringComparison.Ordinal);

        // The blob the ring is written to is the configured one, and not another blob in another account.
        Assert.Contains(BlobUri, ValuesHeldBy(options.XmlRepository));
    }

    [Fact]
    public void AMissingKeyIdentifierFailsTheSetUpAndNamesTheVariable()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Apply(new Dictionary<string, string?>()));

        Assert.Contains(
            AzureKeyVaultKeyRingProtector.KeyIdentifierKey,
            failure.Message,
            StringComparison.Ordinal);
    }

    // Both values are handed to a client that authenticates with this installation's Azure identity and
    // carries the key ring, so plain http and a value naming no endpoint are refused where they are read.
    [Theory]
    [InlineData(AzureKeyVaultKeyRingProtector.KeyIdentifierKey, "propr-key-ring")]
    [InlineData(AzureKeyVaultKeyRingProtector.KeyIdentifierKey, "http://contoso-vault.vault.azure.net/keys/ring")]
    [InlineData(AzureKeyVaultKeyRingProtector.KeyIdentifierKey, "https:contoso-vault")]
    [InlineData(AzureKeyVaultKeyRingProtector.BlobUriKey, "propr-key-ring")]
    [InlineData(AzureKeyVaultKeyRingProtector.BlobUriKey, "http://contoso.blob.core.windows.net/propr/keys.xml")]
    [InlineData(AzureKeyVaultKeyRingProtector.BlobUriKey, "https:contoso")]
    public void AValueThatIsNotAnHttpsUrlNamingAHostFailsTheSetUpAndNamesTheVariable(string key, string value)
    {
        var settings = new Dictionary<string, string?>
        {
            [AzureKeyVaultKeyRingProtector.KeyIdentifierKey] = KeyIdentifier,
            [key] = value,
        };

        var failure = Assert.Throws<InvalidOperationException>(() => Apply(settings));

        Assert.Contains(key, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServicePrincipalVariablesSelectTheClientSecretCredential()
    {
        var credential = AzureKeyVaultKeyRingProtector.ResolveCredential(
            Configuration(
                new Dictionary<string, string?>
                {
                    ["AZURE_TENANT_ID"] = "00000000-0000-0000-0000-000000000001",
                    ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002",
                    ["AZURE_CLIENT_SECRET"] = "the-secret",
                }));

        Assert.IsType<ClientSecretCredential>(credential);
    }

    // A secret is usable only as part of a service principal, so a secret missing one of its ids would leave
    // the default chain authenticating as a managed identity or a signed-in developer, and the key ring would
    // be wrapped under an identity nobody chose.
    [Theory]
    [InlineData("AZURE_TENANT_ID")]
    [InlineData("AZURE_CLIENT_ID")]
    public void AClientSecretWithoutBothIdsFailsAndNamesTheMissingVariable(string missing)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "00000000-0000-0000-0000-000000000001",
            ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002",
            ["AZURE_CLIENT_SECRET"] = "the-secret",
        };
        settings.Remove(missing);

        var failure = Assert.Throws<InvalidOperationException>(() => AzureKeyVaultKeyRingProtector.ResolveCredential(Configuration(settings)));

        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
    }

    // The deployment shape a user-assigned managed identity is configured in: an application id and nothing
    // else. The default chain authenticates as that identity.
    [Fact]
    public void AClientIdWithoutASecretSelectsTheDefaultCredentialChain()
    {
        var credential = AzureKeyVaultKeyRingProtector.ResolveCredential(
            Configuration(
                new Dictionary<string, string?>
                {
                    ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002",
                }));

        Assert.IsType<DefaultAzureCredential>(credential);
    }

    // The workload-identity shape: both ids, no secret, and a federated token file the default chain reads.
    [Fact]
    public void BothIdsWithoutASecretSelectTheDefaultCredentialChain()
    {
        var credential = AzureKeyVaultKeyRingProtector.ResolveCredential(
            Configuration(
                new Dictionary<string, string?>
                {
                    ["AZURE_TENANT_ID"] = "00000000-0000-0000-0000-000000000001",
                    ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002",
                }));

        Assert.IsType<DefaultAzureCredential>(credential);
    }

    [Fact]
    public void NoServicePrincipalVariablesSelectTheDefaultCredentialChain()
    {
        Assert.IsType<DefaultAzureCredential>(AzureKeyVaultKeyRingProtector.ResolveCredential(Configuration(new Dictionary<string, string?>())));
    }

    // This resolution restates the one the rest of the installation applies, because an add-in references the
    // key-ring contract and nothing else of the product. Both are held to one set of cases, so a change to
    // the variables either of them accepts or refuses fails on the other as well.
    [Fact]
    public void TheProtectorHoldsTheInstallationWideCredentialContract()
    {
        AzureCredentialResolutionContract.AssertHolds(AzureKeyVaultKeyRingProtector.ResolveCredential);
    }

    [Fact]
    public void TheProtectorNamesItselfTheWayAnOperatorSelectsIt()
    {
        Assert.Equal("azure-key-vault", new AzureKeyVaultKeyRingProtector().Name);
    }

    private static KeyManagementOptions ComposeAndReadKeyManagement(IDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        new AzureKeyVaultKeyRingProtector().Apply(services.AddDataProtection(), Configuration(settings));

        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
    }

    private static void Apply(IDictionary<string, string?> settings)
    {
        new AzureKeyVaultKeyRingProtector().Apply(
            new ServiceCollection().AddDataProtection(),
            Configuration(settings));
    }

    private static IConfiguration Configuration(IDictionary<string, string?> settings)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    /// <summary>
    ///     The key identifier <paramref name="encryptor" /> wraps the key ring with, read from the member it
    ///     keeps it in.
    /// </summary>
    /// <param name="encryptor">The encryptor the set-up configured.</param>
    /// <remarks>
    ///     The Azure encryptor exposes nothing of what it was built with, and a vault is out of reach here, so
    ///     the member it wraps with is read directly. What it holds elsewhere, such as a client built from the
    ///     same value, says nothing about the key a wrap would use.
    /// </remarks>
    private static string? ConfiguredKeyIdentifier(object encryptor)
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = encryptor.GetType();

        return type.GetProperty("KeyId", members)?.GetValue(encryptor) as string
               ?? type.GetField("_keyId", members)?.GetValue(encryptor) as string;
    }

    /// <summary>
    ///     The strings and URLs <paramref name="instance" /> was built with, read from its own fields and from
    ///     the objects they hold. The Azure encryptor keeps what it was configured with to itself, and this is
    ///     how a test reads what reached it.
    /// </summary>
    /// <param name="instance">The object to read.</param>
    private static IReadOnlyList<string> ValuesHeldBy(object instance)
    {
        var found = new List<string>();
        var pending = new Queue<object>();
        pending.Enqueue(instance);

        for (var depth = 0; depth < 3 && pending.Count > 0; depth++)
        {
            foreach (var current in pending.ToArray())
            {
                pending.Dequeue();
                foreach (var field in current.GetType()
                             .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    switch (field.GetValue(current))
                    {
                        case string text:
                            found.Add(text);
                            break;
                        case Uri uri:
                            found.Add(uri.ToString());
                            break;
                        case { } held when !held.GetType().IsPrimitive:
                            pending.Enqueue(held);
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        return found;
    }
}
