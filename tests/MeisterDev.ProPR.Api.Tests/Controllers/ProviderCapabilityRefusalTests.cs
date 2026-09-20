// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     The licence check made where a connection using a provider family is written.
/// </summary>
/// <remarks>
///     A family names the capability key it needs as an opaque string and carries no licensing code. This is the
///     first of the four points the host checks that key at, and the one an operator meets while the connection
///     form is still open.
/// </remarks>
public sealed class ProviderCapabilityRefusalTests
{
    [Fact]
    public async Task AFamilyWhoseDeclaredCapabilityTheLicenceCoversIsNotRefused()
    {
        var refusal = await ProviderCapabilityRefusal.DescribeAsync(
            Registry(),
            Licensed("example-connections", available: true),
            "example/provider");

        Assert.Null(refusal);
    }

    [Fact]
    public async Task AFamilyWhoseDeclaredCapabilityTheLicenceDoesNotCoverIsRefusedNamingTheRequirement()
    {
        var refusal = await ProviderCapabilityRefusal.DescribeAsync(
            Registry(),
            Licensed("example-connections", available: false),
            "example/provider");

        Assert.NotNull(refusal);
        Assert.Contains("example-connections", refusal, StringComparison.Ordinal);
    }

    // Every family this build ships declares none, so the check is inert for all of them and the gate is never
    // asked. Refusing a family that asks for nothing would take the shipped providers away.
    [Fact]
    public async Task AFamilyThatDeclaresNoCapabilityIsUnrestrictedAndTheLicenceIsNotConsulted()
    {
        var capabilities = Substitute.For<IProviderAddInCapabilityGate>();

        var refusal = await ProviderCapabilityRefusal.DescribeAsync(
            new AiProviderRegistry([Declaring(capabilityKey: null)]),
            capabilities,
            "example/provider");

        Assert.Null(refusal);
        await capabilities.DidNotReceiveWithAnyArgs().IsAvailableAsync(default, default);
    }

    // A composition with no licensing state has nothing to check against, and refusing everything there would
    // stop a deployment that carries no installation state from configuring a provider at all.
    [Fact]
    public async Task AHostWithNoLicenceCheckRefusesNothing()
    {
        Assert.Null(
            await ProviderCapabilityRefusal.DescribeAsync(
                Registry(),
                capabilities: null,
                "example/provider"));
    }

    private static IAiProviderDriverRegistry Registry()
    {
        return new AiProviderRegistry([Declaring("example-connections")]);
    }

    /// <summary>A family whose declaration names the capability its installation has to be entitled to.</summary>
    /// <param name="capabilityKey">The key the family declares.</param>
    private static IAiProviderDriver Declaring(string? capabilityKey)
    {
        var declaration = new ProviderDeclaration
        {
            Key = "example/provider",
            Label = "Example provider",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode("example/provider:ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs("example/provider:ApiKey"),
            RequiredCapabilityKey = capabilityKey,
        };

        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(declaration);
        driver.SupportedAuthModes.Returns(declaration.SupportedAuthModes);
        driver.CredentialFields.Returns(declaration.CredentialFields);

        return driver;
    }

    private static IProviderAddInCapabilityGate Licensed(string capabilityKey, bool available)
    {
        var capabilities = Substitute.For<IProviderAddInCapabilityGate>();
        capabilities.IsAvailableAsync(capabilityKey, Arg.Any<CancellationToken>()).Returns(available);

        return capabilities;
    }
}
