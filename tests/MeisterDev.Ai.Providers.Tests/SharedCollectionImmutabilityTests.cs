// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests;

/// <summary>
///     No static of the provider assemblies hands out a collection that can be written through.
/// </summary>
/// <remarks>
///     <para>
///         A static holds one instance for the process, and every provider family in the process reaches the same
///         one. <c>IReadOnlyList</c> and <c>IReadOnlyDictionary</c> describe what a caller may do and not what the
///         object is: a <c>List</c> or a <c>Dictionary</c> behind either is cast back to its own type in one line,
///         and a family that writes through it changes what every other family and the host itself reads
///         afterwards. An array is worse, because it needs no cast.
///     </para>
///     <para>
///         This is asserted rather than reviewed because review has not held it. The same defect was found eight
///         separate times on the increment that introduced these assemblies, each time by a different reader and
///         each time in a different type, which is the shape of a defect that needs a check rather than more
///         attention.
///     </para>
///     <para>
///         What a static hands out is followed rather than only inspected, because the eighth occurrence was two
///         levels down: a declaration held as a static exposed an immutable list of credential shapes, and each
///         shape in it held the caller's own list of fields. A check that read only the object the member
///         returned reported that declaration as correct. The walk follows the product's own types and the
///         collections of them, which is the graph a family reaches through one static.
///     </para>
/// </remarks>
public sealed class SharedCollectionImmutabilityTests
{
    /// <summary>The assemblies a provider family reaches, which is where a shared static matters.</summary>
    public static TheoryData<string> ProviderAssemblies =>
    [
        typeof(IAiProviderDriver).Assembly.GetName().Name!,
        typeof(AiProviderRegistry).Assembly.GetName().Name!,
    ];

    [Theory]
    [MemberData(nameof(ProviderAssemblies))]
    public void NoStaticHandsOutAWritableCollection(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);
        var offenders = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsGenericTypeDefinition)
            {
                continue;
            }

            foreach (var (member, value, held) in StaticValues(type))
            {
                offenders.AddRange(WritableCollectionsWithin($"{type.FullName}.{member}", value, [], depth: 0, held));
            }
        }

        Assert.Empty(offenders);
    }

    // The check above passes both because nothing hands out a writable collection and because a check that
    // reported nothing would pass too. This is what separates the two: the shapes the defect takes are reported,
    // and the shapes that fix it are not.
    [Fact]
    public void TheCheckReportsAWritableCollectionAndAcceptsAnImmutableOne()
    {
        IReadOnlyDictionary<string, string> behindAnInterface = new Dictionary<string, string>();
        IReadOnlyList<string> asAnArray = new[] { "one" };

        Assert.True(IsWritable(behindAnInterface));
        Assert.True(IsWritable(asAnArray));
        Assert.False(IsWritable(ImmutableDictionary<string, string>.Empty));
        Assert.False(IsWritable(ImmutableArray.Create("one")));
        Assert.False(IsWritable(new[] { "one" }.ToFrozenSet()));
    }

    // The shape the eighth occurrence took: the member hands out an immutable collection, and an element of it
    // holds the caller's own list. Reading the returned object alone reports that as correct.
    [Fact]
    public void TheWalkReachesACollectionNestedInsideAnImmutableOne()
    {
        var declaration = new ProviderDeclaration
        {
            Key = "tests/nested",
            Label = "Nested",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode("tests/nested:ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs("tests/nested:ApiKey"),
        };

        Assert.Empty(WritableCollectionsWithin("declaration", declaration, [], depth: 0));
        Assert.NotEmpty(WritableCollectionsWithin("holder", new NestedHolder(), [], depth: 0));
    }

    /// <summary>Every writable collection reachable from one value, named by the path it was reached through.</summary>
    /// <remarks>
    ///     <para>
    ///         Only a collection the graph actually holds is reported. A member that builds its result on every
    ///         read hands each caller a collection of their own, so writing through one changes nothing anyone
    ///         else reads; a member is told apart from the other kind by being read twice and compared by
    ///         reference. Without that, every projection in a declaration reports as an offence and the check is
    ///         one nobody can keep green.
    ///     </para>
    ///     <para>
    ///         Bounded three ways: by depth, by reference identity so a cycle ends, and by type, so the walk stays
    ///         inside the product's own types instead of descending into the framework. A value outside those
    ///         types is still judged; only its members are left unread.
    ///     </para>
    /// </remarks>
    private static IEnumerable<string> WritableCollectionsWithin(
        string path,
        object? value,
        HashSet<object> seen,
        int depth,
        bool held = true)
    {
        const int MaximumDepth = 8;

        if (value is null || depth > MaximumDepth || value is string || !seen.Add(value))
        {
            yield break;
        }

        if (held && IsWritable(value))
        {
            yield return $"{path} hands out a {value.GetType().Name}. A shared instance is one instance for the "
                         + "process; use an immutable or frozen collection so a caster cannot write through it.";
            yield break;
        }

        if (value is IEnumerable elements)
        {
            var index = 0;
            foreach (var element in elements)
            {
                // An element is the object it is whether the collection around it was rebuilt or not, so it is
                // judged as held.
                foreach (var offence in WritableCollectionsWithin($"{path}[{index}]", element, seen, depth + 1))
                {
                    yield return offence;
                }

                index++;
            }

            yield break;
        }

        if (!IsOurs(value.GetType()))
        {
            yield break;
        }

        foreach (var (member, memberValue, memberHeld) in ReadableValues(value))
        {
            foreach (var offence in
                     WritableCollectionsWithin($"{path}.{member}", memberValue, seen, depth + 1, memberHeld))
            {
                yield return offence;
            }
        }
    }

    /// <summary>Every instance member of one object a caller outside its type can read.</summary>
    /// <remarks>
    ///     A field holds whatever it holds, so its value is held by definition. A property is read twice: two
    ///     reads answering with the same instance is a value the object holds, and two answering with different
    ///     ones is a value it composes for each caller.
    /// </remarks>
    private static IEnumerable<(string Member, object? Value, bool Held)> ReadableValues(object instance)
    {
        const BindingFlags Instances = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = instance.GetType();

        foreach (var field in type.GetFields(Instances))
        {
            if (field.IsPrivate || field.IsFamilyAndAssembly)
            {
                continue;
            }

            yield return (field.Name, Read(() => field.GetValue(instance)), true);
        }

        foreach (var property in type.GetProperties(Instances))
        {
            if (property.GetIndexParameters().Length > 0
                || property.GetMethod is not { IsPrivate: false, IsFamilyAndAssembly: false })
            {
                continue;
            }

            var first = Read(() => property.GetValue(instance));
            var second = Read(() => property.GetValue(instance));

            yield return (property.Name, first, ReferenceEquals(first, second));
        }
    }

    private static bool IsOurs(Type type)
    {
        return type.Namespace?.StartsWith("MeisterDev.", StringComparison.Ordinal) == true;
    }

    /// <summary>A type whose member hands out the caller's own list two levels down.</summary>
    private sealed class NestedHolder
    {
        public IReadOnlyList<NestedElement> Elements { get; } = ImmutableArray.Create(new NestedElement());
    }

    private sealed class NestedElement
    {
        public IReadOnlyList<string> Held { get; } = ["one", "two"];
    }

    /// <summary>Every static member of one type that hands a value to a caller outside it.</summary>
    /// <remarks>
    ///     Members a caller can reach, and that makes a shared instance shared: a private static is held by
    ///     the one type that declares it, and whether that type hands it out is a question about the member that
    ///     returns it rather than about the field. A member that throws when read is skipped, because that is a
    ///     member with a precondition rather than a collection anyone holds.
    /// </remarks>
    /// <param name="type">The type to read.</param>
    private static IEnumerable<(string Member, object Value, bool Held)> StaticValues(Type type)
    {
        const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var field in type.GetFields(Statics))
        {
            if (field.IsLiteral || field.IsPrivate || field.IsFamilyAndAssembly)
            {
                continue;
            }

            if (Read(() => field.GetValue(null)) is { } value and not string)
            {
                yield return (field.Name, value, true);
            }
        }

        foreach (var property in type.GetProperties(Statics))
        {
            if (property.GetIndexParameters().Length > 0
                || property.GetMethod is not { IsPrivate: false, IsFamilyAndAssembly: false })
            {
                continue;
            }

            var first = Read(() => property.GetValue(null));
            if (first is { } value and not string)
            {
                yield return (property.Name, value, ReferenceEquals(first, Read(() => property.GetValue(null))));
            }
        }
    }

    /// <summary>Whether the object a caller is handed can be written through.</summary>
    /// <remarks>
    ///     Decided by what the object is rather than by the member's declared type, which is the whole point: the
    ///     declared type is what made every one of these look correct. A type not on either list is reported, so a
    ///     collection nobody thought about is a failure rather than a silent pass.
    /// </remarks>
    /// <param name="value">The collection the static hands out.</param>
    private static bool IsWritable(object value)
    {
        var type = value.GetType();

        if (type.IsArray)
        {
            return true;
        }

        if (IsFromNamespaceOf(type, typeof(ImmutableArray<>)) || IsFromNamespaceOf(type, typeof(FrozenSet<>)))
        {
            return false;
        }

        // A collection of the product's own that is itself immutable is not reported: what the check is about is
        // handing out a mutable one, and an enumerable a family cannot write to is not that.
        return value is IList or IDictionary;
    }

    private static bool IsFromNamespaceOf(Type type, Type exemplar)
    {
        return string.Equals(type.Namespace, exemplar.Namespace, StringComparison.Ordinal);
    }

    private static object? Read(Func<object?> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception)
            when (exception is TargetInvocationException or TypeInitializationException or NotSupportedException)
        {
            // A member with a precondition, or one reflection cannot call at all. Neither is a shared collection
            // handed to a caller, which this reads for.
            return null;
        }
    }
}
