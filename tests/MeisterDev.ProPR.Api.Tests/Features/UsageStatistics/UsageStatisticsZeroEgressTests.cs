// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.UsageStatistics.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.UsageStatistics;

/// <summary>
///     A send cycle performs no outbound request in the states where sending is not permitted.
///     <para>
///         Every case drives the real send loop through the real transport, substituting only the message
///         handler, and asserts that the handler was never invoked. An installation that is switched off, or
///         that no administrator has signed into, issues no request.
///     </para>
/// </summary>
public sealed class UsageStatisticsZeroEgressTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnInstallationWithUsageStatisticsOff_ProducesNoOutboundRequest()
    {
        var state = State(optIn: false, gateOpen: true);

        var (decision, requests) = await RunOneCycleAsync(state, InstallationEdition.Community);

        Assert.Equal(UsageStatisticsSendDecision.Disabled, decision);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task AnInstallationWithNoAdministratorSignIn_ProducesNoOutboundRequest()
    {
        var state = State(optIn: true, gateOpen: false);

        var (decision, requests) = await RunOneCycleAsync(state, InstallationEdition.Community);

        Assert.Equal(UsageStatisticsSendDecision.AwaitingConsent, decision);
        Assert.Equal(0, requests);
    }

    // A license enables sending but does not bypass the consent gate, so a commercial installation nobody has
    // signed into sends nothing, like a community one.
    [Fact]
    public async Task ACommercialInstallationBeforeItsFirstAdministrator_ProducesNoOutboundRequest()
    {
        var state = State(optIn: true, gateOpen: false);

        var (decision, requests) = await RunOneCycleAsync(state, InstallationEdition.Commercial);

        Assert.Equal(UsageStatisticsSendDecision.AwaitingConsent, decision);
        Assert.Equal(0, requests);
    }

    // A positive control for the cases above: the same harness produces a request when sending is permitted.
    [Fact]
    public async Task AnInstallationThatIsOnAndAdministered_ProducesExactlyOneRequest()
    {
        var state = State(optIn: true, gateOpen: true);

        var (decision, requests) = await RunOneCycleAsync(state, InstallationEdition.Community);

        Assert.Equal(UsageStatisticsSendDecision.Sent, decision);
        Assert.Equal(1, requests);
    }

    private static UsageStatisticsState State(bool optIn, bool gateOpen)
    {
        return new UsageStatisticsState(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            optIn,
            gateOpen ? Now.AddDays(-10) : null,
            null,
            null,
            null,
            null,
            null,
            null,
            [],
            null);
    }

    private static async Task<(UsageStatisticsSendDecision Decision, int Requests)> RunOneCycleAsync(
        UsageStatisticsState state,
        InstallationEdition edition)
    {
        var counter = new CountingHttpMessageHandler();
        var timeProvider = new FixedTimeProvider(Now);

        var store = Substitute.For<IUsageStatisticsStateStore>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(state));
        store.TryClaimSendAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        store.RecordSendOutcomeAsync(Arg.Any<UsageStatisticsSendOutcome>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(state));

        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.GetSummaryAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LicensingSummaryDto(edition, null, [])));
        var editionResolver = new UsageStatisticsEditionResolver(licensing);

        var countSource = Substitute.For<IUsageStatisticsCountSource>();
        countSource.CountAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new UsageStatisticsCounts(1, 0, 0, null, null)));

        var productVersion = Substitute.For<IProductVersionProvider>();
        productVersion.Version.Returns("1.0.0.alpha.0049");

        var snapshotBuilder = new UsageStatisticsSnapshotBuilder(countSource, productVersion, timeProvider);

        // The real transport over a handler that counts requests instead of connecting. Only the message
        // handler is substituted, so the assertion covers the whole path down to the request.
        var pingClient = new UsageStatisticsPingClient(
            new HttpClient(counter),
            timeProvider,
            NullLogger<UsageStatisticsPingClient>.Instance);

        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        services.AddScoped(_ => new UsageStatisticsSender(
            store,
            snapshotBuilder,
            editionResolver,
            pingClient,
            timeProvider));

        await using var provider = services.BuildServiceProvider();

        var worker = new UsageStatisticsSendWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            NullLogger<UsageStatisticsSendWorker>.Instance);

        var result = await worker.RunCycleOnceAsync(CancellationToken.None);
        return (result!.Decision, counter.RequestCount);
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            this.RequestCount++;
            return Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{}"),
                });
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
