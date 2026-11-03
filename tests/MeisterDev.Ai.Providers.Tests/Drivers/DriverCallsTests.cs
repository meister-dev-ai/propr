// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     What a verification or a discovery reports when the call to the provider got no answer.
/// </summary>
public sealed class DriverCallsTests
{
    private static readonly ProviderEndpoint Endpoint =
        new("meisterdev/openAiCompatible", "https://llm.example.test/v1", "apiKey", "key");

    public static TheoryData<Exception> Unanswered => new()
    {
        new HttpRequestException("Name or service not known", new SocketException((int)SocketError.HostNotFound)),
        new HttpRequestException("Refused to connect to host llm.example.test: blocked egress address."),
        new HttpRequestException("The SSL connection could not be established.", new AuthenticationException("untrusted root")),
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
    };

    [Theory]
    [MemberData(nameof(Unanswered))]
    public async Task ADiscoveryThatGotNoAnswerIsAFailedDiscoveryOfTheEndpoint(Exception exception)
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.DiscoverModelsAsync(Endpoint, Arg.Any<CancellationToken>()).ThrowsAsync(exception);

        var discovered = await DriverCalls.DiscoverModelsAsync(driver, Endpoint, CancellationToken.None);

        Assert.Equal("failed", discovered.DiscoveryStatus);
        Assert.True(discovered.ManualEntryAllowed);
        Assert.Empty(discovered.Models);
        Assert.Equal(AiVerificationFailureCategory.EndpointReachability, discovered.FailureCategory);
        Assert.Equal(exception.Message, Assert.Single(discovered.Warnings));
    }

    [Theory]
    [MemberData(nameof(Unanswered))]
    public async Task AVerificationThatGotNoAnswerIsAFailedVerificationOfTheEndpoint(Exception exception)
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.VerifyAsync(Endpoint, Arg.Any<CancellationToken>()).ThrowsAsync(exception);

        var verification = await DriverCalls.VerifyAsync(driver, Endpoint, CancellationToken.None);

        Assert.Equal(AiVerificationStatus.Failed, verification.Status);
        Assert.Equal(AiVerificationFailureCategory.EndpointReachability, verification.FailureCategory);
    }

    [Fact]
    public async Task AStatusThatAClientRaisedKeepsItsCategory()
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.DiscoverModelsAsync(Endpoint, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized));

        var discovered = await DriverCalls.DiscoverModelsAsync(driver, Endpoint, CancellationToken.None);

        Assert.Equal(AiVerificationFailureCategory.Credentials, discovered.FailureCategory);
        Assert.Equal(401, discovered.HttpStatus);
    }

    [Fact]
    public async Task ACancellationOfTheCallerIsNotAFailureOfTheProvider()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var driver = Substitute.For<IAiProviderDriver>();
        driver.DiscoverModelsAsync(Endpoint, cancellation.Token)
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DriverCalls.DiscoverModelsAsync(driver, Endpoint, cancellation.Token));
    }

    [Fact]
    public async Task AnErrorOfTheFamilyItselfStillEscapes()
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.VerifyAsync(Endpoint, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("bug"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DriverCalls.VerifyAsync(driver, Endpoint, CancellationToken.None));
    }

    [Fact]
    public async Task AnAnswerIsPassedOnUnchanged()
    {
        var driver = Substitute.For<IAiProviderDriver>();
        var answer = new ProviderModelDiscoveryResult("succeeded", true, [], []);
        driver.DiscoverModelsAsync(Endpoint, Arg.Any<CancellationToken>()).Returns(answer);

        Assert.Same(answer, await DriverCalls.DiscoverModelsAsync(driver, Endpoint, CancellationToken.None));
    }
}
