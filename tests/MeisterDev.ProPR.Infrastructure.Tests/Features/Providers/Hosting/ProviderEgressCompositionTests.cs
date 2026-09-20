// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.Ai.Providers.Transport;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     What a provider family actually gets when it asks the host for a client, composed the way the host
///     composes it rather than the way a test could.
/// </summary>
/// <remarks>
///     These are the two properties a family could otherwise lose by building its own client: the connect-time
///     address check that defends against an operator-supplied base URL, and the response repairs several
///     providers need. Both are the host's, both are innermost, and a family cannot opt out of either.
/// </remarks>
public sealed class ProviderEgressCompositionTests
{
    [Theory]
    [InlineData(ProviderHttpPurpose.Probe)]
    [InlineData(ProviderHttpPurpose.Admin)]
    [InlineData(ProviderHttpPurpose.Runtime)]
    public async Task ARequestToABlockedAddressIsRefusedForEveryPurpose(ProviderHttpPurpose purpose)
    {
        using var host = Compose(allowPrivateEgress: false);
        using var client = host
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .ToProviderFactory()
            .Create(purpose);

        // A literal in a range the guard refuses, so nothing is resolved and no request leaves the machine.
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("https://169.254.169.254/latest/meta-data/")));

        Assert.Contains("blocked egress address", refused.Message, StringComparison.Ordinal);
    }

    // A family's own outer handler cannot remove what the host put nearest the wire: the refusal reaches the
    // family's handler as a failure rather than being something it can decline to apply.
    [Fact]
    public async Task AFamilysOwnHandlerCannotReachAnAddressTheGuardRefuses()
    {
        using var host = Compose(allowPrivateEgress: false);
        using var client = host
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .ToProviderFactory()
            .Create(ProviderHttpPurpose.Runtime, [new PassThroughHandler()]);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("https://169.254.169.254/latest/meta-data/")));
    }

    // The finish reason an OpenAI-compatible gateway forwards from another vendor is one the client library's
    // enum refuses to read, so the call fails while the response is still being deserialized. The repair sits
    // on the runtime pipeline, and a family that took its client from the host gets it.
    [Fact]
    public async Task TheRuntimePipelineStillRepairsAFinishReasonTheClientLibraryCannotRead()
    {
        using var vendor = LoopbackVendor.Serving("""{"choices":[{"finish_reason":"end_turn","message":{"role":"assistant","content":"hi"}}]}""");

        // The listener is on loopback, so the installation-wide opt-in has to be on for it to be reachable at
        // all. That is the same switch an operator sets for a self-hosted endpoint.
        using var host = Compose(allowPrivateEgress: true);
        using var client = host
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .ToProviderFactory()
            .Create(ProviderHttpPurpose.Runtime);

        using var response = await client.GetAsync(vendor.Address);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"\"{FinishReasonNormalizingHandler.FinishReasonField}\":\"stop\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("end_turn", body, StringComparison.Ordinal);
    }

    private static ServiceProvider Compose(bool allowPrivateEgress)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureSupport(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AI_ALLOW_PRIVATE_EGRESS"] = allowPrivateEgress ? "true" : "false",
                    })
                .Build(),
            environment: null,
            includeProviderOperationalServices: false);

        return services.BuildServiceProvider();
    }

    /// <summary>A family's own handler that changes nothing, standing in for one that signs or repairs.</summary>
    private sealed class PassThroughHandler : DelegatingHandler
    {
    }

    /// <summary>A provider on loopback, answering every request with one body.</summary>
    private sealed class LoopbackVendor : IDisposable
    {
        /// <summary>How many ports are tried before the failure to bind one is reported.</summary>
        private const int PortAttempts = 5;

        private readonly HttpListener _listener;

        private LoopbackVendor(HttpListener listener, Uri address)
        {
            this._listener = listener;
            this.Address = address;
        }

        /// <summary>Where the provider answers.</summary>
        public Uri Address { get; }

        public static LoopbackVendor Serving(string body)
        {
            var (listener, port) = OpenOnAFreePort();

            _ = Task.Run(async () =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var context = await listener.GetContextAsync();
                        var payload = Encoding.UTF8.GetBytes(body);
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = payload.Length;
                        await context.Response.OutputStream.WriteAsync(payload);
                        context.Response.Close();
                    }
                }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                {
                    // The listener was stopped while it was waiting, which is how this loop ends.
                }
            });

            return new LoopbackVendor(listener, new Uri($"http://127.0.0.1:{port}/v1/chat/completions"));
        }

        public void Dispose()
        {
            this._listener.Close();
        }

        /// <summary>Opens a listener on a port nothing else on the machine holds.</summary>
        /// <remarks>
        ///     A port is found free by binding it and letting it go again, so anything else on the machine can
        ///     take it before this listener binds it, and the bind then throws. A fresh port is tried a bounded
        ///     number of times and the last attempt throws, so a machine that cannot carry a loopback listener
        ///     fails the test rather than looping.
        /// </remarks>
        private static (HttpListener Listener, int Port) OpenOnAFreePort()
        {
            var attempt = 0;

            while (true)
            {
                attempt++;
                var port = FreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");

                try
                {
                    listener.Start();
                    return (listener, port);
                }
                catch (HttpListenerException) when (attempt < PortAttempts)
                {
                    listener.Close();
                }
            }
        }

        /// <summary>A port the operating system chose, and nothing held at the moment it was picked.</summary>
        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}

/// <summary>Builds the factory a provider family is handed, over the host's registered pipelines.</summary>
internal static class ProviderHttpClientFactoryComposition
{
    /// <summary>Wraps the host's handler pool in the factory the contract declares.</summary>
    /// <param name="handlers">The host's pooled handler chains, by pipeline name.</param>
    public static IProviderHttpClientFactory ToProviderFactory(this IHttpMessageHandlerFactory handlers)
    {
        return new ProviderHttpClientFactory(handlers);
    }
}
