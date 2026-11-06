// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.ProPR.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace MeisterDev.ProPR.Api.Tests.Hosting;

public sealed class UntrustedProxyWarningMiddlewareTests
{
    [Theory]
    [InlineData("100.100.0.206")]
    [InlineData("::ffff:100.100.0.206")]
    public async Task AForwardedRequestFromAnUntrustedAddress_IsReportedOnceWithTheAddress(string address)
    {
        var logger = new ListLogger();
        var passed = 0;
        var middleware = Middleware(logger, _ => passed++);

        await middleware.InvokeAsync(Request(address, "X-Forwarded-Proto", "https"));
        await middleware.InvokeAsync(Request("100.100.0.207", "X-Forwarded-For", "203.0.113.10"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("100.100.0.206", entry.Message, StringComparison.Ordinal);
        Assert.Contains(TrustedProxyNetworks.ConfigurationKey, entry.Message, StringComparison.Ordinal);
        Assert.Equal(2, passed);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("::1")]
    public async Task AForwardedRequestFromATrustedAddress_IsNotReported(string address)
    {
        var logger = new ListLogger();

        await Middleware(logger).InvokeAsync(Request(address, "X-Forwarded-Proto", "https"));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ARequestWithoutForwardingHeaders_IsNotReported()
    {
        var logger = new ListLogger();

        await Middleware(logger).InvokeAsync(Request("203.0.113.10"));

        Assert.Empty(logger.Entries);
    }

    private static UntrustedProxyWarningMiddleware Middleware(ListLogger logger, Action<HttpContext>? next = null)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Add(IPNetwork.Parse("10.0.0.0/8"));
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        return new UntrustedProxyWarningMiddleware(
            context =>
            {
                next?.Invoke(context);
                return Task.CompletedTask;
            },
            Options.Create(options),
            logger);
    }

    private static DefaultHttpContext Request(string address, string? header = null, string? value = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (header is not null)
        {
            context.Request.Headers[header] = value;
        }

        return context;
    }

    private sealed class ListLogger : ILogger<UntrustedProxyWarningMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
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
            this.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
