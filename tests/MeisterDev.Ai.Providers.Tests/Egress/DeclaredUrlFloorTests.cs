// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Egress;

/// <summary>
///     The rules an address entered into a field a provider family declared passes, whatever the family says
///     about it.
/// </summary>
public sealed class DeclaredUrlFloorTests
{
    [Theory]
    [InlineData("http://api.example.com/v1", "must use https")]
    [InlineData("https://127.0.0.1/v1", "private, loopback, or link-local")]
    [InlineData("https://169.254.169.254/latest", "private, loopback, or link-local")]
    [InlineData("https://10.0.0.5/v1", "private, loopback, or link-local")]
    [InlineData("not-a-url", "absolute URL")]
    public void ALockedInstallationRefusesAnAddressItDoesNotPermit(string value, string expected)
    {
        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(
            Declaring(UrlField()),
            new Dictionary<string, string> { ["endpoint"] = value },
            EgressUrlPolicy.Locked);

        Assert.Contains(expected, Assert.Single(refusals).Value, StringComparison.Ordinal);
    }

    // The refusal names the field's label, because that is what the operator is looking at.
    [Fact]
    public void ARefusalNamesTheFieldItIsAbout()
    {
        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(
            Declaring(UrlField()),
            new Dictionary<string, string> { ["endpoint"] = "http://api.example.com/v1" },
            EgressUrlPolicy.Locked);

        var refusal = Assert.Single(refusals);
        Assert.Equal("endpoint", refusal.Key);
        Assert.StartsWith("Endpoint ", refusal.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void APublicHttpsAddressIsPermitted()
    {
        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(
            Declaring(UrlField()),
            new Dictionary<string, string> { ["endpoint"] = "https://api.example.com/v1" },
            EgressUrlPolicy.Locked);

        Assert.Empty(refusals);
    }

    // A family that needs the host process and the operator's browser on one machine configures a loopback
    // callback address, and it is admitted without opening private egress across the whole installation.
    [Fact]
    public void APlainHttpLoopbackAddressIsAdmittedForAFamilyThatDeclaredCoLocation()
    {
        var declaration = Declaring(UrlField()) with { RequiresBrowserCoLocation = true };

        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(
            declaration,
            new Dictionary<string, string> { ["endpoint"] = "http://127.0.0.1:1455/auth/callback" },
            EgressUrlPolicy.Locked);

        Assert.Empty(refusals);
    }

    [Fact]
    public void APlainHttpLoopbackAddressIsRefusedForAFamilyThatDidNot()
    {
        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(
            Declaring(UrlField()),
            new Dictionary<string, string> { ["endpoint"] = "http://127.0.0.1:1455/auth/callback" },
            EgressUrlPolicy.Locked);

        Assert.Single(refusals);
    }

    // The carve-out reaches loopback alone. Every other private address still needs the installation-wide opt-in,
    // and that keeps a co-location declaration from becoming a way to reach the private network.
    [Fact]
    public void TheCoLocationCarveOutDoesNotReachOtherPrivateAddresses()
    {
        var declaration = Declaring(UrlField()) with { RequiresBrowserCoLocation = true };

        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(
            declaration,
            new Dictionary<string, string> { ["endpoint"] = "http://169.254.169.254/latest" },
            EgressUrlPolicy.Locked);

        Assert.Single(refusals);
    }

    // Nor does it admit a scheme that is not http or https: the value it exists for is a loopback callback.
    [Fact]
    public void TheCoLocationCarveOutDoesNotAdmitAnotherScheme()
    {
        var declaration = Declaring(UrlField()) with { RequiresBrowserCoLocation = true };

        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(
            declaration,
            new Dictionary<string, string> { ["endpoint"] = "ftp://localhost/payload" },
            EgressUrlPolicy.Locked);

        Assert.Single(refusals);
    }

    // A family that declares a field as free text has put the value somewhere the host does not read as an
    // address, and the host never uses it as one.
    [Fact]
    public void AValueInAFieldDeclaredAsPlainTextIsNotCheckedAsAnAddress()
    {
        var declaration = Declaring(new ProviderDeclaredField("endpoint", "Endpoint", ProviderFieldKind.String));

        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(
            declaration,
            new Dictionary<string, string> { ["endpoint"] = "http://169.254.169.254/latest" },
            EgressUrlPolicy.Locked);

        Assert.Empty(refusals);
    }

    // An installation that opened private egress reaches a self-hosted endpoint, and the scheme rule stays: the
    // opt-in says where traffic may go, not how it may travel.
    [Fact]
    public void ThePrivateEgressOptInDoesNotRelaxTheScheme()
    {
        var opened = new EgressUrlPolicy(AllowPrivateEgress: true, AllowInsecureScheme: false);

        Assert.Empty(
            DeclaredUrlFloor.FindRefusedAddressFields(
                Declaring(UrlField()),
                new Dictionary<string, string> { ["endpoint"] = "https://10.0.0.5/v1" },
                opened));

        Assert.Single(
            DeclaredUrlFloor.FindRefusedAddressFields(
                Declaring(UrlField()),
                new Dictionary<string, string> { ["endpoint"] = "http://10.0.0.5/v1" },
                opened));
    }

    // Whether a field had to be filled in is the required rule's question. An empty one carries no address, so
    // there is nothing here to refuse.
    [Fact]
    public void AFieldWithNoValueIsNotRefused()
    {
        var refusals = DeclaredUrlFloor.FindRefusedAddressFields(
            Declaring(UrlField()),
            new Dictionary<string, string> { ["endpoint"] = string.Empty },
            EgressUrlPolicy.Locked);

        Assert.Empty(refusals);
    }

    private static ProviderDeclaredField UrlField()
    {
        return new ProviderDeclaredField("endpoint", "Endpoint", ProviderFieldKind.Url);
    }

    private static ProviderDeclaration Declaring(params ProviderDeclaredField[] fields)
    {
        return new ProviderDeclaration
        {
            Key = "tests/floor",
            Label = "Floor test family",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            Fields = fields,
            AuthModes = [new ProviderDeclaredAuthMode("tests/floor:ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, "tests/floor:ChatCompletions"]),
            ConformanceInputs = new ProviderConformanceInputs("tests/floor:ApiKey"),
        };
    }
}
