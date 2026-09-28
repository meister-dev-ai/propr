// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterDev.DataProtection.Tests;

/// <summary>
///     A protector supplied from a directory, read the way a deployment holds one.
/// </summary>
public sealed class KeyRingProtectorAddInTests : IDisposable
{
    /// <summary>The setting the example add-in reports as missing, which only it reads.</summary>
    private const string ExampleRequiredSettingKey = "MEISTER_DATA_PROTECTION_EXAMPLE_SETTING";

    private readonly string _stagingRoot = Path.Combine(
        Path.GetTempPath(),
        "data-protection-add-ins-" + Guid.NewGuid().ToString("N"));

    /// <summary>Where the build stages the example add-in, a folder per add-in named after the assembly in it.</summary>
    private static string AddInDirectory =>
        Path.Combine(AppContext.BaseDirectory, "add-in-builds", "MeisterDev.DataProtection.ExampleAddIn");

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(this._stagingRoot))
        {
            Directory.Delete(this._stagingRoot, true);
        }
    }

    [Fact]
    public void AProtectorInTheAddInDirectoryIsSelectableByName()
    {
        var protector = KeyRingProtectorCatalog.Find("example", AddInDirectory);

        Assert.NotNull(protector);
        Assert.Equal("example", protector.Name);
    }

    [Fact]
    public void AProtectorFromAnAddInImplementsTheHostsOwnContract()
    {
        var protector = KeyRingProtectorCatalog.Find("example", AddInDirectory);

        // Loaded into its own context, and still the interface this host looks for: the contract assembly comes
        // from the default context, so the type crossing the boundary is one type and not two.
        var loaded = Assert.IsAssignableFrom<IKeyRingProtector>(protector);
        var implemented = Assert.Single(
            loaded.GetType().GetInterfaces(),
            candidate => candidate.FullName == typeof(IKeyRingProtector).FullName);

        Assert.Same(typeof(IKeyRingProtector).Assembly, implemented.Assembly);
        Assert.NotSame(typeof(IKeyRingProtector).Assembly, loaded.GetType().Assembly);
    }

    [Fact]
    public void AnAddInProtectorIsAppliedByTheSharedSetUp()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [MeisterDataProtectionExtensions.ProtectorKey] = "example",
                    [MeisterDataProtectionExtensions.AddInDirectoryKey] = AddInDirectory,
                })
            .Build();

        // The add-in reports the setting it needs, and the report reaches the host as the start-up failure a
        // compiled-in protector's would.
        var failure = Assert.Throws<InvalidOperationException>(() => services.AddMeisterDataProtection(configuration));

        Assert.Contains("MEISTER_DATA_PROTECTION_EXAMPLE_SETTING", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddInDirectoryThatIsNotThereLeavesTheNameUnprovided()
    {
        Assert.Null(KeyRingProtectorCatalog.Find("example", Path.Combine(AppContext.BaseDirectory, "no-such-directory")));
    }

    // The add-in directory holds a protector claiming the compiled-in name, so a directory an operator can
    // write to is shown not to take over a name the product ships.
    [Fact]
    public void ACompiledInProtectorAnswersBeforeTheDirectoryIsRead()
    {
        var protector = KeyRingProtectorCatalog.Find(CertificateKeyRingProtector.ProtectorName, AddInDirectory);

        Assert.IsType<CertificateKeyRingProtector>(protector);
        Assert.Same(typeof(IKeyRingProtector).Assembly, protector.GetType().Assembly);
    }

    // The add-in's one call against the builder decides how the key ring is written, so what a host composed
    // with it differs from what it composes with no protector at all.
    [Fact]
    public void TheSelectedAddInDecidesHowTheKeyRingIsEncrypted()
    {
        var services = new ServiceCollection();
        services.AddMeisterDataProtection(
            Configuration(
                new Dictionary<string, string?>
                {
                    [MeisterDataProtectionExtensions.ProtectorKey] = "example",
                    [MeisterDataProtectionExtensions.AddInDirectoryKey] = AddInDirectory,
                    [ExampleRequiredSettingKey] = "anything",
                }));

        using var provider = services.BuildServiceProvider();
        var encryptor = provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor;

        Assert.NotNull(encryptor);
        Assert.Equal(AddInDirectory, Path.GetDirectoryName(encryptor.GetType().Assembly.Location));

        // What this add-in's encryptor does and no other does: it hands the key element back as it was and
        // names its own decryptor to read it, so the encryptor in the composed host is the one it installed.
        var encrypted = encryptor.Encrypt(new XElement("key", new XElement("descriptor", "the-key-material")));

        Assert.Equal(
            "MeisterDev.DataProtection.ExampleAddIn.ExampleXmlDecryptor",
            encrypted.DecryptorType.FullName);
        Assert.Equal("the-key-material", encrypted.EncryptedElement.Value);
    }

    [Fact]
    public void WithNoProtectorSelectedTheKeyRingIsWrittenUnencrypted()
    {
        var services = new ServiceCollection();
        services.AddMeisterDataProtection(Configuration(new Dictionary<string, string?>()));

        using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor);
    }

    // One unusable file in a mounted directory is the normal case: the directory holds whatever a deployment
    // put there, and a protector in another file still has to be found.
    [Fact]
    public void AFileThatIsNotAnAssemblyIsPassedOver()
    {
        var directory = this.StageAddInCopies(1);
        File.WriteAllText(Path.Combine(directory, "broken.dll"), "not an assembly");

        Assert.NotNull(KeyRingProtectorCatalog.Find("example", directory));
    }

    // A protector whose constructor throws is one unusable add-in, and the scan continues past it: the example
    // add-in carries such a type beside the one this looks for.
    [Fact]
    public void AProtectorThatCannotBeCreatedDoesNotStopTheScan()
    {
        Assert.NotNull(KeyRingProtectorCatalog.Find("example", AddInDirectory));
    }

    // Which of two add-ins claiming one name a host would run is decided by where the files sit, so start-up
    // stops instead of picking one.
    [Fact]
    public void TwoAddInsProvidingOneNameFailTheLookupAndNameThem()
    {
        var directory = this.StageAddInCopies(2);

        var failure = Assert.Throws<InvalidOperationException>(() => KeyRingProtectorCatalog.Find("example", directory));

        Assert.Contains("example", failure.Message, StringComparison.Ordinal);

        // Both files are named, so an operator can remove one of them without looking for them first.
        Assert.Contains(Path.Combine(directory, "add-in-0"), failure.Message, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(directory, "add-in-1"), failure.Message, StringComparison.Ordinal);
    }

    // Which of two types of one add-in claiming a name a host would run is decided by the order reflection
    // reports them in, so the lookup stops instead of picking one. The example add-in carries such a pair.
    [Fact]
    public void TwoTypesOfOneAddInProvidingOneNameFailTheLookupAndNameThem()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => KeyRingProtectorCatalog.Find("twinned", AddInDirectory));

        Assert.Contains("twinned", failure.Message, StringComparison.Ordinal);
        Assert.Contains("FirstTwinnedKeyRingProtector", failure.Message, StringComparison.Ordinal);
        Assert.Contains("SecondTwinnedKeyRingProtector", failure.Message, StringComparison.Ordinal);
    }

    // The name a second type of the same add-in claims is its own. Every other name the add-in provides stays
    // selectable, so one author's mistake costs that name and not the add-in.
    [Fact]
    public void AnAddInClaimingOneNameTwiceStillProvidesItsOtherNames()
    {
        Assert.NotNull(KeyRingProtectorCatalog.Find("example", AddInDirectory));
    }

    // A filesystem answers a pattern's casing differently on Windows and on Linux, so the extension is
    // matched instead: the same directory holds the same add-in on every host.
    [Fact]
    public void AnAssemblyWhoseExtensionIsCapitalisedIsStillAnAddIn()
    {
        var directory = this.StageAddInCopies(1);
        var staged = Path.Combine(directory, "add-in-0");
        const string assemblyName = "MeisterDev.DataProtection.ExampleAddIn";
        File.Move(
            Path.Combine(staged, assemblyName + ".dll"),
            Path.Combine(staged, assemblyName + ".DLL"));

        Assert.NotNull(KeyRingProtectorCatalog.Find("example", directory));
    }

    // A link below the add-in directory leads wherever whoever created it chose, and that is not what the
    // operator configured as the directory add-ins are read from.
    [Fact]
    public void ADirectoryLinkIsNotFollowedOutOfTheAddInDirectory()
    {
        var linked = Path.Combine(this.StageAddInCopies(1), "add-in-0");
        var root = Path.Combine(this._stagingRoot, "add-in-root");
        Directory.CreateDirectory(root);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), linked);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Creating a directory link needs a privilege Windows does not grant a test run by default, and
            // there is nothing to assert about a link the platform refused to make.
            return;
        }

        Assert.Null(KeyRingProtectorCatalog.Find("example", root));

        // The same files are an add-in where they are the directory itself, so the scan passed over the link
        // and not over the add-in.
        Assert.NotNull(KeyRingProtectorCatalog.Find("example", linked));
    }

    // A file link in the add-in directory names an assembly wherever whoever created it chose, so loading it
    // would run code from a file the operator never put in the directory. The path stays inside the root, so
    // the check the subdirectories are held to says nothing about it.
    [Fact]
    public void AnAssemblyLinkIsNotLoadedFromOutsideTheAddInDirectory()
    {
        const string assemblyName = "MeisterDev.DataProtection.ExampleAddIn.dll";
        var staged = Path.Combine(this.StageAddInCopies(1), "add-in-0");
        var root = Path.Combine(this._stagingRoot, "linked-assembly-root");
        Directory.CreateDirectory(root);

        // Everything the add-in needs beside its assembly is copied, so a scan that followed the link would
        // find the protector and the link is what the scan has to refuse.
        foreach (var file in Directory.EnumerateFiles(staged).Where(file => Path.GetFileName(file) != assemblyName))
        {
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        }

        try
        {
            File.CreateSymbolicLink(Path.Combine(root, assemblyName), Path.Combine(staged, assemblyName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Creating a link needs a privilege Windows does not grant a test run by default, and there is
            // nothing to assert about a link the platform refused to make.
            return;
        }

        Assert.Null(KeyRingProtectorCatalog.Find("example", root));

        // The same file is an add-in where the scan reaches it directly, so the lookup above passed over the
        // link and not over the add-in.
        Assert.NotNull(KeyRingProtectorCatalog.Find("example", staged));
    }

    /// <summary>A directory holding <paramref name="copies" /> copies of the example add-in, one per folder.</summary>
    /// <param name="copies">How many copies to stage.</param>
    private string StageAddInCopies(int copies)
    {
        var root = Path.Combine(this._stagingRoot, Guid.NewGuid().ToString("N"));
        for (var copy = 0; copy < copies; copy++)
        {
            var target = Path.Combine(root, $"add-in-{copy}");
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(AddInDirectory))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }

        return root;
    }

    private static IConfiguration Configuration(IDictionary<string, string?> settings)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
