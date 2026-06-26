// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.ValueObjects;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     The escalate-only security floor flags a file when ANY leg fires
///     (security-sensitive path, security marker, or model escalate). Over-inclusion is acceptable
///     (escalate-only); absence of all legs is never a positive "safe" signal.
/// </summary>
public sealed class SecurityFloorTests
{
    [Theory]
    [InlineData("src/Auth/TokenService.cs", true)]
    [InlineData("src/Features/Identity/LoginController.cs", true)]
    [InlineData("src/Crypto/Signer.cs", true)]
    [InlineData("config/secrets.json", true)]
    [InlineData("src/Widgets/Button.cs", false)]
    [InlineData("README.md", false)]
    [InlineData("", false)]
    public void IsSecuritySensitivePath_MatchesSensitiveLocations(string path, bool expected)
    {
        Assert.Equal(expected, SecurityFloor.IsSecuritySensitivePath(path));
    }

    [Fact]
    public void IsFlagged_FiresWhenAnyLegFires()
    {
        var none = FileRiskMarkers.None;
        var withMarker = new FileRiskMarkers(true, ["security.auth-token"]);

        Assert.True(SecurityFloor.IsFlagged("src/Auth/X.cs", none, false)); // path leg
        Assert.True(SecurityFloor.IsFlagged("src/Widgets/X.cs", withMarker, false)); // marker leg
        Assert.True(SecurityFloor.IsFlagged("src/Widgets/X.cs", none, true)); // model leg
        Assert.False(SecurityFloor.IsFlagged("src/Widgets/X.cs", none, false)); // no leg
    }
}
