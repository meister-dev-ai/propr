// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Runner;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     What the runner host writes about the connection and endpoint objects it handles.
/// </summary>
/// <remarks>
///     The runner relays every model call and holds provider endpoints, so a destructured rendering of one is a
///     credential on this side as much as on the control plane's. These cover the runner's own logger rather than
///     the redaction policy on its own, because the gap they exist for was a host that had the policy available
///     and never applied it.
/// </remarks>
public sealed class RunnerLoggingTests
{
    private const string Secret = "sk-must-never-be-logged";

    [Fact]
    public void AProviderEndpointDestructuredIntoALogLineShowsNoCredential()
    {
        var rendered = Log(
            new ProviderEndpoint(
                "meisterdev/openAiCompatible",
                "https://api.example.com/v1",
                "meisterdev/openAiCompatible:ApiKey",
                Secret));

        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void AConnectionDestructuredIntoALogLineShowsItsFieldNamesAndNoCredential()
    {
        var rendered = Log(Connection());

        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.Contains("BaseUrl", rendered, StringComparison.Ordinal);
        Assert.Contains("AuthMode", rendered, StringComparison.Ordinal);
    }

    // The declared settings document is family-defined, so nothing in it is projected: a family that put
    // credential material in a plain field has a visibility defect and not a leak into the runner's log.
    [Fact]
    public void TheDeclaredValuesOfAConnectionAreNotWrittenAtAll()
    {
        var rendered = Log(Connection());

        Assert.DoesNotContain("region-eu-west", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("cs-declared-secret", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOperatorsLogLevelStillDecidesWhatIsWritten()
    {
        var sink = new CapturingSink();
        using var logger = RunnerLogging
            .Configure(Settings(("RUNNER_LOG_LEVEL", "Warning")), new LoggerConfiguration(), "test-host")
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("kept quiet");
        logger.Warning("worth saying");

        Assert.DoesNotContain("kept quiet", sink.Rendered(), StringComparison.Ordinal);
        Assert.Contains("worth saying", sink.Rendered(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunnerNamesItselfOnEveryLine()
    {
        var sink = new CapturingSink();
        using var logger = RunnerLogging
            .Configure(Settings(("RUNNER_DISPLAY_NAME", "runner-7")), new LoggerConfiguration(), "test-host")
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("working");

        Assert.Contains("runner-7", sink.Rendered(), StringComparison.Ordinal);
    }

    private static string Log(object value)
    {
        var sink = new CapturingSink();
        using var logger = RunnerLogging
            .Configure(Settings(), new LoggerConfiguration().MinimumLevel.Verbose(), "test-host")
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("handling {@Value}", value);

        return sink.Rendered();
    }

    private static IConfiguration Settings(params (string Key, string Value)[] values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)))
            .Build();
    }

    private static AiConnectionDto Connection()
    {
        return new AiConnectionDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Primary",
            "meisterdev/openAiCompatible",
            "https://api.example.com/v1",
            "meisterdev/openAiCompatible:ApiKey",
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
            ProviderSettings = new Dictionary<string, string> { ["region"] = "region-eu-west" },
            DeclaredSecrets = new Dictionary<string, string> { ["clientSecret"] = "cs-declared-secret" },
        };
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
