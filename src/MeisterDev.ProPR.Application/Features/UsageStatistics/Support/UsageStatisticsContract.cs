// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Support;

/// <summary>
///     The wire contract: the address a snapshot is posted to and how it is serialized.
///     <para>
///         The payload preview is produced by serializing the same snapshot with the same options, so it
///         matches the payload the sender produces.
///     </para>
/// </summary>
public static class UsageStatisticsContract
{
    /// <summary>The wire schema this build sends and understands.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The receiver a release build posts to.</summary>
    public const string DefaultPingEndpoint = "https://telemetry.meister-dev.ai/v1/ping";

    /// <summary>The assembly metadata key a build writes an alternative endpoint under.</summary>
    internal const string EndpointMetadataKey = "UsageStatisticsEndpoint";

    /// <summary>Where the payload fields are documented.</summary>
    public const string PayloadDocumentationUrl =
        "https://github.com/meister-dev-ai/propr/blob/main/docs/reference/usage-statistics.md";

    /// <summary>The contact address for privacy questions about the payload.</summary>
    public const string PrivacyContact = "privacy@meister-dev.ai";

    /// <summary>
    ///     The receiver this build posts to.
    ///     <para>
    ///         Fixed when the assembly is compiled, from the <c>UsageStatisticsEndpoint</c> build property, and
    ///         the production address when nothing set it. It is not configurable at run time, so an
    ///         installation cannot be redirected or silenced by an environment variable and the destination is
    ///         a property of the image. The administration page and the payload preview show the same value the
    ///         sender uses.
    ///     </para>
    /// </summary>
    public static readonly string PingEndpoint = ResolvePingEndpoint(typeof(UsageStatisticsContract).Assembly);

    /// <summary>How the snapshot is serialized, on the wire and in the preview.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>Serializes a snapshot exactly as it would be sent.</summary>
    public static string Serialize(UsageStatisticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    /// <summary>
    ///     Reads the endpoint compiled into <paramref name="assembly" />.
    ///     <para>
    ///         A value that is present but not an absolute <c>http</c> or <c>https</c> address stops the
    ///         application from starting rather than falling back to the default. A fallback would send a
    ///         staging installation's snapshots to the production receiver after a typo in the build property.
    ///     </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The build compiled in an endpoint that cannot be used.</exception>
    internal static string ResolvePingEndpoint(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var configured = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, EndpointMetadataKey, StringComparison.Ordinal))
            ?.Value;

        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultPingEndpoint;
        }

        var endpoint = configured.Trim();

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"This build was compiled with UsageStatisticsEndpoint='{endpoint}', which is not an absolute "
                + "http or https address. Rebuild with a usable endpoint, or with none to use the default.");
        }

        return endpoint;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // Every property name is set by an attribute on the snapshot, so no naming policy applies and
            // renaming a C# property does not rename a documented wire field.
            PropertyNamingPolicy = null,
            WriteIndented = false,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
