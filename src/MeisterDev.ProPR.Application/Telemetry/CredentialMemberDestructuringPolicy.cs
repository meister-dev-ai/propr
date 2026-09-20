// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using MeisterDev.Ai.Providers.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace MeisterDev.ProPR.Application.Telemetry;

/// <summary>
///     Elides a credential member of any object destructured into a log event, whatever its type.
/// </summary>
/// <remarks>
///     <para>
///         The transforms beside this one are named per type, and that lets each say what else to omit. What
///         they cannot say is anything about a type nobody listed, and a list is a list of the types someone
///         remembered: a record added later carries its credential into the first <c>{@value}</c> that mentions
///         it, and nothing about that call site looks wrong.
///     </para>
///     <para>
///         This is the floor under them. A member named the way credential material is named — the set
///         <see cref="SecretSafeRendering.HostCredentialMemberNames" /> states, which the shared driver checks
///         also judge a provider family by — is written as an elision and every other member as it stands.
///         Registered last, so a type with a transform of its own keeps it.
///     </para>
///     <para>
///         The floor is deliberately weaker than a transform. It writes members a named transform would omit,
///         such as a settings document a provider family filled in, so a type carrying something the host cannot
///         judge still wants one of those. What it guarantees is the part nobody should have to remember.
///     </para>
/// </remarks>
public sealed class CredentialMemberDestructuringPolicy : IDestructuringPolicy
{
    /// <summary>What a credential member is written as.</summary>
    private const string Elision = "[redacted]";

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ReadableProperties = new();

    /// <inheritdoc />
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(propertyValueFactory);

        result = null;

        var properties = ReadableProperties.GetOrAdd(value.GetType(), Readable);
        if (!Array.Exists(properties, property => IsCredential(property.Name)))
        {
            return false;
        }

        var written = new List<LogEventProperty>(properties.Length);
        foreach (var property in properties)
        {
            if (IsCredential(property.Name))
            {
                written.Add(new LogEventProperty(property.Name, new ScalarValue(Elision)));
                continue;
            }

            // A property whose getter throws says nothing about the object and must not stop the rest being
            // written: the alternative is an exception raised from inside the logging of an exception.
            object? read;
            try
            {
                read = property.GetValue(value);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                continue;
            }

            written.Add(
                new LogEventProperty(
                    property.Name,
                    propertyValueFactory.CreatePropertyValue(read, destructureObjects: true)));
        }

        result = new StructureValue(written, value.GetType().Name);
        return true;
    }

    private static bool IsCredential(string name)
    {
        return SecretSafeRendering.HostCredentialMemberNames.Contains(name);
    }

    // Indexers are left out: reading one needs an argument, and a type that has one holds its members elsewhere.
    private static PropertyInfo[] Readable(Type type)
    {
        return
        [
            .. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
        ];
    }
}
