// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.ExampleAddIn;

namespace MeisterDev.Ai.Providers.Tests;

/// <summary>
///     Guards what makes the contract assembly usable by a provider family built outside this repository: it
///     reaches nothing of the host or the shared runtime, and everything the driver contract names is in it.
/// </summary>
/// <remarks>
///     A family that referenced the shared runtime would take the decorator stages, the guarded egress handler,
///     the secret envelope, the catalog and six vendor SDKs with it. The contract has no compatibility guarantee
///     either, so a type that has to be in it and is not costs a rebuild of every family to add later. Both are
///     one convenient using directive away, so they are asserted rather than trusted.
/// </remarks>
public sealed class ContractIsolationTests
{
    /// <summary>
    ///     Package assemblies that must not reach the contract. Each is a vendor SDK, a web framework or an
    ///     object-relational mapper, and the contract's purpose is that a family takes none of them from here.
    /// </summary>
    private static readonly string[] ForbiddenReferencePrefixes =
    [
        "MeisterDev.",
        "Azure.",
        "AWSSDK.",
        "Google.",
        "OpenAI",
        "System.ClientModel",
        "Microsoft.AspNetCore.",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
    ];

    private static Assembly ContractAssembly => typeof(IAiProviderDriver).Assembly;

    private static Assembly SharedRuntimeAssembly => typeof(AiProviderRegistry).Assembly;

    [Fact]
    public void TheContractIsItsOwnAssembly()
    {
        Assert.Equal("MeisterDev.Ai.Providers.Abstractions", ContractAssembly.GetName().Name);
        Assert.NotSame(SharedRuntimeAssembly, ContractAssembly);
    }

    // Over the whole closure, not the direct references alone. A family takes everything the contract drags in,
    // so a vendor SDK or an object-relational mapper reached through the one package the contract does take
    // would arrive with it while a check of the direct list said nothing.
    [Fact]
    public void TheContractReferencesNothingAFamilyCannotTake()
    {
        var forbidden = ReachableFrom(ContractAssembly)
            .Where(name => ForbiddenReferencePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(forbidden);
    }

    // Microsoft.Extensions.AI.Abstractions is the one package the contract takes, because IChatClient and
    // IEmbeddingGenerator are in the driver's own signatures. It is named here so adding a second package is a
    // decision someone makes rather than one that slips in.
    [Fact]
    public void TheContractTakesTheModelAbstractionsAndNoOtherPackage()
    {
        var packages = ContractAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => !name.StartsWith("System.", StringComparison.Ordinal)
                           && !string.Equals(name, "System", StringComparison.Ordinal)
                           && !string.Equals(name, "netstandard", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["Microsoft.Extensions.AI.Abstractions"], packages);
    }

    [Fact]
    public void TheContractExposesEveryPublicTypeUnderTheProviderNamespace()
    {
        var strays = ContractAssembly
            .GetExportedTypes()
            .Where(type => !(type.Namespace ?? string.Empty).StartsWith("MeisterDev.Ai.Providers", StringComparison.Ordinal))
            .Select(type => type.FullName ?? type.Name)
            .ToList();

        Assert.Empty(strays);
    }

    // A driver implements this interface from its own assembly, so every type in its members' signatures has to
    // be one the driver can name. A signature type left behind in the shared runtime would make the interface
    // unimplementable from outside, which is the failure this assembly exists to remove.
    [Fact]
    public void EveryTypeTheDriverContractNamesComesFromTheContractOrTheModelAbstractions()
    {
        var allowed = new[] { ContractAssembly, typeof(Microsoft.Extensions.AI.IChatClient).Assembly, typeof(object).Assembly };

        var stranded = new List<string>();
        foreach (var member in typeof(IAiProviderDriver).GetMembers())
        {
            if (SignatureTypes(member) is not { } signature)
            {
                // A member kind this check cannot read is a member whose types nothing looked at, which for
                // whatever it names is the same as having no check at all.
                stranded.Add($"{member.Name}: this check does not read a {member.MemberType} member");
                continue;
            }

            foreach (var part in signature.SelectMany(Unwrap))
            {
                if (!part.IsGenericParameter && !allowed.Contains(part.Assembly))
                {
                    stranded.Add($"{member.Name}: {part.FullName} in {part.Assembly.GetName().Name}");
                }
            }
        }

        Assert.Empty(stranded);
    }

    // The point of the split, stated as a fact about a built assembly: a family compiled against the contract
    // alone carries no reference to the shared runtime or to any host project.
    [Fact]
    public void AFamilyBuiltAgainstTheContractReferencesNeitherTheSharedRuntimeNorTheHost()
    {
        var references = typeof(ExampleProviderDriver).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("MeisterDev.", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["MeisterDev.Ai.Providers.Abstractions"], references);
    }

    // Type identity, not shape: a family that resolved its own copy of the contract would declare a driver the
    // host cannot assign to its own interface, and nothing about the build would say so.
    [Fact]
    public void AFamilyBuiltAgainstTheContractImplementsTheSameInterfaceTheHostResolves()
    {
        Assert.True(typeof(IAiProviderDriver).IsAssignableFrom(typeof(ExampleProviderDriver)));

        var implemented = typeof(ExampleProviderDriver)
            .GetInterfaces()
            .Single(contract => contract.Name == nameof(IAiProviderDriver));

        Assert.Same(typeof(IAiProviderDriver), implemented);
    }

    // Every member of the declaration is reachable from a project that has the contract and nothing else, and
    // every one of them can be set. A member that could only be populated with a type from the shared runtime
    // would be a member an out-of-tree family cannot use.
    [Fact]
    public void AFamilyBuiltAgainstTheContractCanPopulateEveryMemberOfTheDeclaration()
    {
        var declaration = new ExampleProviderDriver().Declaration;

        Assert.Equal("example/provider", declaration.Key);
        Assert.NotEmpty(declaration.Label);
        Assert.NotEmpty(declaration.Version);
        Assert.Equal(ProviderContract.Version, declaration.ContractVersion);
        Assert.NotEmpty(declaration.LegacyNames.Keys);
        Assert.NotEmpty(declaration.LegacyNames.AuthModeSpellings);
        Assert.NotEmpty(declaration.LegacyNames.ProtocolModeSpellings);
        Assert.NotEmpty(declaration.Fields);
        Assert.NotEmpty(declaration.RequestShapeDefaults.RefusedParameters);
        Assert.False(declaration.RequestShapeDefaults.AcceptsTemperature);
        Assert.NotEmpty(declaration.Actions);
        Assert.NotEmpty(declaration.AuthModes);
        Assert.NotEmpty(declaration.ProtocolModes.Supported);
        Assert.True(declaration.RequiresBrowserCoLocation);
        Assert.NotEmpty(declaration.ReachedHostPatterns);
        Assert.Equal("example-connections", declaration.RequiredCapabilityKey);
        Assert.NotEmpty(declaration.ConformanceInputs.CredentialAuthMode);
        Assert.NotEmpty(declaration.ConformanceInputs.RecordedUsagePayload);
        Assert.NotNull(declaration.InvocationWindow);
        Assert.True(declaration.HasCredentialHealth);
        Assert.True(declaration.OpensListener);
    }

    // The closed field vocabulary has to cover what a form needs, so a family that declares a computed field, a
    // conditional one and an action input can say all three without the host learning a rule language.
    [Fact]
    public void TheDeclaredFieldVocabularyCoversWhatACredentialFormNeeds()
    {
        var fields = new ExampleProviderDriver().Declaration.Fields;

        Assert.Contains(fields, field => field.Kind == ProviderFieldKind.Url && field.IsRequired);
        Assert.Contains(fields, field => field.Kind == ProviderFieldKind.Int && field.DefaultValue == "1455");
        Assert.Contains(fields, field => field.IsComputed && field.VisibleWhen is not null);
        Assert.Contains(fields, field => field.Kind == ProviderFieldKind.Choice && field.Choices.Count > 0);
        Assert.Contains(fields, field => field.Scope == ProviderFieldScope.ActionInput);

        // Connection configuration and action inputs are told apart by scope, so a transient value is never
        // among the values a host would persist.
        Assert.DoesNotContain(
            new ExampleProviderDriver().Declaration.ConnectionFields,
            field => field.Scope == ProviderFieldScope.ActionInput);
    }

    /// <summary>Every type one member's signature names, or null for a member kind this check cannot read.</summary>
    /// <param name="member">The interface member.</param>
    private static IReadOnlyList<Type>? SignatureTypes(MemberInfo member)
    {
        return member switch
        {
            PropertyInfo property => [property.PropertyType],
            MethodInfo method => [method.ReturnType, .. method.GetParameters().Select(parameter => parameter.ParameterType)],
            EventInfo declaredEvent => [declaredEvent.EventHandlerType!],
            FieldInfo field => [field.FieldType],
            Type nested => [nested],
            _ => null,
        };
    }

    /// <summary>
    ///     Every type a signature names, with by-ref, array and generic wrapping taken off all the way down. A
    ///     walk that stopped at the immediate arguments would read the outer dictionary and its two arguments and
    ///     stop, so a host-only type one level further in would never be looked at.
    /// </summary>
    /// <param name="type">The type as the signature declares it.</param>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        var root = type.IsByRef || type.IsArray || type.IsPointer ? type.GetElementType()! : type;

        if (!root.IsGenericType)
        {
            yield return root;
            yield break;
        }

        yield return root.GetGenericTypeDefinition();
        foreach (var argument in root.GenericTypeArguments.SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    /// <summary>
    ///     The names of every assembly reachable from <paramref name="root" />, directly or through another.
    /// </summary>
    /// <remarks>
    ///     An assembly that cannot be loaded here is still reported by name; only its own references are
    ///     unknowable, which is the most this can say without shipping a resolver of its own.
    /// </remarks>
    /// <param name="root">The assembly to walk from.</param>
    private static IReadOnlyCollection<string> ReachableFrom(Assembly root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            foreach (var reference in pending.Dequeue().GetReferencedAssemblies())
            {
                if (!seen.Add(reference.Name ?? string.Empty))
                {
                    continue;
                }

                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
                catch (Exception exception)
                    when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // Not resolvable from this test's context; its name is recorded above either way.
                }
            }
        }

        return seen;
    }
}
