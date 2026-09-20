// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Startup;

/// <summary>
///     What the composed host does with the add-in directory an operator configured. A host that started with a
///     broken add-in silently and a host that refused to start because of one are both wrong: it starts, serves
///     everything that loaded, and records what it skipped.
/// </summary>
public sealed class ProviderAddInStartupTests : IDisposable
{
    private const string CompiledInApiKey = "example/provider:ApiKey";

    private const string CompiledInChatCompletions = "example/provider:ChatCompletions";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "propr-add-in-startup",
        Guid.NewGuid().ToString("n"));

    public ProviderAddInStartupTests()
    {
        Directory.CreateDirectory(this._directory);
    }

    [Fact]
    public void AnUnreadableFileInTheDirectoryDoesNotStopTheHostStarting()
    {
        var corrupt = this.PutCorruptAssembly("corrupt");

        using var factory = new ApiHostFactory(this._directory);
        _ = factory.CreateClient();

        // Listed as found and not activatable, rather than loaded and skipped: nothing in the external
        // directory is loaded until an administrator activates it, and a file that is not an assembly says so
        // where they would be deciding about it.
        var catalog = factory.Services.GetRequiredService<ProviderAddInCatalog>();
        var found = Assert.Single(catalog.Awaiting);
        Assert.Equal(corrupt, found.FilePath);
        Assert.False(found.CanBeActivated);
        Assert.Contains("assembly", found.Refusal!, StringComparison.OrdinalIgnoreCase);

        // Passing over the corrupt file instead of failing the load keeps every family the built-in directory
        // declares in service.
        var registry = factory.Services.GetRequiredService<IAiProviderDriverRegistry>();
        Assert.Contains("meisterdev/openAi", registry.RegisteredKinds);
    }

    // The example add-in states the identity a driver compiled into this host also claims. The registry refuses
    // two drivers for one identity by throwing, so without the host deciding this first, one file in a mounted
    // directory would stop it starting.
    [Fact]
    public void AnAddInClaimingAnIdentityTheHostAlreadyHoldsIsSkippedAndTheHostStarts()
    {
        var addIn = this.InstallExampleAddIn();

        using var factory = new ApiHostFactory(this._directory, ClaimingTheExampleIdentity());
        _ = factory.CreateClient();

        // Told before the decision rather than after a load: the add-in states its identity in its assembly
        // attribute, so the host can say the identity is taken without running any of it.
        var catalog = factory.Services.GetRequiredService<ProviderAddInCatalog>();
        var found = Assert.Single(catalog.Awaiting);
        Assert.Equal(addIn, found.FilePath);
        Assert.False(found.CanBeActivated);
        Assert.Contains("example/provider", found.Refusal!, StringComparison.Ordinal);
        Assert.DoesNotContain(catalog.Loaded, loaded => loaded.FilePath == addIn);

        var registry = factory.Services.GetRequiredService<IAiProviderDriverRegistry>();
        Assert.Contains("example/provider", registry.RegisteredKinds);
    }

    // One pass. The directory is read while the host starts and never again, so what the inventory reports and
    // what the host serves do not change when the directory does.
    [Fact]
    public void AFileAddedAfterStartupChangesNothingUntilTheHostRestarts()
    {
        using var factory = new ApiHostFactory(this._directory);
        _ = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<ProviderAddInCatalog>();
        Assert.Empty(catalog.Rejected);

        this.PutCorruptAssembly("late");

        Assert.Same(catalog, factory.Services.GetRequiredService<ProviderAddInCatalog>());
        Assert.Empty(factory.Services.GetRequiredService<ProviderAddInCatalog>().Rejected);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(this._directory, recursive: true);
        }
        catch (IOException)
        {
            // A loaded assembly can keep its file open, and a directory left in the temporary path is not worth
            // failing a test over.
        }
    }

    private string PutCorruptAssembly(string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(this._directory, name));
        var path = Path.Combine(folder.FullName, name + ".dll");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x01]);

        return path;
    }

    private string InstallExampleAddIn()
    {
        const string Name = "MeisterDev.Ai.Providers.ExampleAddIn";
        var source = Path.Combine(AppContext.BaseDirectory, "add-in-builds", Name);
        var folder = Directory.CreateDirectory(Path.Combine(this._directory, Name));

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(folder.FullName, Path.GetFileName(file)), overwrite: true);
        }

        return Path.Combine(folder.FullName, Name + ".dll");
    }

    /// <summary>The API host as a test composes it, pointed at one add-in directory.</summary>
    // A driver compiled into this host claiming the identity the example add-in declares. Stated here rather
    // than by referencing the add-in's own type: the host loads that assembly from a directory, which 
    // the add-in path is, so this project holds no reference to it.
    private static IAiProviderDriver ClaimingTheExampleIdentity()
    {
        var declaration = new ProviderDeclaration
        {
            Key = "example/provider",
            Label = "Compiled into the host",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode(CompiledInApiKey, [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto, CompiledInChatCompletions]),
            ConformanceInputs = new ProviderConformanceInputs(CompiledInApiKey),
        };

        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(declaration);
        driver.SupportedAuthModes.Returns(declaration.SupportedAuthModes);
        driver.SupportedProtocolModes.Returns(declaration.ProtocolModes.Supported);
        driver.CredentialFields.Returns(declaration.CredentialFields);

        return driver;
    }

    private sealed class ApiHostFactory(string addInDirectory, IAiProviderDriver? hostDriver = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (hostDriver is not null)
            {
                builder.ConfigureServices(services => services.AddSingleton(hostDriver));
            }

            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", "test-provider-add-in-secret-32chars--");
            builder.UseSetting(ProviderAddInDirectories.ExternalDirectoryKey, addInDirectory);
        }
    }
}
