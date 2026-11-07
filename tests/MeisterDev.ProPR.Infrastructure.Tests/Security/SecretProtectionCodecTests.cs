// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;

namespace MeisterDev.ProPR.Infrastructure.Tests.Security;

public sealed class SecretProtectionCodecTests
{
    [Fact]
    public void Protect_ThenUnprotect_RoundTripsPlaintext()
    {
        var sut = CreateCodec();

        var protectedValue = sut.Protect("super-secret-value", "AdoServicePrincipalCredentials");

        Assert.NotEqual("super-secret-value", protectedValue);
        Assert.True(sut.IsProtected(protectedValue));
        Assert.Equal("super-secret-value", sut.Unprotect(protectedValue, "AdoServicePrincipalCredentials"));
    }

    [Fact]
    public void Unprotect_PlaintextValue_ReturnsOriginalValue()
    {
        var sut = CreateCodec();

        Assert.Equal("legacy-plaintext", sut.Unprotect("legacy-plaintext", "AiConnectionApiKey"));
    }

    [Fact]
    public void Unprotect_WithWrongPurpose_ThrowsCryptographicException()
    {
        var sut = CreateCodec();
        var protectedValue = sut.Protect("super-secret-value", "AdoServicePrincipalCredentials");

        Assert.ThrowsAny<CryptographicException>(() => sut.Unprotect(protectedValue, "AiConnectionApiKey"));
    }

    [Fact]
    public void Protect_WebhookSecret_RoundTripsWithWebhookPurpose()
    {
        var sut = CreateCodec();

        var protectedValue = sut.Protect("generated-webhook-secret", "WebhookSecret");

        Assert.NotEqual("generated-webhook-secret", protectedValue);
        Assert.True(sut.IsProtected(protectedValue));
        Assert.Equal("generated-webhook-secret", sut.Unprotect(protectedValue, "WebhookSecret"));
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        return new SecretProtectionCodec(new EphemeralDataProtectionProvider());
    }
}
