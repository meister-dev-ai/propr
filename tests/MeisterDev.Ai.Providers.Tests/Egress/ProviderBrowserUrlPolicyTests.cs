// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Egress;

/// <summary>
///     What the host will and will not open in an operator's browser on a provider family's say-so.
/// </summary>
/// <remarks>
///     The address is authored by the family and the console it reaches is the administration origin, so both
///     halves are checked: the scheme, because a script address handed to a browser runs there, and the host,
///     because a family that declares two vendor hosts would otherwise be able to send an administrator to any
///     address it liked.
/// </remarks>
public sealed class ProviderBrowserUrlPolicyTests
{
    [Fact]
    public void AnAddressOnADeclaredHostIsOpened()
    {
        Assert.Null(
            ProviderBrowserUrlPolicy.GetRefusalReason(
                Declaring(["auth.example.com", "api.example.com"]),
                "https://auth.example.com/authorize?state=abc"));
    }

    [Fact]
    public void ASuffixPatternCoversItsSubdomainsAndTheHostItself()
    {
        var declaration = Declaring([".example.com"]);

        Assert.Null(ProviderBrowserUrlPolicy.GetRefusalReason(declaration, "https://auth.example.com/authorize"));
        Assert.Null(ProviderBrowserUrlPolicy.GetRefusalReason(declaration, "https://example.com/authorize"));
    }

    // Checking the scheme alone would let a family that declares its vendor's hosts send an administrator's
    // browser anywhere over https.
    [Fact]
    public void AnUndeclaredHostIsRefusedAndTheHostIsNamed()
    {
        var refusal = ProviderBrowserUrlPolicy.GetRefusalReason(
            Declaring(["auth.example.com"]),
            "https://evil.example.net/steal");

        Assert.NotNull(refusal);
        Assert.Contains("evil.example.net", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AFamilyThatDeclaredNoHostCanOpenNothing()
    {
        Assert.NotNull(
            ProviderBrowserUrlPolicy.GetRefusalReason(
                Declaring([]),
                "https://auth.example.com/authorize"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///etc/passwd")]
    public void AnAddressThatIsNotWebIsRefusedAndItsSchemeIsNamed(string url)
    {
        var refusal = ProviderBrowserUrlPolicy.GetRefusalReason(Declaring(["auth.example.com"]), url);

        Assert.NotNull(refusal);
        Assert.Contains(new Uri(url).Scheme, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/authorize")]
    [InlineData("auth.example.com/authorize")]
    [InlineData("")]
    [InlineData(null)]
    public void AnAddressThatIsNotAbsoluteIsRefused(string? url)
    {
        Assert.NotNull(ProviderBrowserUrlPolicy.GetRefusalReason(Declaring(["auth.example.com"]), url));
    }

    // The loopback carve-out exists for a redirect that comes back to this machine, which is not egress and
    // which no declared pattern names.
    [Fact]
    public void PlainHttpToALoopbackHostIsOpenedOnlyForAFamilyThatDeclaredCoLocation()
    {
        Assert.Null(
            ProviderBrowserUrlPolicy.GetRefusalReason(
                Declaring(["auth.example.com"], coLocated: true),
                "http://127.0.0.1:1455/auth/callback"));

        Assert.NotNull(
            ProviderBrowserUrlPolicy.GetRefusalReason(
                Declaring(["auth.example.com"]),
                "http://127.0.0.1:1455/auth/callback"));
    }

    [Theory]
    [InlineData("http://auth.example.com/authorize")]
    [InlineData("http://10.0.0.5/authorize")]
    public void PlainHttpToAnythingOtherThanLoopbackIsRefusedForEveryFamily(string url)
    {
        Assert.NotNull(ProviderBrowserUrlPolicy.GetRefusalReason(Declaring(["auth.example.com"]), url));
        Assert.NotNull(ProviderBrowserUrlPolicy.GetRefusalReason(Declaring(["auth.example.com"], coLocated: true), url));
    }

    // The declared patterns and a tenant's endpoint list are written in one form, so they are matched by one
    // rule. Two implementations would drift, and the drift would be invisible until something was let through.
    [Theory]
    [InlineData("api.example.com", "api.example.com", true)]
    [InlineData("api.example.com", "other.example.com", false)]
    [InlineData(".example.com", "api.example.com", true)]
    [InlineData(".example.com", "example.com", true)]
    [InlineData(".example.com", "example.com.evil.net", false)]
    [InlineData("API.Example.COM", "api.example.com", true)]
    [InlineData("api.example.com", "api.example.com.evil.net", false)]
    public void PatternMatchingBehavesTheSameHereAsForATenantsEndpointList(
        string pattern,
        string host,
        bool covered)
    {
        Assert.Equal(covered, ProviderHostPattern.DoesPatternMatchHost(pattern, host));

        var refusal = ProviderBrowserUrlPolicy.GetRefusalReason(Declaring([pattern]), $"https://{host}/authorize");
        Assert.Equal(covered, refusal is null);
    }

    // The host patterns are what an administrator is told the address was checked against, and userinfo changes
    // which host a browser reaches: a further '@' makes the covered name a user name on some other host.
    [Theory]
    [InlineData("https://evil@api.example.com/authorize")]
    [InlineData("https://api.example.com:pw@attacker.test/authorize")]
    [InlineData("https://user:pass@api.example.com/authorize")]
    public void AnAddressCarryingACredentialIsRefused(string url)
    {
        var refusal = ProviderBrowserUrlPolicy.GetRefusalReason(Declaring(["api.example.com"]), url);

        Assert.NotNull(refusal);
        Assert.Contains("carries a credential in the address", refusal, StringComparison.Ordinal);
    }

    // The operator's browser carries whatever session it has for a local service, which the family's own code
    // does not. An address on a port the family never binds reaches something else on the machine.
    [Fact]
    public void ALoopbackAddressIsOpenedOnThePortTheConnectionSaysTheFamilyBinds()
    {
        var declaration = Declaring(["auth.example.com"], coLocated: true);

        Assert.Null(
            ProviderBrowserUrlPolicy.GetRefusalReason(
                declaration,
                "http://127.0.0.1:1455/auth/callback",
                [1455]));

        var refusal = ProviderBrowserUrlPolicy.GetRefusalReason(
            declaration,
            "http://127.0.0.1:9090/admin",
            [1455]);

        Assert.NotNull(refusal);
        Assert.Contains("9090", refusal, StringComparison.Ordinal);
        Assert.Contains("1455", refusal, StringComparison.Ordinal);
    }

    // A family that named no port field has told the host nothing about which port it binds, so the carve-out
    // reaches any loopback port, as it did before a family could name one.
    [Fact]
    public void AFamilyThatNamesNoPortKeepsTheWholeLoopbackCarveOut()
    {
        Assert.Null(
            ProviderBrowserUrlPolicy.GetRefusalReason(
                Declaring(["auth.example.com"], coLocated: true),
                "http://127.0.0.1:9090/admin",
                []));
    }

    // Read off the declaration and the connection's values, which is what the dispatcher hands the policy.
    [Fact]
    public void ThePortIsReadFromTheFieldTheFamilyNamed()
    {
        var declaration = Declaring(["auth.example.com"], coLocated: true) with
        {
            OpensListener = true,
            Fields =
            [
                new ProviderDeclaredField("listenerPort", "Callback port", ProviderFieldKind.Int) { DefaultValue = "8976" },
                new ProviderDeclaredField("timeoutSeconds", "Timeout", ProviderFieldKind.Int) { DefaultValue = "30" },
            ],
            ListenerPortFieldNames = ["listenerPort"],
        };

        Assert.Equal([8976], declaration.ListenerPortsIn(null));
        Assert.Equal(
            [1455],
            declaration.ListenerPortsIn(new Dictionary<string, string>(StringComparer.Ordinal) { ["listenerPort"] = "1455" }));
    }

    private static ProviderDeclaration Declaring(IReadOnlyList<string> hosts, bool coLocated = false)
    {
        return new ProviderDeclaration
        {
            Key = "example/provider",
            Label = "Example provider",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode("example/provider:ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs("example/provider:ApiKey"),
            ReachedHostPatterns = hosts,
            RequiresBrowserCoLocation = coLocated,
        };
    }
}
