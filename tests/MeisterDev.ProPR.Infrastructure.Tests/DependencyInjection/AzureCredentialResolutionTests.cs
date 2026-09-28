// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Identity;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.Tests.Shared;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Infrastructure.Tests.DependencyInjection;

/// <summary>
///     The identity the infrastructure services authenticate to Azure with, which is the one the key-ring
///     protector reads from the same three variables.
/// </summary>
public sealed class AzureCredentialResolutionTests
{
    [Fact]
    public void TheServicePrincipalVariablesSelectTheClientSecretCredential()
    {
        var credential = InfrastructureServiceExtensions.ResolveAzureDevOpsCredential(
            Configuration(
                new Dictionary<string, string?>
                {
                    ["AZURE_TENANT_ID"] = "00000000-0000-0000-0000-000000000001",
                    ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002",
                    ["AZURE_CLIENT_SECRET"] = "the-secret",
                }));

        Assert.IsType<ClientSecretCredential>(credential);
    }

    [Fact]
    public void NoServicePrincipalVariablesSelectTheDefaultCredentialChain()
    {
        Assert.IsType<DefaultAzureCredential>(InfrastructureServiceExtensions.ResolveAzureDevOpsCredential(Configuration(new Dictionary<string, string?>())));
    }

    // A secret is usable only as part of a service principal, so a secret missing one of its ids would leave
    // the default chain authenticating as a managed identity or a signed-in developer, and Azure DevOps
    // traffic would go out under an identity nobody chose.
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

        var failure = Assert.Throws<InvalidOperationException>(() => InfrastructureServiceExtensions.ResolveAzureDevOpsCredential(Configuration(settings)));

        Assert.Contains(missing, failure.Message, StringComparison.Ordinal);
    }

    // The deployment shape a user-assigned managed identity is configured in: an application id and nothing
    // else. The default chain authenticates as that identity.
    [Fact]
    public void AClientIdWithoutASecretSelectsTheDefaultCredentialChain()
    {
        var credential = InfrastructureServiceExtensions.ResolveAzureDevOpsCredential(
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
        var credential = InfrastructureServiceExtensions.ResolveAzureDevOpsCredential(
            Configuration(
                new Dictionary<string, string?>
                {
                    ["AZURE_TENANT_ID"] = "00000000-0000-0000-0000-000000000001",
                    ["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000002",
                }));

        Assert.IsType<DefaultAzureCredential>(credential);
    }

    // The key-ring protector add-in restates this resolution, because an add-in references the key-ring
    // contract and nothing else of the product. Both are held to one set of cases, so a change to the
    // variables either of them accepts or refuses fails on the other as well.
    [Fact]
    public void TheResolverHoldsTheInstallationWideCredentialContract()
    {
        AzureCredentialResolutionContract.AssertHolds(InfrastructureServiceExtensions.ResolveAzureDevOpsCredential);
    }

    private static IConfiguration Configuration(IDictionary<string, string?> settings)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
