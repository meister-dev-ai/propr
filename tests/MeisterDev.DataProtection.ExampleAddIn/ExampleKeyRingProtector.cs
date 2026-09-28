// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.DataProtection.ExampleAddIn;

/// <summary>
///     A protector supplied the way a deployment supplies one, for the discovery path to be exercised from the
///     position an add-in author is in.
/// </summary>
/// <remarks>
///     It protects nothing: the encryptor it installs writes the key element back unchanged. What it stands
///     for is the contract: a name an installation selects, and one call against the builder that a host cannot
///     distinguish from a compiled-in protector's. Because that call replaces the key-ring encryptor, a host
///     that composed this protector can be told apart from one that composed none.
/// </remarks>
public sealed class ExampleKeyRingProtector : IKeyRingProtector
{
    /// <summary>The variable this protector reports as missing when it is applied without it.</summary>
    public const string RequiredSettingKey = "MEISTER_DATA_PROTECTION_EXAMPLE_SETTING";

    /// <inheritdoc />
    public string Name => "example";

    /// <inheritdoc />
    public void Apply(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(configuration[RequiredSettingKey]))
        {
            throw new InvalidOperationException($"The 'example' key-ring protector requires {RequiredSettingKey}.");
        }

        builder.Services.Configure<KeyManagementOptions>(options => options.XmlEncryptor = new ExampleXmlEncryptor());
    }
}

/// <summary>The key-ring encryptor this add-in installs, which returns the key element unchanged.</summary>
public sealed class ExampleXmlEncryptor : IXmlEncryptor
{
    /// <inheritdoc />
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        return new EncryptedXmlInfo(new XElement(plaintextElement), typeof(ExampleXmlDecryptor));
    }
}

/// <summary>Reads back what <see cref="ExampleXmlEncryptor" /> wrote.</summary>
public sealed class ExampleXmlDecryptor : IXmlDecryptor
{
    /// <inheritdoc />
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        return new XElement(encryptedElement);
    }
}
