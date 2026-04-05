// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Security;

public sealed class SecretProtectionCodecTests : IDisposable
{
    private readonly string _keysDirectory = Path.Combine(Path.GetTempPath(), $"MeisterProPR.SecretProtectionCodecTests.{Guid.NewGuid():N}");

    [Fact]
    public void Protect_ThenUnprotect_RoundTripsPlaintext()
    {
        var sut = this.CreateCodec();

        var protectedValue = sut.Protect("super-secret-value", "ClientAdoCredentials");

        Assert.NotEqual("super-secret-value", protectedValue);
        Assert.True(sut.IsProtected(protectedValue));
        Assert.Equal("super-secret-value", sut.Unprotect(protectedValue, "ClientAdoCredentials"));
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
        var protectedValue = sut.Protect("super-secret-value", "ClientAdoCredentials");

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => sut.Unprotect(protectedValue, "AiConnectionApiKey"));
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

    public void Dispose()
    {
        if (Directory.Exists(this._keysDirectory))
        {
            Directory.Delete(this._keysDirectory, recursive: true);
        }
    }
}
