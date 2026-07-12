// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Auth;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.IdentityAndAccess.Auth;

public sealed class AccountLockoutServiceTests
{
    [Fact]
    public void IsLockedOut_WhenLockoutInFuture_ReturnsTrue()
    {
        var service = CreateService(out _);
        var user = new AppUser { LockoutEndAt = DateTimeOffset.UtcNow.AddMinutes(5) };

        Assert.True(service.IsLockedOut(user));
    }

    [Fact]
    public void IsLockedOut_WhenNoLockoutOrElapsed_ReturnsFalse()
    {
        var service = CreateService(out _);

        Assert.False(service.IsLockedOut(new AppUser()));
        Assert.False(service.IsLockedOut(new AppUser { LockoutEndAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));
    }

    [Fact]
    public async Task RecordFailureAsync_BelowThreshold_IncrementsWithoutLockout()
    {
        var service = CreateService(out var repository);
        repository.IncrementFailedLoginAttemptsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(4);
        var user = new AppUser { Id = Guid.NewGuid() };

        await service.RecordFailureAsync(user);

        Assert.Equal(4, user.FailedLoginAttempts);
        Assert.Null(user.LockoutEndAt);
        await repository.DidNotReceive().SetLockoutEndAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordFailureAsync_AtThreshold_AppliesBaseLockout()
    {
        var service = CreateService(out var repository);
        repository.IncrementFailedLoginAttemptsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(5);
        var user = new AppUser { Id = Guid.NewGuid() };
        var before = DateTimeOffset.UtcNow;

        await service.RecordFailureAsync(user);

        Assert.Equal(5, user.FailedLoginAttempts);
        Assert.NotNull(user.LockoutEndAt);
        Assert.InRange(user.LockoutEndAt!.Value, before.AddMinutes(14), before.AddMinutes(16));
        await repository.Received(1).SetLockoutEndAsync(user.Id, Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordFailureAsync_SecondThresholdBlock_DoublesLockout()
    {
        var service = CreateService(out var repository);
        repository.IncrementFailedLoginAttemptsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(10);
        var user = new AppUser { Id = Guid.NewGuid() };
        var before = DateTimeOffset.UtcNow;

        await service.RecordFailureAsync(user);

        Assert.Equal(10, user.FailedLoginAttempts);
        Assert.InRange(user.LockoutEndAt!.Value, before.AddMinutes(29), before.AddMinutes(31));
    }

    [Fact]
    public async Task RecordFailureAsync_BackoffCappedAtMax()
    {
        var service = CreateService(out var repository, new AccountLockoutOptions { MaxFailedAttempts = 5, BaseLockoutMinutes = 15, MaxLockoutMinutes = 60 });
        repository.IncrementFailedLoginAttemptsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(25);
        var user = new AppUser { Id = Guid.NewGuid() };
        var before = DateTimeOffset.UtcNow;

        await service.RecordFailureAsync(user);

        // increment→25 → blocks=5 → 15 * 2^4 = 240 min, capped to 60.
        Assert.InRange(user.LockoutEndAt!.Value, before.AddMinutes(59), before.AddMinutes(61));
    }

    [Fact]
    public async Task ResetAsync_ClearsCounterAndLockout()
    {
        var service = CreateService(out var repository);
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            FailedLoginAttempts = 3,
            LockoutEndAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        await service.ResetAsync(user);

        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockoutEndAt);
        await repository.Received(1).ResetFailedLoginsAsync(user.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetAsync_WhenAlreadyClear_DoesNotWrite()
    {
        var service = CreateService(out var repository);
        var user = new AppUser { Id = Guid.NewGuid(), FailedLoginAttempts = 0, LockoutEndAt = null };

        await service.ResetAsync(user);

        await repository.DidNotReceive().ResetFailedLoginsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static AccountLockoutService CreateService(out IUserRepository repository, AccountLockoutOptions? options = null)
    {
        repository = Substitute.For<IUserRepository>();
        options ??= new AccountLockoutOptions { MaxFailedAttempts = 5, BaseLockoutMinutes = 15, MaxLockoutMinutes = 60 };
        return new AccountLockoutService(repository, Microsoft.Extensions.Options.Options.Create(options));
    }
}
