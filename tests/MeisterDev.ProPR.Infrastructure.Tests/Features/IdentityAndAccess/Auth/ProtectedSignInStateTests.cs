// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess.Auth;

public sealed class ProtectedSignInStateTests
{
    private const string Purpose = "sign-in-state-test";

    private readonly SecretProtectionCodec _codec = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void TryRead_ReturnsTheStateThatProtectWrote()
    {
        var protectedState = ProtectedSignInState.Protect(this._codec, new SignInState("state-1", 7), Purpose);

        var state = ProtectedSignInState.TryRead<SignInState>(this._codec, protectedState, Purpose);

        Assert.Equal(new SignInState("state-1", 7), state);
    }

    [Fact]
    public void TryRead_WithJsonThatCarriesNoProtection_ReturnsNull()
    {
        var state = ProtectedSignInState.TryRead<SignInState>(this._codec, """{"Value":"forged","Number":1}""", Purpose);

        Assert.Null(state);
    }

    [Fact]
    public void TryRead_WithAStateProtectedForAnotherPurpose_ReturnsNull()
    {
        var protectedState = ProtectedSignInState.Protect(this._codec, new SignInState("state-1", 7), "another-purpose");

        Assert.Null(ProtectedSignInState.TryRead<SignInState>(this._codec, protectedState, Purpose));
    }

    [Fact]
    public void TryRead_WithAnAlteredState_ReturnsNull()
    {
        var protectedState = ProtectedSignInState.Protect(this._codec, new SignInState("state-1", 7), Purpose);
        var altered = protectedState[..^4] + (protectedState.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");

        Assert.Null(ProtectedSignInState.TryRead<SignInState>(this._codec, altered, Purpose));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryRead_WithoutState_ReturnsNull(string? protectedState)
    {
        Assert.Null(ProtectedSignInState.TryRead<SignInState>(this._codec, protectedState, Purpose));
    }

    private sealed record SignInState(string Value, int Number);
}
