using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.AzureDevOps;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>Unit tests for <see cref="AdoIdentityResolver" />.</summary>
public sealed class AdoIdentityResolverTests
{
    [Fact]
    public async Task ResolveAsync_WithNoPerClientCredentials_UsesGlobalCredential()
    {
        // Arrange
        var clientId = Guid.NewGuid();

        var credentialRepository = Substitute.For<IClientAdoCredentialRepository>();
        credentialRepository
            .GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientAdoCredentials?>(null));

        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var globalCredential = Substitute.For<TokenCredential>();
        globalCredential
            .GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));

        var sut = new AdoIdentityResolver(globalCredential, httpFactory, credentialRepository);

        // Act — will fail at the HTTP level (no real ADO), which is fine
        try
        {
            await sut.ResolveAsync("https://dev.azure.com/testorg", "Some User", clientId);
        }
        catch
        {
            // Network call fails — expected in unit test
        }

        // Assert: global credential WAS called
        await globalCredential.Received(1)
            .GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_WithPerClientCredentials_UsesClientCredential()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var perClientCredentials = new ClientAdoCredentials("tenant-abc", "client-abc", "secret-abc");

        var credentialRepository = Substitute.For<IClientAdoCredentialRepository>();
        credentialRepository
            .GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ClientAdoCredentials?>(perClientCredentials));

        // Use a real HttpClientFactory stub that throws so we confirm the credential lookup happened
        var httpFactory = Substitute.For<IHttpClientFactory>();
        var httpClient = new HttpClient();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        // GlobalCredential should NOT be called
        var globalCredential = Substitute.For<TokenCredential>();

        var sut = new AdoIdentityResolver(globalCredential, httpFactory, credentialRepository);

        // Act — will fail at the HTTP level (no real ADO), which is fine
        try
        {
            await sut.ResolveAsync("https://dev.azure.com/testorg", "Some User", clientId);
        }
        catch
        {
            // Network call fails — expected in unit test
        }

        // Assert: per-client credential repository was queried
        await credentialRepository.Received(1)
            .GetByClientIdAsync(clientId, Arg.Any<CancellationToken>());

        // Assert: global credential was NOT used
        await globalCredential.DidNotReceiveWithAnyArgs()
            .GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>());
    }
}