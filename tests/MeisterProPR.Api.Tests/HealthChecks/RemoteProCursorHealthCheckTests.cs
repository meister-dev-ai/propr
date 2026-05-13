// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.HealthChecks;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Api.Tests.HealthChecks;

public sealed class RemoteProCursorHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_WhenRemoteProbeFails_ReturnsUnhealthyWithoutAttachingExceptionAndLogsError()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(new ThrowingHttpMessageHandler())
        {
            BaseAddress = new Uri("http://127.0.0.1:8081/"),
        };
        httpClientFactory.CreateClient(nameof(RemoteProCursorHealthCheck)).Returns(httpClient);

        var logger = new ListLogger<RemoteProCursorHealthCheck>();
        var sut = new RemoteProCursorHealthCheck(
            httpClientFactory,
            Options.Create(
                new ProCursorRemoteOptions
                {
                    Mode = ProCursorRemoteOptions.ProprManagedRemoteMode,
                    ServiceBaseUrl = "http://127.0.0.1:8081",
                    SharedKey = "test-shared-key",
                    RequestTimeoutSeconds = 5,
                }),
            logger);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Remote ProCursor is unavailable.", result.Description);
        Assert.Null(result.Exception);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Null(entry.Exception);
        Assert.Contains("Remote ProCursor health probe failed", entry.Message);
        Assert.Contains("127.0.0.1:8081", entry.Message);
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused");
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        public sealed record LogEntry(LogLevel LogLevel, string Message, Exception? Exception);

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
