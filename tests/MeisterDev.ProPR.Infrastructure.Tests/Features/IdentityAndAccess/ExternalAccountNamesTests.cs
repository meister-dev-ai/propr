// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess;

public sealed class ExternalAccountNamesTests
{
    [Theory]
    [InlineData("Jane.Doe@example.test", "jane.doe")]
    [InlineData("operator", "operator")]
    [InlineData("o'brien+ops@example.test", "obrienops")]
    [InlineData("@example.test", "user")]
    public async Task CreateUniqueUsernameAsync_DerivesTheNameFromThePartBeforeTheAtSign(string source, string expected)
    {
        var repository = Substitute.For<IUserRepository>();

        var username = await ExternalAccountNames.CreateUniqueUsernameAsync(repository, source, CancellationToken.None);

        Assert.Equal(expected, username);
    }

    [Fact]
    public async Task CreateUniqueUsernameAsync_WhenTheNameIsTaken_AppendsTheFirstFreeNumber()
    {
        var repository = Substitute.For<IUserRepository>();
        repository.GetByUsernameAsync("operator", Arg.Any<CancellationToken>()).Returns(new AppUser { Username = "operator" });
        repository.GetByUsernameAsync("operator1", Arg.Any<CancellationToken>()).Returns(new AppUser { Username = "operator1" });

        var username = await ExternalAccountNames.CreateUniqueUsernameAsync(repository, "operator", CancellationToken.None);

        Assert.Equal("operator2", username);
    }
}
