// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;

namespace MeisterProPR.Application.Tests.Features.IdentityAndAccess;

public sealed class IdentityAndAccessModuleTests
{
    [Fact]
    public void GenerateAccessToken_ThenValidateAccessToken_RoundTripsExpectedClaims()
    {
        var configuration = BuildConfiguration("test-identity-access-secret-32chars!");
        var sut = new JwtTokenService(configuration);
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            GlobalRole = AppUserRole.Admin,
            IsActive = true,
        };

        var token = sut.GenerateAccessToken(user);
        var principal = sut.ValidateAccessToken(token);

        Assert.NotNull(principal);
        Assert.Equal(user.Id.ToString(), principal!.FindFirst("sub")?.Value);
        Assert.Equal(user.Username, principal.FindFirst("unique_name")?.Value);
        Assert.Equal(AppUserRole.Admin.ToString(), principal.FindFirst("global_role")?.Value);
    }

    [Fact]
    public void ValidateAccessToken_WhenTokenIsInvalid_ReturnsNull()
    {
        var configuration = BuildConfiguration("test-identity-access-secret-32chars!");
        var sut = new JwtTokenService(configuration);

        var principal = sut.ValidateAccessToken("not-a-jwt");

        Assert.Null(principal);
    }

    [Fact]
    public void Constructor_WhenSecretIsTooShort_Throws()
    {
        var configuration = BuildConfiguration("too-short-secret");

        var exception = Assert.Throws<InvalidOperationException>(() => new JwtTokenService(configuration));

        Assert.Contains("at least 32 characters", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration(string secret)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["MEISTER_JWT_SECRET"] = secret,
                })
            .Build();
    }
}
