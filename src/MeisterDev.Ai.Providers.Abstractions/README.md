# MeisterDev.Ai.Providers.Abstractions

The contract a ProPR provider family is written against. A provider family references this assembly and nothing
else from the ProPR repository. It carries the driver contract, the declaration a family supplies, the results
an action may return, the host primitives a family may call, and the two host-owned value types a family has to
name.

The contract has no compatibility guarantee. Every family built against it has to be rebuilt when it changes.

## Packaging an add-in

`dotnet publish` copies referenced assemblies into a project's output. Three must not end up there: this one,
`Microsoft.Extensions.AI.Abstractions`, and `MeisterDev.Ai.Providers.Conformance`. An add-in whose folder holds
a copy of one of them loads against that copy instead of the host's, and **nothing reports a failure**. The
assembly loads, the contract-version check passes, the add-in's driver type implements a different
`IAiProviderDriver` than the host's, and the family is absent from the registry while the inventory says nothing
failed.

Three settings prevent it.

| Setting | Where | What it does |
| --- | --- | --- |
| `<EnableDynamicLoading>true</EnableDynamicLoading>` | the add-in project | Produces the `.deps.json` the host's loader reads to resolve the add-in's own dependencies from the add-in's folder. |
| `<Private>false</Private>` | every project reference to one of the three | Compiles against it without copying it into the output. |
| removal from the copy and publish lists | every reference to one of the three, package or project | Compiles against it and leaves it out of the output and publish folder. |

The shared build configuration in `build/` applies all three. NuGet imports it, so an add-in that takes this
package as a package needs no further edit. An add-in built inside the ProPR repository imports the same two
files by path:

```xml
<Import Project="..\..\src\MeisterDev.Ai.Providers.Abstractions\build\MeisterDev.Ai.Providers.Abstractions.props"/>
...
<Import Project="..\..\src\MeisterDev.Ai.Providers.Abstractions\build\MeisterDev.Ai.Providers.Abstractions.targets"/>
```

The `.props` file sets the property, which the SDK reads while the project is still being evaluated. The
`.targets` file acts on the references, which the project has not declared at `.props` time.

The third setting removes the three assemblies from the resolved copy and publish lists during the build. It
does not put `ExcludeAssets` on the package reference, because NuGet reads `ExcludeAssets` during restore. When
the contract arrives as a package, these two build files come from inside that package, and the package is on
disk only once restore has finished. An add-in built outside the ProPR repository would restore without the
exclusion and copy both shared assemblies into its output. Removing them during the build holds whichever way
the contract was referenced.

NuGet imports the configuration into every project that references this package, and not every such project is
an add-in. A test project for an add-in, or a library factored out of one, is built and run as an ordinary
project and needs the shared assemblies in its own output. Such a project sets
`<ProviderAddInPackaging>false</ProviderAddInPackaging>`, and the configuration then contributes none of the
three settings and neither check.

The configuration fails the build with `PROPRADDIN001` when one of the three is found in the add-in's output or
publish folder, naming which one. `ProviderAddInPackagingCheck` set to `false` suppresses that error and keeps
the three settings. The add-in stays unloadable; only the message goes away.

## The Microsoft.Extensions.AI version

`IChatClient`, `ChatMessage` and `IEmbeddingGenerator` appear in the driver contract's own signatures, so the
host and every add-in resolve one copy of `Microsoft.Extensions.AI.Abstractions` from the host's default load
context. An add-in is pinned to the host's version of that package. `Directory.Build.props` at the repository
root holds the version the host runs; an add-in built elsewhere sets the same version on its own package
reference. Moving the host to a new version means rebuilding every add-in.

## Running the driver checks

`MeisterDev.Ai.Providers.Conformance` holds the checks every provider family has to pass. They read what your
family declares about itself, and replay the usage payload it recorded through its own mapping. None of them
reaches the network or runs your driver against an endpoint.

The host runs the same checks against a family it loads, so running them in your own build tells you what the
host would say before you deploy.

Reference the package from a test project for your add-in, which sets
`<ProviderAddInPackaging>false</ProviderAddInPackaging>` because it needs the shared assemblies in its own
output, and call the kit:

```csharp
var report = DriverConformance.Run(
    new ConformanceSubject(new AcmeProviderDriver())
    {
        UsageMapping = AcmeUsage.FromVendorPayload,
    });

Assert.True(report.Passed, report.Summary);
```

Supply the driver. Every check takes what it needs from your family's declaration.
`ProviderDeclaration.ConformanceInputs` states the parts no check can derive: the authentication mode to
present, and a usage payload recorded from your vendor.

`UsageMapping` is the one value that comes from you, because no host can reach it. It is your family's
translation of its vendor's usage counts onto `ProviderTokenUsage`. The check replays the recorded payload
through it and asserts the input total covers both cache buckets. The host bills the input total less the two
cache buckets, floored at zero, so a vendor reporting input exclusive of them has to be normalized in the
mapping. Without that the whole prompt bills at nothing. Leave the mapping out and the check reports itself as
one that did not run.

## Being activated

An add-in in the external directory is discovered but not loaded. A platform administrator sees it on the
add-ins page with its file path, its SHA-256 and the identity, version and reached hosts read out of its
assembly attribute, and activates it there. Nothing in your assembly runs before that.

Declare the attribute once, at assembly level:

```csharp
[assembly: ProviderAddIn(
    Key = "acme/gateway",
    Label = "Acme Gateway",
    Version = "1.2.0",
    ContractVersion = ProviderContract.Version,
    ReachedHosts = [".acme.ai"],
    RequiredCapability = null)]
```

The host reads it without executing anything, so it has to agree with what your driver's `Declaration` says.
Activation compares the two and refuses an add-in whose attribute and declaration disagree, because the
administrator approved what the attribute stated.

Activation is bound to the content hash. Replacing the file revokes it, and the administrator activates the new
version after seeing what changed.

Families the deployment image carries in its own add-in directory need no activation: their bytes ship with the
host, and replacing one means replacing a shipped binary.
