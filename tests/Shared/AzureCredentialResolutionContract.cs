// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Configuration;

namespace MeisterDev.Tests.Shared;

/// <summary>
///     The variable sets an Azure credential resolver accepts and refuses, asserted against every
///     implementation of that contract.
/// </summary>
/// <remarks>
///     Two implementations read the same three variables: the one the infrastructure services authenticate
///     with, and the one the key-ring protector add-in carries, which restates it because an add-in
///     references the key-ring contract and nothing else of the product. The two cannot be compiled into one
///     test assembly either: the add-in's Azure.Core defines the credential types the infrastructure's
///     Azure.Identity also defines, and a project referencing both cannot name them. This file is compiled
///     into both test projects instead, so one set of cases decides what each implementation does and a
///     change made to one of them fails beside the other.
/// </remarks>
internal static class AzureCredentialResolutionContract
{
    /// <summary>The two ids a client secret has to be accompanied by.</summary>
    private static readonly string[] ServicePrincipalIds = ["AZURE_TENANT_ID", "AZURE_CLIENT_ID"];

    /// <summary>
    ///     Asserts that <paramref name="resolve" /> selects the client-secret credential for a complete
    ///     service principal, refuses a client secret that is not accompanied by both ids while naming the
    ///     ones that are missing, and falls through to the default credential chain for every other set of
    ///     values.
    /// </summary>
    /// <param name="resolve">One implementation of the contract.</param>
    /// <remarks>
    ///     The variables carry the meanings the Azure SDK gives them, so the deployment shapes Azure documents
    ///     reach the default chain instead of stopping start-up: a user-assigned managed identity is named by
    ///     AZURE_CLIENT_ID alone, and a federated workload identity by a tenant id and a client id with no
    ///     secret.
    /// </remarks>
    public static void AssertHolds(Func<IConfiguration, object> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);

        // Compared by type name: each implementation binds to the copy of the credential type its own
        // assembly carries, so the two runs of this contract answer with two types of one name.
        Assert.Equal("ClientSecretCredential", resolve(Configuration(CompleteServicePrincipal())).GetType().Name);
        Assert.Equal("DefaultAzureCredential", resolve(Configuration(new Dictionary<string, string?>())).GetType().Name);

        // A secret is usable only as part of a service principal, so a secret without the ids that complete it
        // would otherwise leave the default chain authenticating as an identity nobody chose.
        foreach (var missing in ServicePrincipalIds)
        {
            var incomplete = CompleteServicePrincipal();
            incomplete.Remove(missing);

            var failure = Assert.Throws<InvalidOperationException>(() => resolve(Configuration(incomplete)));

            Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
        }

        // A secret on its own names both ids as missing.
        var secretAlone =
            Assert.Throws<InvalidOperationException>(() => resolve(Configuration(new Dictionary<string, string?> { ["AZURE_CLIENT_SECRET"] = "the-secret" })));

        foreach (var missing in ServicePrincipalIds)
        {
            Assert.Contains(missing, secretAlone.Message, StringComparison.Ordinal);
        }

        // A client id with no secret names the user-assigned managed identity the default chain authenticates
        // as, and a tenant id and client id with no secret are the workload-identity shape. Both are read by
        // the default chain.
        var withoutSecret = CompleteServicePrincipal();
        withoutSecret.Remove("AZURE_CLIENT_SECRET");

        Assert.Equal("DefaultAzureCredential", resolve(Configuration(withoutSecret)).GetType().Name);

        foreach (var present in ServicePrincipalIds)
        {
            var single = new Dictionary<string, string?> { [present] = "a-value" };

            Assert.Equal("DefaultAzureCredential", resolve(Configuration(single)).GetType().Name);
        }

        // Whitespace is not a value. A variable an environment file left blank would otherwise select a
        // service principal with an empty secret.
        var blank = CompleteServicePrincipal();
        blank["AZURE_CLIENT_SECRET"] = "   ";

        Assert.Equal("DefaultAzureCredential", resolve(Configuration(blank)).GetType().Name);

        var blankId = CompleteServicePrincipal();
        blankId["AZURE_CLIENT_ID"] = "   ";

        Assert.Throws<InvalidOperationException>(() => resolve(Configuration(blankId)));
    }

    private static Dictionary<string, string?> CompleteServicePrincipal()
    {
        return new Dictionary<string, string?>
        {
            ["AZURE_TENANT_ID"] = "00000000-0000-0000-0000-000000000001",
            ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002",
            ["AZURE_CLIENT_SECRET"] = "the-secret",
        };
    }

    private static IConfiguration Configuration(IDictionary<string, string?> settings)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
