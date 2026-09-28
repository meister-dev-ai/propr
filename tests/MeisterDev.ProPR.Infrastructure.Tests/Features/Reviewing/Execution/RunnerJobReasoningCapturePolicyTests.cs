// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     The one answer both routes a runner's text arrives by are checked against: the relay it asks for a
///     completion through, and the trace it spools back.
/// </summary>
public sealed class RunnerJobReasoningCapturePolicyTests
{
    private static readonly Guid JobId = Guid.Parse("77777777-7777-4777-8777-777777777777");
    private static readonly Guid ClientId = Guid.Parse("88888888-8888-4888-8888-888888888888");

    [Theory]
    [InlineData(ReasoningCapturePolicy.Enabled, false, true)]
    [InlineData(ReasoningCapturePolicy.Disabled, true, false)]
    public async Task AStatedPolicyOverridesTheInstallationSwitch(
        ReasoningCapturePolicy policy,
        bool installationSwitch,
        bool expected)
    {
        var sut = Sut(policy, installationSwitch, out _);

        Assert.Equal(expected, await sut.CapturesReasoningAsync(JobId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATenantThatStatesNothingLeavesTheInstallationSwitchInCharge(bool installationSwitch)
    {
        var sut = Sut(ReasoningCapturePolicy.InstallationDefault, installationSwitch, out _);

        Assert.Equal(installationSwitch, await sut.CapturesReasoningAsync(JobId));
    }

    // No tenant has permitted the text when the job cannot be resolved, so it is not recorded. The installation
    // switch is not an authorisation: it states what a tenant that has said nothing gets, and an unresolvable
    // job has no tenant at all.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJobThatCannotBeFoundCapturesNoReasoning(bool installationSwitch)
    {
        var sut = Sut(ReasoningCapturePolicy.Enabled, installationSwitch, out var jobs);
        jobs.GetById(JobId).Returns((ReviewJob?)null);

        Assert.False(await sut.CapturesReasoningAsync(JobId));
    }

    // A half-written job row names no client, so the walk to a tenant cannot start.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJobThatNamesNoClientCapturesNoReasoning(bool installationSwitch)
    {
        var sut = Sut(ReasoningCapturePolicy.Enabled, installationSwitch, out var jobs);
        jobs.GetById(JobId)
            .Returns(new ReviewJob(JobId, Guid.Empty, "https://host.invalid", "proj", "repo", 12, 1));

        Assert.False(await sut.CapturesReasoningAsync(JobId));
    }

    // An unresolvable job is a persistence fault and not a policy, so it is not silent.
    [Fact]
    public async Task AJobThatCannotBeFoundIsLogged()
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetById(JobId).Returns((ReviewJob?)null);
        var captured = new List<string>();

        var sut = new RunnerJobReasoningCapturePolicy(
            jobs,
            new AiReviewOptions { CaptureReasoningInProtocol = true },
            Substitute.For<ITenantReasoningCapturePolicyProvider>(),
            new CapturingLogger(captured));

        Assert.False(await sut.CapturesReasoningAsync(JobId));
        Assert.Contains(captured, line => line.Contains(JobId.ToString(), StringComparison.Ordinal));
    }

    // A store that answers with a fault leaves the job as unreadable as a missing row does, so the same answer
    // applies: no tenant has permitted the text.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AJobLookupThatFaultsCapturesNoReasoning(bool installationSwitch)
    {
        var sut = Sut(ReasoningCapturePolicy.Enabled, installationSwitch, out var jobs);
        jobs.GetById(JobId).Returns(_ => throw new InvalidOperationException("The job store is unreachable."));

        Assert.False(await sut.CapturesReasoningAsync(JobId));
    }

    // A store fault is a persistence fault and not a policy, so it is not silent.
    [Fact]
    public async Task AJobLookupThatFaultsIsLogged()
    {
        var jobs = Substitute.For<IJobRepository>();
        jobs.GetById(JobId).Returns(_ => throw new InvalidOperationException("The job store is unreachable."));
        var captured = new List<string>();

        var sut = new RunnerJobReasoningCapturePolicy(
            jobs,
            new AiReviewOptions { CaptureReasoningInProtocol = true },
            Substitute.For<ITenantReasoningCapturePolicyProvider>(),
            new CapturingLogger(captured));

        Assert.False(await sut.CapturesReasoningAsync(JobId));
        Assert.Contains(captured, line => line.Contains(JobId.ToString(), StringComparison.Ordinal));
    }

    // Cancellation is the caller walking away, not a fault the policy answers for.
    [Fact]
    public async Task ACancelledJobLookupIsReported()
    {
        var sut = Sut(ReasoningCapturePolicy.Enabled, installationSwitch: true, out var jobs);
        jobs.GetById(JobId).Returns(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.CapturesReasoningAsync(JobId));
    }

    // A composition without the tenant module has no tenant to ask, which is the community edition.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithoutATenantPolicyProviderTheInstallationSwitchDecides(bool installationSwitch)
    {
        var sut = new RunnerJobReasoningCapturePolicy(
            Substitute.For<IJobRepository>(),
            new AiReviewOptions { CaptureReasoningInProtocol = installationSwitch });

        Assert.Equal(installationSwitch, await sut.CapturesReasoningAsync(JobId));
    }

    private static RunnerJobReasoningCapturePolicy Sut(
        ReasoningCapturePolicy policy,
        bool installationSwitch,
        out IJobRepository jobs)
    {
        jobs = Substitute.For<IJobRepository>();
        jobs.GetById(JobId).Returns(new ReviewJob(JobId, ClientId, "https://host.invalid", "proj", "repo", 12, 1));

        var tenantPolicies = Substitute.For<ITenantReasoningCapturePolicyProvider>();
        tenantPolicies.GetForClientAsync(ClientId, Arg.Any<CancellationToken>()).Returns(policy);

        return new RunnerJobReasoningCapturePolicy(
            jobs,
            new AiReviewOptions { CaptureReasoningInProtocol = installationSwitch },
            tenantPolicies);
    }

    private sealed class CapturingLogger(List<string> lines) : ILogger<RunnerJobReasoningCapturePolicy>
    {
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
            lines.Add(formatter(state, exception));
        }
    }
}
