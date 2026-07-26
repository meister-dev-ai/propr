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
    private const string Secret = "sk-must-never-be-logged";

    public static TheoryData<string, object> CredentialBearingValues()
    {
        return new TheoryData<string, object>
        {
            { nameof(AiConnectionAuthRequest), new AiConnectionAuthRequest(AiAuthMode.ApiKey, Secret) },
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

    // Interpolated into a message, Serilog uses ToString — which is why the types override it. A transform does
    // nothing for this path.
    [Theory]
    [MemberData(nameof(CredentialBearingValues))]
    public void RenderingACredentialBearingValueAsTextDoesNotEmitTheSecret(string label, object value)
    {
        Assert.DoesNotContain(Secret, value.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(label, label, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProfileSentToTheApiCallerCarriesNoSecret()
    {
        var json = JsonSerializer.Serialize(Connection());

        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        // The rest of the profile is still there, so this is redaction rather than an empty response.
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
            AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            AiAuthMode.ApiKey,
            DefaultHeaders: new Dictionary<string, string> { ["Authorization"] = $"Bearer {Secret}" },
            DefaultQueryParams: new Dictionary<string, string> { ["api-key"] = Secret });

        logger.Information("probing {@Endpoint} rendered as {Endpoint}", endpoint, endpoint);

        Assert.DoesNotContain(Secret, sink.Rendered(), StringComparison.Ordinal);
    }

    private static CreateAiConnectionRequest CreateRequest()
    {
        return new CreateAiConnectionRequest(
            "Primary DeepSeek",
            AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            new AiConnectionAuthRequest(AiAuthMode.ApiKey, Secret));
    }

    private static DiscoverModelsRequest Discover()
    {
        return new DiscoverModelsRequest(
            AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            new AiConnectionAuthRequest(AiAuthMode.ApiKey, Secret));
    }

    private static ProbeAiConnectionRequest Probe()
    {
        return new ProbeAiConnectionRequest(
            AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            new AiConnectionAuthRequest(AiAuthMode.ApiKey, Secret));
    }

    private static AiConnectionDto Connection()
    {
        return new AiConnectionDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Primary DeepSeek",
            AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            AiAuthMode.ApiKey,
            AiDiscoveryMode.ManualOnly,
            true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            Secret);
    }

    private static AiConnectionWriteRequestDto WriteRequest()
    {
        return new AiConnectionWriteRequestDto(
            "Primary DeepSeek",
            AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            AiAuthMode.ApiKey,
            AiDiscoveryMode.ManualOnly,
            [],
            [],
            Secret: Secret);
    }

    private static AiConnectionProbeOptionsDto ProbeOptions()
    {
        return new AiConnectionProbeOptionsDto(
            AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            AiAuthMode.ApiKey,
            Secret);
    }

    private static ProviderEndpoint Endpoint()
    {
        return new ProviderEndpoint(
            AiProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            AiAuthMode.ApiKey,
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
