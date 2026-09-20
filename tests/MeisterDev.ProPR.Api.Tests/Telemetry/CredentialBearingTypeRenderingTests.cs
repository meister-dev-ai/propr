// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Runtime.CompilerServices;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.ProPR.Api.Telemetry;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Options;
using MeisterDev.ProPR.Licensing;
using MeisterDev.ProPR.Runner.Contracts;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeisterDev.ProPR.Api.Tests.Telemetry;

/// <summary>
///     Every type of the product's own that carries a credential renders without it.
/// </summary>
/// <remarks>
///     <para>
///         This is the leak that needs no misconfiguration and no logging setup: a record's generated rendering
///         prints every member, so one ordinary interpolation writes the credential and nothing about the call
///         site looks wrong.
///     </para>
///     <para>
///         The types are found by reflection rather than listed. A list is a list of the types someone
///         remembered, so a type added later is protected only if the person adding it also edits the list, and
///         the leak this exists for is one nobody notices. The same property is asserted for a provider family's
///         own types by the shared driver checks, under the same member names.
///     </para>
/// </remarks>
public sealed class CredentialBearingTypeRenderingTests
{
    /// <summary>The value written into a credential member, looked for in what the type renders.</summary>
    private const string Sentinel = "credential-sentinel-value";

    /// <summary>
    ///     The assemblies the product's own types live in, named by one type each so the set is a set of
    ///     assemblies and never a set of types.
    /// </summary>
    private static readonly Assembly[] ProductAssemblies =
    [
        typeof(ProviderEndpoint).Assembly,
        typeof(ProviderSecretEnvelope).Assembly,
        typeof(AiConnection).Assembly,
        typeof(AiConnectionDto).Assembly,
        typeof(AiConnectionRecord).Assembly,
        typeof(SecretLogRedaction).Assembly,
        typeof(LicenseClaims).Assembly,
        typeof(RunnerJobManifest).Assembly,
        typeof(AiEvaluatorOptions).Assembly,
    ];

    [Fact]
    public void NoCredentialBearingTypeRendersItsCredential()
    {
        var leaks = new List<string>();

        foreach (var type in ProductAssemblies.SelectMany(TypesOf))
        {
            if (CredentialMemberOf(type) is not { } member)
            {
                continue;
            }

            if (RendersEveryMember(type))
            {
                leaks.Add(
                    $"'{type.FullName}' carries a credential in '{member}' and relies on the rendering the "
                    + "compiler generates for a record, which prints every member.");
            }
            else if (ConstructedWithASecret(type) is { } instance && Renders(instance, Sentinel))
            {
                leaks.Add($"'{type.FullName}' renders the value of '{member}'.");
            }
        }

        // Every one of them, because they are found by reading the product's own types and a reader who fixed the
        // first would otherwise come back for the next.
        Assert.True(leaks.Count == 0, string.Join(Environment.NewLine, leaks));
    }

    // The other half of the same leak. Serilog consults ToString only for {Value}; for {@Value} it reflects over
    // the properties and never calls it, so a type whose own rendering is safe is still written whole. The
    // transforms in SecretLogRedaction are what close that path, and they are a list someone maintains, which is
    // the shape the remark above warns about. So the list is checked against the types rather than trusted.
    [Fact]
    public void NoCredentialBearingTypeIsDestructuredWithItsCredential()
    {
        var leaks = new List<string>();

        foreach (var type in ProductAssemblies.SelectMany(TypesOf))
        {
            if (CredentialMemberOf(type) is not { } member || ConstructedWithASecret(type) is not { } instance)
            {
                continue;
            }

            if (Destructures(instance, Sentinel))
            {
                leaks.Add($"'{type.FullName}' is destructured with the value of '{member}'.");
            }
        }

        Assert.True(leaks.Count == 0, string.Join(Environment.NewLine, leaks));
    }

    // The check above has to be able to see a value that was written, or it passes by not looking. A member
    // under no credential name is covered by nothing, so its value reaches the event and the harness finds it.
    [Fact]
    public void AValueTheHarnessShouldSeeIsSeen()
    {
        Assert.True(Destructures(new TokenizerNamed(Sentinel), Sentinel));
    }

    // And the floor is what keeps the same value out when the member is named as credential material, whether or
    // not the type has a transform of its own.
    [Fact]
    public void ATypeNoTransformNamesIsStillDestructuredWithoutItsCredential()
    {
        Assert.False(Destructures(new GeneratedRenderingCredential(Sentinel, "https://api.example.com"), Sentinel));
    }

    private static bool Destructures(object instance, string value)
    {
        var sink = new CapturingSink();
        using var logger = SecretLogRedaction.Apply(new LoggerConfiguration().MinimumLevel.Verbose())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("configuring {@Value}", instance);

        return sink.Rendered().Contains(value, StringComparison.Ordinal);
    }

    // The rule is what it is because the two cases differ. A record synthesizes a rendering that prints every
    // member; a class that declares none inherits object's, which prints the type name and nothing else.
    [Fact]
    public void ARecordCarryingACredentialIsCaughtAndAClassDeclaringNoRenderingIsNot()
    {
        Assert.True(RendersEveryMember(typeof(GeneratedRenderingCredential)));
        Assert.False(RendersEveryMember(typeof(PlainClassCredential)));
    }

    [Fact]
    public void ARenderingThatPrintsTheCredentialIsCaught()
    {
        var instance = ConstructedWithASecret(typeof(HandWrittenRenderingCredential));

        Assert.NotNull(instance);
        Assert.True(Renders(instance, Sentinel));
    }

    [Fact]
    public void ARenderingThatElidesTheCredentialPasses()
    {
        var instance = ConstructedWithASecret(typeof(ElidingRenderingCredential));

        Assert.NotNull(instance);
        Assert.False(Renders(instance, Sentinel));
    }

    [Fact]
    public void AMemberUnderNoCredentialNameIsNotCredentialBearing()
    {
        Assert.Null(CredentialMemberOf(typeof(TokenizerNamed)));
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            // An assembly holding a type that cannot be loaded is still checked on the types that can.
            types = partial.Types;
        }

        return types
            .Where(type => type is { IsClass: true } or { IsValueType: true })
            .Select(type => type!)
            .Where(type => !type.IsEnum
                           && !type.ContainsGenericParameters
                           && !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false));
    }

    /// <summary>The member holding credential material, or null for a type that holds none.</summary>
    /// <param name="type">The type to read.</param>
    /// <remarks>
    ///     Inherited members count. A record deriving from a record synthesizes its own rendering, which prints
    ///     the base's members too, and a positional parameter that matches an inherited property declares no
    ///     property of its own — so a type that looked to declare nothing would be skipped while rendering the
    ///     credential it inherited.
    /// </remarks>
    private static string? CredentialMemberOf(Type type)
    {
        const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        foreach (var property in type.GetProperties(Declared))
        {
            if (property.PropertyType == typeof(string)
                && SecretSafeRendering.HostCredentialMemberNames.Contains(property.Name))
            {
                return property.Name;
            }
        }

        foreach (var field in type.GetFields(Declared))
        {
            if (field.FieldType == typeof(string)
                && SecretSafeRendering.HostCredentialMemberNames.Contains(field.Name))
            {
                return field.Name;
            }
        }

        return null;
    }

    /// <summary>
    ///     Whether the rendering this type would actually use prints every member, which a record's
    ///     generated one does.
    /// </summary>
    /// <param name="type">The type to read.</param>
    /// <remarks>
    ///     The whole chain is walked, because a record deriving from a record declares no rendering of its own
    ///     and still gets one that prints its members. A type that reaches <see cref="object" /> without finding
    ///     a declaration renders as its own name and shows nothing it holds.
    /// </remarks>
    private static bool RendersEveryMember(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var rendering = current.GetMethod(
                nameof(ToString),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);

            if (rendering is not null)
            {
                return rendering.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);
            }
        }

        return false;
    }

    /// <summary>
    ///     The type with its credential set to a value that can be looked for, or null for a type this check
    ///     cannot construct.
    /// </summary>
    /// <param name="type">The type to construct.</param>
    /// <remarks>
    ///     Only from a constructor whose every parameter is a string, a value type, or carries a default, so
    ///     constructing it runs no work of the type's beyond the constructor itself. A value type is supplied as
    ///     its own zero: a type whose first parameter is an enum would otherwise be skipped, and a credential
    ///     record beside an enumerated kind is an ordinary shape.
    /// </remarks>
    private static object? ConstructedWithASecret(Type type)
    {
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = constructor.GetParameters();
            if (parameters.Any(parameter => !Suppliable(parameter)))
            {
                continue;
            }

            var arguments = parameters.Select(Argument).ToArray();

            try
            {
                return constructor.Invoke(arguments);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A constructor that refuses these values has its own rules about them. The type is still covered
                // by the rule above, which needs no instance.
                return null;
            }
        }

        return null;
    }

    private static bool Suppliable(ParameterInfo parameter)
    {
        return parameter.ParameterType == typeof(string)
               || parameter.HasDefaultValue
               || (parameter.ParameterType.IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) is null);
    }

    private static object? Argument(ParameterInfo parameter)
    {
        if (parameter.ParameterType == typeof(string))
        {
            return SecretSafeRendering.HostCredentialMemberNames.Contains(parameter.Name ?? string.Empty)
                ? Sentinel
                : "visible";
        }

        return parameter.HasDefaultValue ? parameter.DefaultValue : Activator.CreateInstance(parameter.ParameterType);
    }

    private static bool Renders(object instance, string value)
    {
        try
        {
            return instance.ToString()?.Contains(value, StringComparison.Ordinal) == true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A rendering that throws prints nothing, so it leaks nothing.
            return false;
        }
    }

    /// <summary>A credential-bearing type relying on the rendering the compiler generates for a record.</summary>
    private sealed record GeneratedRenderingCredential(string ApiKey, string Endpoint);

    /// <summary>A credential-bearing class declaring no rendering, so it renders as its own name.</summary>
    private sealed class PlainClassCredential
    {
        public string? ApiKey { get; set; }
    }

    /// <summary>A credential-bearing type whose own rendering prints the credential.</summary>
    private sealed class HandWrittenRenderingCredential(string apiKey)
    {
        public string ApiKey { get; } = apiKey;

        public override string ToString()
        {
            return $"Credential {{ ApiKey = {this.ApiKey} }}";
        }
    }

    /// <summary>A credential-bearing type whose own rendering elides the credential.</summary>
    private sealed class ElidingRenderingCredential(string apiKey)
    {
        public string ApiKey { get; } = apiKey;

        public override string ToString()
        {
            return $"Credential {{ ApiKey = {SecretSafeRendering.Elide(this.ApiKey)} }}";
        }
    }

    /// <summary>A type whose member merely contains a credential name inside a longer word.</summary>
    private sealed record TokenizerNamed(string TokenizerName);

    /// <summary>Keeps every event so the whole rendered output can be scanned, properties included.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public void Emit(LogEvent logEvent)
        {
            this._events.Add(logEvent);
        }

        public string Rendered()
        {
            using var writer = new StringWriter();
            foreach (var logEvent in this._events)
            {
                logEvent.RenderMessage(writer);
                foreach (var property in logEvent.Properties)
                {
                    writer.Write($" {property.Key}={property.Value}");
                }
            }

            return writer.ToString();
        }
    }
}
