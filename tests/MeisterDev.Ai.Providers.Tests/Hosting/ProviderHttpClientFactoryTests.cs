// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.Ai.Providers.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.Ai.Providers.Tests.Hosting;

/// <summary>
///     The client a provider family takes for one purpose, with its own handlers wrapped outside the host's.
/// </summary>
/// <remarks>
///     What matters here is what a family can and cannot do to the pipeline: it can see and change what it sends
///     and what comes back, and it cannot remove what the host put nearest the wire or hold a handler of its own
///     past the client that used it.
/// </remarks>
public sealed class ProviderHttpClientFactoryTests
{
    [Theory]
    [InlineData(ProviderHttpPurpose.Probe, ProviderHttpPipelines.Probe)]
    [InlineData(ProviderHttpPurpose.Admin, ProviderHttpPipelines.Admin)]
    [InlineData(ProviderHttpPurpose.Runtime, ProviderHttpPipelines.Runtime)]
    public async Task EachPurposeIssuesItsRequestsThroughThePipelineTheHostRegisteredForIt(
        ProviderHttpPurpose purpose,
        string expectedPipeline)
    {
        using var host = PipelineHost.WithAllThree();
        using var client = host.Factory.Create(purpose);

        using var response = await client.GetAsync(new Uri("https://api.example.com/models"));

        Assert.Equal(expectedPipeline, await response.Content.ReadAsStringAsync());
    }

    // The family's handler sits outside the host's, so it sees the request before the host's handlers touch it
    // and the response after they have. A family that signs its own requests or repairs its own responses does
    // it here rather than by building a client.
    [Fact]
    public async Task AFamilysHandlerRunsOutsideTheHostsAndSeesWhatTheHostsHandlersProduced()
    {
        using var host = PipelineHost.WithAllThree();
        var outer = new RecordingHandler();

        using var client = host.Factory.Create(ProviderHttpPurpose.Admin, [outer]);
        using var response = await client.GetAsync(new Uri("https://api.example.com/token"));

        Assert.Equal(ProviderHttpPipelines.Admin, outer.SawResponseBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The first handler a family lists is the outermost, which is the order the host composes its own pipelines
    // in. A family that listed them expecting one order and got the other would sign a request its own repair
    // handler had not finished with.
    [Fact]
    public async Task TheFirstHandlerAFamilyListsIsTheOutermost()
    {
        using var host = PipelineHost.WithAllThree();
        var order = new List<string>();

        using var client = host.Factory.Create(
            ProviderHttpPurpose.Probe,
            [new OrderingHandler("first", order), new OrderingHandler("second", order)]);

        using var response = await client.GetAsync(new Uri("https://api.example.com/ping"));

        Assert.Equal(new[] { "first", "second" }, order);
    }

    // A client a family takes shares the host's pooled chain, and that keeps connection reuse and name
    // resolution with the host. A family holding a handler it built would pin resolution for the life of the
    // process and leak sockets.
    [Fact]
    public void ClientsTakenRepeatedlyShareOnePooledChain()
    {
        using var host = PipelineHost.WithAllThree();

        using var first = host.Factory.Create(ProviderHttpPurpose.Runtime);
        using var second = host.Factory.Create(ProviderHttpPurpose.Runtime);

        Assert.Equal(1, host.PrimaryHandlersBuilt);
    }

    // Disposing the client disposes the handlers the family passed in, which it cannot reach again, and stops at
    // the pooled chain: tearing that down would take connections other callers are still using.
    [Fact]
    public async Task DisposingTheClientDisposesTheFamilysHandlersAndLeavesThePooledChainAlone()
    {
        using var host = PipelineHost.WithAllThree();
        var outer = new RecordingHandler();

        using (var client = host.Factory.Create(ProviderHttpPurpose.Admin, [outer]))
        {
            using var response = await client.GetAsync(new Uri("https://api.example.com/token"));
        }

        Assert.True(outer.WasDisposed);

        // The pooled chain still serves, which it could not do if the client had disposed it.
        using var again = host.Factory.Create(ProviderHttpPurpose.Admin);
        using var afterwards = await again.GetAsync(new Uri("https://api.example.com/token"));
        Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);
    }

    // A long completion is not cut off part-written, which the runtime pipeline's infinite client
    // timeout is for; each call is still bounded by the token it was made with.
    [Fact]
    public void TheRuntimeClientDoesNotImposeATimeoutOfItsOwn()
    {
        using var host = PipelineHost.WithAllThree();

        using var runtime = host.Factory.Create(ProviderHttpPurpose.Runtime);
        using var probe = host.Factory.Create(ProviderHttpPurpose.Probe);

        Assert.Equal(Timeout.InfiniteTimeSpan, runtime.Timeout);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, probe.Timeout);
    }

    // Both of these produce a pipeline that does something other than what the list says, and neither reports
    // itself: a handler that already has an inner one belongs to another chain, and one listed twice would be
    // made to wrap itself.
    [Fact]
    public void AHandlerThatIsAlreadyChainedOrListedTwiceIsRefused()
    {
        using var host = PipelineHost.WithAllThree();

        var chained = new RecordingHandler { InnerHandler = new RecordingHandler() };
        Assert.Throws<ArgumentException>(() => host.Factory.Create(ProviderHttpPurpose.Probe, [chained]));

        var repeated = new RecordingHandler();
        Assert.Throws<ArgumentException>(() => host.Factory.Create(ProviderHttpPurpose.Probe, [repeated, repeated]));
    }

    [Fact]
    public void APurposeOutsideTheThreeIsRefused()
    {
        using var host = PipelineHost.WithAllThree();

        Assert.Throws<ArgumentOutOfRangeException>(() => host.Factory.Create((ProviderHttpPurpose)99));
    }

    /// <summary>A host composition carrying the three named pipelines, each answering with its own name.</summary>
    private sealed class PipelineHost : IDisposable
    {
        private readonly Counter _counter;
        private readonly ServiceProvider _services;

        private PipelineHost(ServiceProvider services, Counter counter)
        {
            this._services = services;
            this._counter = counter;
            this.Factory = new ProviderHttpClientFactory(services.GetRequiredService<IHttpMessageHandlerFactory>());
        }

        public ProviderHttpClientFactory Factory { get; }

        /// <summary>How many primary handlers the pool has built, which sharing one chain is measured by.</summary>
        public int PrimaryHandlersBuilt => this._counter.Value;

        public static PipelineHost WithAllThree()
        {
            var counter = new Counter();
            var services = new ServiceCollection();

            foreach (var pipeline in new[]
                     {
                         ProviderHttpPipelines.Probe,
                         ProviderHttpPipelines.Admin,
                         ProviderHttpPipelines.Runtime,
                     })
            {
                var name = pipeline;
                services.AddHttpClient(name)
                    .ConfigurePrimaryHttpMessageHandler(() =>
                    {
                        counter.Increment();
                        return new NamingHandler(name);
                    });
            }

            return new PipelineHost(services.BuildServiceProvider(), counter);
        }

        public void Dispose()
        {
            this._services.Dispose();
        }

        private sealed class Counter
        {
            private int _value;

            public int Value => Volatile.Read(ref this._value);

            public void Increment()
            {
                Interlocked.Increment(ref this._value);
            }
        }
    }

    /// <summary>The host's innermost handler, standing in for the guarded one and naming the pipeline it is on.</summary>
    /// <param name="pipeline">The pipeline this handler belongs to.</param>
    private sealed class NamingHandler(string pipeline) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(pipeline) });
        }
    }

    /// <summary>A family's own handler, recording what it saw and whether it was disposed with the client.</summary>
    private sealed class RecordingHandler : DelegatingHandler
    {
        public string? SawResponseBody { get; private set; }

        public bool WasDisposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            this.SawResponseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            this.WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>A family's own handler, recording where in the chain it sits.</summary>
    /// <param name="name">What to record.</param>
    /// <param name="order">Where each handler records itself, in the order they run.</param>
    private sealed class OrderingHandler(string name, List<string> order) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            order.Add(name);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
