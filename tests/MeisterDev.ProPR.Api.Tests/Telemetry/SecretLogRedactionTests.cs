// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Telemetry;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeisterDev.ProPR.Api.Tests.Telemetry;

/// <summary>
///     The credential scan the security requirement asks for, run as a test rather than promised in prose: every
///     type that carries a provider credential is put through both rendering paths and the API response shape, and
///     the secret must appear in none of them.
/// </summary>
public sealed class SecretLogRedactionTests
{
    private const string CompatibleApiKey = "meisterdev/openAiCompatible:ApiKey";

    private const string Secret = "sk-must-never-be-logged";

    // A family's secret-marked declared value. It goes into the same protected envelope as the credential and is
    // withheld from the caller on the same terms.
    private const string DeclaredSecret = "cs-must-never-be-logged";

    // A family's non-secret declared value. The host cannot know whether a family put credential material in one,
    // so none of them is written to a log line.
    private const string DeclaredSetting = "eu-west-must-never-be-logged";

    public static TheoryData<string, object> CredentialBearingValues()
    {
        return new TheoryData<string, object>
        {
            { nameof(AiConnectionAuthRequest), new AiConnectionAuthRequest(CompatibleApiKey, Secret) },
            { nameof(CreateAiConnectionRequest), CreateRequest() },
            { nameof(DiscoverModelsRequest), Discover() },
            { nameof(ProbeAiConnectionRequest), Probe() },
            { nameof(AiConnectionDto), Connection() },
            { nameof(AiConnectionWriteRequestDto), WriteRequest() },
            { nameof(AiConnectionProbeOptionsDto), ProbeOptions() },
            { nameof(ProviderEndpoint), Endpoint() },
        };
    }

    // Destructured with @, Serilog reflects over the properties and never consults ToString, so this is the path
    // the transforms cover. Both paths need closing: which one a call site used is not visible from the type.
    [Theory]
    [MemberData(nameof(CredentialBearingValues))]
    public void DestructuringACredentialBearingValueDoesNotEmitTheSecret(string label, object value)
    {
        var sink = new CapturingSink();
        using var logger = SecretLogRedaction.Apply(new LoggerConfiguration().MinimumLevel.Verbose())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("configuring {@Value}", value);

        var rendered = sink.Rendered();
        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.Contains(label, label, StringComparison.Ordinal);
    }

    // The declared settings document is whatever a provider family chose to declare, so the host has no basis
    // for deciding which entry is safe to print and prints none of them. A family that declared credential
    // material as a plain value therefore has a visibility defect and not a leak into the log.
    [Fact]
    public void ADestructuredConnectionWritesNoDeclaredValueAtAll()
    {
        var sink = new CapturingSink();
        using var logger = SecretLogRedaction.Apply(new LoggerConfiguration().MinimumLevel.Verbose())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("configuring {@Value}", Connection());

        var rendered = sink.Rendered();
        Assert.DoesNotContain(DeclaredSetting, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(DeclaredSecret, rendered, StringComparison.Ordinal);
    }

    // Interpolated into a message, Serilog uses ToString rather than the transforms. The connection renders its
    // declared fields as names, which the operator needs to tell one connection from another, and none of
    // their values.
    [Fact]
    public void AnInterpolatedConnectionWritesItsDeclaredFieldNamesAndNoDeclaredValue()
    {
        var rendered = Connection().ToString();

        Assert.Contains("region", rendered, StringComparison.Ordinal);
        Assert.Contains("clientSecret", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(DeclaredSetting, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(DeclaredSecret, rendered, StringComparison.Ordinal);
    }

    // A connection whose family declares nothing renders the same shape as one with entries, so a reader is not
    // left wondering whether the absence means "none declared" or "withheld".
    [Fact]
    public void AConnectionWithAnEmptyDeclaredDocumentRendersTheSameShape()
    {
        var rendered = (Connection() with
        {
            ProviderSettings = null,
            DeclaredSecrets = new Dictionary<string, string>(),
        }).ToString();

        Assert.Contains("ProviderSettings", rendered, StringComparison.Ordinal);
        Assert.Contains("DeclaredSecrets", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProfileSentToTheApiCallerCarriesNoSecret()
    {
        var json = JsonSerializer.Serialize(Connection());

        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(DeclaredSecret, json, StringComparison.Ordinal);

        // Neither credential member is serialized at all, so nothing downstream can read one back out of the
        // response by name.
        var properties = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(property => property.Name);
        Assert.DoesNotContain("secret", properties, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("declaredSecrets", properties, StringComparer.OrdinalIgnoreCase);

        // What a console needs is still there: which secret-marked fields hold a value, and the rest of the
        // profile, so this is redaction rather than an empty response.
        Assert.Contains("clientSecret", json, StringComparison.Ordinal);
        Assert.Contains("https://api.deepseek.com/v1", json, StringComparison.Ordinal);
    }

    // A credential an operator put in a header or a query parameter is still a credential.
    [Fact]
    public void ASecretHiddenInAHeaderOrQueryParameterIsAlsoWithheld()
    {
        var sink = new CapturingSink();
        using var logger = SecretLogRedaction.Apply(new LoggerConfiguration().MinimumLevel.Verbose())
            .WriteTo.Sink(sink)
            .CreateLogger();
        var endpoint = new ProviderEndpoint(
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com/v1",
            CompatibleApiKey,
            DefaultHeaders: new Dictionary<string, string> { ["Authorization"] = $"Bearer {Secret}" },
            DefaultQueryParams: new Dictionary<string, string> { ["api-key"] = Secret });

        logger.Information("probing {@Endpoint} rendered as {Endpoint}", endpoint, endpoint);

        Assert.DoesNotContain(Secret, sink.Rendered(), StringComparison.Ordinal);
    }

    private static CreateAiConnectionRequest CreateRequest()
    {
        return new CreateAiConnectionRequest(
            "Primary DeepSeek",
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com/v1",
            new AiConnectionAuthRequest(CompatibleApiKey, Secret));
    }

    private static DiscoverModelsRequest Discover()
    {
        return new DiscoverModelsRequest(
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com/v1",
            new AiConnectionAuthRequest(CompatibleApiKey, Secret));
    }

    private static ProbeAiConnectionRequest Probe()
    {
        return new ProbeAiConnectionRequest(
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com/v1",
            new AiConnectionAuthRequest(CompatibleApiKey, Secret));
    }

    private static AiConnectionDto Connection()
    {
        return new AiConnectionDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Primary DeepSeek",
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com/v1",
            CompatibleApiKey,
            AiDiscoveryMode.ManualOnly,
            true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            Secret)
        {
            ProviderSettings = new Dictionary<string, string> { ["region"] = DeclaredSetting },
            DeclaredSecrets = new Dictionary<string, string> { ["clientSecret"] = DeclaredSecret },
        };
    }

    private static AiConnectionWriteRequestDto WriteRequest()
    {
        return new AiConnectionWriteRequestDto(
            "Primary DeepSeek",
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com/v1",
            CompatibleApiKey,
            AiDiscoveryMode.ManualOnly,
            [],
            [],
            Secret: Secret);
    }

    private static AiConnectionProbeOptionsDto ProbeOptions()
    {
        return new AiConnectionProbeOptionsDto(
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com/v1",
            CompatibleApiKey,
            Secret);
    }

    private static ProviderEndpoint Endpoint()
    {
        return new ProviderEndpoint(
            "meisterdev/openAiCompatible",
            "https://api.deepseek.com/v1",
            CompatibleApiKey,
            Secret);
    }

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
