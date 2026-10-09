// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using System.Reflection;
using Azure.Core;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Broker;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.ProCursor;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.ProCursor;

public sealed class ProCursorScmBrokerBoundaryTests
{
    [Fact]
    public void NativeScmBackendOwnsTheSdkImplementation()
    {
        Assert.NotNull(
            typeof(LocalProPrScmBroker).Assembly.GetType("MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.ProCursor.AdoProCursorScmBroker"));
    }

    [Fact]
    public void SharedBrokerDoesNotDependOnNativeSdkConstruction()
    {
        Assert.DoesNotContain(
            typeof(LocalProPrScmBroker).GetConstructors().SelectMany(ctor => ctor.GetParameters()),
            parameter => parameter.ParameterType == typeof(VssConnectionFactory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingClientIsRejectedBeforeConnectionCredentialsAreRead(bool materialize)
    {
        var clients = Substitute.For<IClientAdminService>();
        var backend = Substitute.For<IProCursorScmBroker>();
        var broker = new LocalProPrScmBroker(clients, backend);
        var source = Source();

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
        {
            if (materialize)
            {
                await broker.MaterializeAsync(source, Branch(), null);
            }
            else
            {
                await broker.GetLatestCommitShaAsync(source, Branch());
            }
        });

        Assert.Equal($"Client {source.ClientId} was not found.", error.Message);
        await clients.Received(1).ExistsAsync(source.ClientId, Arg.Any<CancellationToken>());
        await backend.DidNotReceiveWithAnyArgs().GetLatestCommitShaAsync(default!, default!);
        await backend.DidNotReceiveWithAnyArgs().MaterializeAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(ScmAuthenticationKind.PersonalAccessToken, true, true)]
    [InlineData(ScmAuthenticationKind.OAuthClientCredentials, true, true)]
    [InlineData(ScmAuthenticationKind.OAuthClientCredentials, false, false)]
    [InlineData(ScmAuthenticationKind.WindowsUserAccount, true, true)]
    [InlineData(ScmAuthenticationKind.WindowsUserAccount, false, false)]
    [InlineData(ScmAuthenticationKind.AppInstallation, true, false)]
    public async Task CapturedCredentialProjectionPreservesNativeFallback(ScmAuthenticationKind kind, bool complete, bool usable)
    {
        var connections = Substitute.For<IClientScmConnectionRepository>();
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org");
        connections.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionCredentialDto(
                Guid.NewGuid(), clientId, ScmProvider.AzureDevOps, host.HostBaseUrl,
                kind, complete ? "tenant" : null, "client", "ADO", "test", true, UserName: complete ? "user" : null));
        var broker = new AdoProCursorScmBroker(connections, new VssConnectionFactory(Substitute.For<TokenCredential>()));
        var credentials = await broker.ResolveConnectionCredentialsAsync(clientId, host.HostBaseUrl, CancellationToken.None);

        Assert.Equal(usable, credentials is not null);
        if (usable)
        {
            Assert.Equal(kind, credentials!.AuthenticationKind);
            Assert.Equal("test", credentials.Secret);
            Assert.Equal(kind == ScmAuthenticationKind.WindowsUserAccount ? "user" : null, credentials.UserName);
        }

        await connections.Received(1).GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ProCursorSourceKind.Repository)]
    [InlineData(ProCursorSourceKind.AdoWiki)]
    [InlineData((ProCursorSourceKind)999)]
    public async Task AuthorizedSourceCoordinatesAreDelegatedWithoutFamilyOrKindTightening(ProCursorSourceKind kind)
    {
        var source = Source() with
        {
            SourceKind = kind,
            CanonicalSourceRef = new("github", "recorded-source"),
        };
        var branch = Branch();
        var clients = Substitute.For<IClientAdminService>();
        var backend = Substitute.For<IProCursorScmBroker>();
        var validated = false;
        clients.ExistsAsync(source.ClientId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            validated = true;
            return true;
        });
        var expected = new ProCursorScmMaterializationResponse("commit", []);
        backend.MaterializeAsync(source, branch, " raw commit ", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Assert.True(validated);
            return expected;
        });

        Assert.Same(
            expected, await new LocalProPrScmBroker(clients, backend)
                .MaterializeAsync(source, branch, " raw commit "));

        await clients.Received(1).ExistsAsync(source.ClientId, Arg.Any<CancellationToken>());
        await backend.Received(1).MaterializeAsync(source, branch, " raw commit ", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClientValidationCancellationDoesNotInvokeTheNativeBackend()
    {
        var clients = Substitute.For<IClientAdminService>();
        var backend = Substitute.For<IProCursorScmBroker>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = Source();
        clients.ExistsAsync(source.ClientId, cancellation.Token).Returns(Task.FromCanceled<bool>(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalProPrScmBroker(clients, backend).MaterializeAsync(source, Branch(), null, cancellation.Token));

        await backend.DidNotReceiveWithAnyArgs().MaterializeAsync(default!, default!, default);
    }

    [Fact]
    public void NativeModuleRegistersScopedClientValidationAndMaterialization()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IClientAdminService>());
        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddSingleton(new VssConnectionFactory(Substitute.For<TokenCredential>()));
        services.AddAzureDevOpsProCursorBroker();
        services.AddAzureDevOpsProCursorBroker();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var broker = first.ServiceProvider.GetRequiredService<LocalProPrScmBroker>();

        Assert.Same(broker, first.ServiceProvider.GetRequiredService<LocalProPrScmBroker>());
        Assert.NotSame(broker, second.ServiceProvider.GetRequiredService<LocalProPrScmBroker>());
        Assert.NotNull(first.ServiceProvider.GetRequiredService<AdoProCursorScmBroker>());
    }

    private static ProCursorKnowledgeSourceDto Source() =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), "Source", ProCursorSourceKind.Repository, "https://dev.azure.com/org",
            "project", "repository", "main", null, true, "auto", null, []);

    private static ProCursorTrackedBranchDto Branch() =>
        new(Guid.NewGuid(), "refs/heads/main", ProCursorRefreshTriggerMode.BranchUpdate, true, null, null, true, "current");
}
