// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Security;

public sealed class SecretProtectionCodecTests : IDisposable
{
    private readonly string _keysDirectory;

    public SecretProtectionCodecTests()
    {
        try
        {
            this._keysDirectory = Path.Combine(
                ResolveRepoScratchRoot(),
                $"MeisterProPR.SecretProtectionCodecTests.{Guid.NewGuid():N}");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "Failed to initialize SecretProtectionCodecTests because the repository scratch root could not be located.",
                ex);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(this._keysDirectory))
        {
            Directory.Delete(this._keysDirectory, true);
        }
    }

    [Fact]
    public void Protect_ThenUnprotect_RoundTripsPlaintext()
    {
        var sut = this.CreateCodec();

        var protectedValue = sut.Protect("super-secret-value", "AdoServicePrincipalCredentials");

        Assert.NotEqual("super-secret-value", protectedValue);
        Assert.True(sut.IsProtected(protectedValue));
        Assert.Equal("super-secret-value", sut.Unprotect(protectedValue, "AdoServicePrincipalCredentials"));
    }

    [Fact]
    public void Unprotect_PlaintextValue_ReturnsOriginalValue()
    {
        var sut = this.CreateCodec();

        Assert.Equal("legacy-plaintext", sut.Unprotect("legacy-plaintext", "AiConnectionApiKey"));
    }

    [Fact]
    public void Unprotect_WithWrongPurpose_ThrowsCryptographicException()
    {
        var sut = this.CreateCodec();
        var protectedValue = sut.Protect("super-secret-value", "AdoServicePrincipalCredentials");

        Assert.ThrowsAny<CryptographicException>(() => sut.Unprotect(protectedValue, "AiConnectionApiKey"));
    }

    [Fact]
    public void Protect_WebhookSecret_RoundTripsWithWebhookPurpose()
    {
        var sut = this.CreateCodec();

        var protectedValue = sut.Protect("generated-webhook-secret", "WebhookSecret");

        Assert.NotEqual("generated-webhook-secret", protectedValue);
        Assert.True(sut.IsProtected(protectedValue));
        Assert.Equal("generated-webhook-secret", sut.Unprotect(protectedValue, "WebhookSecret"));
    }

    private ISecretProtectionCodec CreateCodec()
    {
        Directory.CreateDirectory(this._keysDirectory);
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(this._keysDirectory));

        using var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }

    private static string ResolveRepoScratchRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var hasSolution = File.Exists(Path.Combine(current.FullName, "MeisterProPR.slnx"));
            var hasSourceTree = Directory.Exists(Path.Combine(current.FullName, "src"));

            if (hasSolution && hasSourceTree)
            {
                var scratchRoot = Path.Combine(current.FullName, ".ignore", "tmp");
                Directory.CreateDirectory(scratchRoot);
                return scratchRoot;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository scratch root.");
    }
}
