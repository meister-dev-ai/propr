// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitHub;

internal static class GitHubAppTestHelpers
{
    public const long DefaultAppId = 123456;
    public const long DefaultInstallationId = 789012;

    private static readonly Lazy<string> SharedPrivateKeyPem = new(CreatePrivateKeyPemCore);

    public static string CreatePrivateKeyPem(bool unique = false)
    {
        return unique ? CreatePrivateKeyPemCore() : SharedPrivateKeyPem.Value;
    }

    public static ClientScmConnectionCredentialDto CreateAppInstallationConnection(
        Guid clientId,
        ProviderHostRef host,
        string? privateKeyPem = null,
        long appId = DefaultAppId,
        long installationId = DefaultInstallationId)
    {
        return new ClientScmConnectionCredentialDto(
            Guid.NewGuid(),
            clientId,
            ScmProvider.GitHub,
            host.HostBaseUrl,
            ScmAuthenticationKind.AppInstallation,
            null,
            null,
            "GitHub App",
            privateKeyPem ?? CreatePrivateKeyPem(),
            true,
            appId,
            installationId);
    }

    public static IClientScmConnectionRepository CreateAppInstallationConnectionRepository(
        Guid clientId,
        ProviderHostRef host,
        string? privateKeyPem = null,
        long appId = DefaultAppId,
        long installationId = DefaultInstallationId)
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        repository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(CreateAppInstallationConnection(clientId, host, privateKeyPem, appId, installationId));
        return repository;
    }

    private static string CreatePrivateKeyPemCore()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
}
