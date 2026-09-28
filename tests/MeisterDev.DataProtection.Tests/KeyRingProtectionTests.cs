// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.DataProtection.Tests;

/// <summary>
///     What an operator holding a key directory, a certificate, or one without the other, can read.
/// </summary>
public sealed class KeyRingProtectionTests : IDisposable
{
    private const string Purpose = "provider-connection-secret";
    private const string CertificatePassword = "key-ring-test-password";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "key-ring-" + Guid.NewGuid().ToString("N"));

    public KeyRingProtectionTests()
    {
        Directory.CreateDirectory(this._root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test run over.
        }
    }

    [Fact]
    public void AKeyRingProtectedByACertificateIsReadBackWithThatCertificate()
    {
        var keysPath = this.MakeDirectory("keys");
        var certificatePath = this.WriteCertificate("current");
        var settings = CertificateSettings(keysPath, certificatePath);

        var payload = Protect(settings, "the-stored-secret");

        // A second host over the same key directory and the same certificate: what a restore holds.
        Assert.Equal("the-stored-secret", Unprotect(settings, payload));
    }

    [Fact]
    public void AKeyRingProtectedByACertificateIsUnreadableWithoutIt()
    {
        var keysPath = this.MakeDirectory("keys");
        var certificatePath = this.WriteCertificate("current");

        var payload = Protect(CertificateSettings(keysPath, certificatePath), "the-stored-secret");

        // The key directory alone, restored onto a host with no protector configured.
        var withoutTheCertificate = new Dictionary<string, string?>
        {
            [MeisterDataProtectionExtensions.KeysPathKey] = keysPath,
        };

        Assert.ThrowsAny<CryptographicException>(() => Unprotect(withoutTheCertificate, payload));
    }

    [Fact]
    public void ARotatedCertificateReadsWhatThePreviousOneProtected()
    {
        var keysPath = this.MakeDirectory("keys");
        var firstCertificate = this.WriteCertificate("first");
        var secondCertificate = this.WriteCertificate("second");

        var payload = Protect(CertificateSettings(keysPath, firstCertificate), "the-stored-secret");

        var rotated = CertificateSettings(keysPath, secondCertificate);
        rotated[CertificateKeyRingProtector.PreviousCertificatePathsKey] = firstCertificate;

        Assert.Equal("the-stored-secret", Unprotect(rotated, payload));
    }

    [Fact]
    public void ARotatedCertificateCannotReadWhatThePreviousOneProtectedWhenItIsNotListed()
    {
        var keysPath = this.MakeDirectory("keys");
        var firstCertificate = this.WriteCertificate("first");
        var secondCertificate = this.WriteCertificate("second");

        var payload = Protect(CertificateSettings(keysPath, firstCertificate), "the-stored-secret");

        Assert.ThrowsAny<CryptographicException>(() => Unprotect(CertificateSettings(keysPath, secondCertificate), payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(MeisterDataProtectionExtensions.NoProtectorName)]
    [InlineData("NONE")]
    public void WithNoProtectorTheKeyRingIsReadByAHostThatHasNoCertificate(string? protectorName)
    {
        var keysPath = this.MakeDirectory("keys");
        var settings = new Dictionary<string, string?>
        {
            [MeisterDataProtectionExtensions.KeysPathKey] = keysPath,
            [MeisterDataProtectionExtensions.ProtectorKey] = protectorName,
        };

        var payload = Protect(settings, "the-stored-secret");

        Assert.Equal(
            "the-stored-secret",
            Unprotect(
                new Dictionary<string, string?> { [MeisterDataProtectionExtensions.KeysPathKey] = keysPath },
                payload));
    }

    [Fact]
    public void AProtectorNameNothingProvidesFailsTheSetUp()
    {
        var settings = new Dictionary<string, string?>
        {
            [MeisterDataProtectionExtensions.KeysPathKey] = this.MakeDirectory("keys"),
            [MeisterDataProtectionExtensions.ProtectorKey] = "hardware-security-module",
            [MeisterDataProtectionExtensions.AddInDirectoryKey] = this.MakeDirectory("add-ins"),
        };

        var failure = Assert.Throws<InvalidOperationException>(() => Compose(settings));

        Assert.Contains(MeisterDataProtectionExtensions.ProtectorKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains("hardware-security-module", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCertificateProtectorWithNoCertificateConfiguredFailsTheSetUp()
    {
        var settings = new Dictionary<string, string?>
        {
            [MeisterDataProtectionExtensions.KeysPathKey] = this.MakeDirectory("keys"),
            [MeisterDataProtectionExtensions.ProtectorKey] = CertificateKeyRingProtector.ProtectorName,
        };

        var failure = Assert.Throws<InvalidOperationException>(() => Compose(settings));

        Assert.Contains(
            CertificateKeyRingProtector.CertificatePathKey,
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ACertificateFileThatIsNotThereFailsTheSetUp()
    {
        var settings = CertificateSettings(
            this.MakeDirectory("keys"),
            Path.Combine(this._root, "absent.pfx"));

        var failure = Assert.Throws<InvalidOperationException>(() => Compose(settings));

        Assert.Contains("absent.pfx", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>What one host protects, another composed the same way reads: one protection identity.</summary>
    [Fact]
    public void TwoHostsComposedFromTheSameSettingsReadEachOthersSecrets()
    {
        var settings = CertificateSettings(this.MakeDirectory("keys"), this.WriteCertificate("shared"));

        using var first = Compose(settings);
        using var second = Compose(settings);

        var payload = first.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose)
            .Protect("the-stored-secret");

        Assert.Equal(
            "the-stored-secret",
            second.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(payload));
    }

    private static ServiceProvider Compose(IDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddMeisterDataProtection(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        return services.BuildServiceProvider();
    }

    private static string Protect(IDictionary<string, string?> settings, string value)
    {
        using var host = Compose(settings);

        return host.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect(value);
    }

    private static string Unprotect(IDictionary<string, string?> settings, string payload)
    {
        using var host = Compose(settings);

        return host.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(payload);
    }

    // Two certificates are listed by one delimiter on every platform, so the same value means the same thing
    // wherever the host runs.
    [Fact]
    public void EveryListedPreviousCertificateReadsWhatItProtected()
    {
        var keysPath = this.MakeDirectory("keys");
        var firstCertificate = this.WriteCertificate("first");
        var secondCertificate = this.WriteCertificate("second");
        var firstPayload = Protect(CertificateSettings(this.MakeDirectory("keys-first"), firstCertificate), "first-secret");
        var secondPayload = Protect(CertificateSettings(this.MakeDirectory("keys-second"), secondCertificate), "second-secret");

        var current = CertificateSettings(keysPath, this.WriteCertificate("current"));
        current[CertificateKeyRingProtector.PreviousCertificatePathsKey] =
            $"{firstCertificate};{secondCertificate}";

        Assert.Equal("first-secret", Unprotect(WithKeys(current, this.MakeDirectory("keys-first")), firstPayload));
        Assert.Equal("second-secret", Unprotect(WithKeys(current, this.MakeDirectory("keys-second")), secondPayload));
    }

    // A path holding a comma is one path: it does not separate the list, and the certificate at it still
    // reads what it protected.
    [Fact]
    public void APreviousCertificatePathHoldingACommaIsOnePath()
    {
        var certificatePath = Path.Combine(this._root, "previous,certificate.pfx");
        File.Move(this.WriteCertificate("previous"), certificatePath);
        var keysPath = this.MakeDirectory("keys");
        var payload = Protect(CertificateSettings(keysPath, certificatePath), "the-stored-secret");

        var rotated = CertificateSettings(keysPath, this.WriteCertificate("current"));
        rotated[CertificateKeyRingProtector.PreviousCertificatePathsKey] = certificatePath;

        Assert.Equal("the-stored-secret", Unprotect(rotated, payload));
    }

    // Neither a comma nor a colon separates the list. The colon cases are read from the value and not written
    // to disk: a file name carrying one cannot be created on Windows, while a path carrying one, such as a
    // drive letter, is the ordinary case there.
    [Theory]
    [InlineData("/srv/propr/previous,certificate.pfx")]
    [InlineData(@"C:\propr\previous.pfx")]
    [InlineData("/srv/propr/previous:certificate.pfx")]
    public void APathHoldingACommaOrAColonIsOnePath(string path)
    {
        Assert.Equal([path], CertificateKeyRingProtector.SplitPaths(path));
    }

    [Fact]
    public void UpgradedCompositionReadsSecretsFromALegacyRelativeKeyPath()
    {
        var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), this.MakeDirectory("legacy-keys "));
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName(MeisterDataProtectionExtensions.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(relativePath));
        using var legacyHost = services.BuildServiceProvider();
        var payload = legacyHost.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose).Protect("legacy-secret");

        Assert.Equal("legacy-secret", Unprotect(new Dictionary<string, string?> { [MeisterDataProtectionExtensions.KeysPathKey] = relativePath }, payload));
    }

    [Fact]
    public void AConfiguredKeyPathPreservesWhitespace()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [MeisterDataProtectionExtensions.KeysPathKey] = "  keys  ",
                })
            .Build();

        Assert.Equal(
            "  keys  ",
            MeisterDataProtectionExtensions.ResolveKeysPath(configuration));
    }

    [Fact]
    public void AnAbsoluteKeyPathIsTakenAsItIs()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [MeisterDataProtectionExtensions.KeysPathKey] = $" {this._root} ",
                })
            .Build();

        Assert.Equal($" {this._root} ", MeisterDataProtectionExtensions.ResolveKeysPath(configuration));
    }

    /// <summary>The settings with the key directory replaced.</summary>
    /// <param name="settings">The settings to copy.</param>
    /// <param name="keysPath">The key directory the copy names.</param>
    private static Dictionary<string, string?> WithKeys(IDictionary<string, string?> settings, string keysPath)
    {
        return new Dictionary<string, string?>(settings)
        {
            [MeisterDataProtectionExtensions.KeysPathKey] = keysPath,
        };
    }

    private static Dictionary<string, string?> CertificateSettings(string keysPath, string certificatePath)
    {
        return new Dictionary<string, string?>
        {
            [MeisterDataProtectionExtensions.KeysPathKey] = keysPath,
            [MeisterDataProtectionExtensions.ProtectorKey] = CertificateKeyRingProtector.ProtectorName,
            [CertificateKeyRingProtector.CertificatePathKey] = certificatePath,
            [CertificateKeyRingProtector.CertificatePasswordKey] = CertificatePassword,
        };
    }

    private string MakeDirectory(string name)
    {
        var path = Path.Combine(this._root, name);
        Directory.CreateDirectory(path);

        return path;
    }

    /// <summary>Writes a certificate an operator would hold, as a password-protected PKCS#12 file.</summary>
    /// <param name="name">What the file is called, and the subject of the certificate in it.</param>
    private string WriteCertificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        var path = Path.Combine(this._root, name + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, CertificatePassword));

        return path;
    }
}
