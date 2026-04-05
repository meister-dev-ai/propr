// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Auth;

/// <summary>
///     Seeds the first admin user on startup when no admin account exists.
/// </summary>
public sealed class AdminBootstrapService(
    IUserRepository userRepository,
    IPasswordHashService passwordHashService,
    IConfiguration configuration,
    ILogger<AdminBootstrapService> logger)
{
    /// <summary>
    ///     Creates the admin user from <c>MEISTER_BOOTSTRAP_ADMIN_USER</c> /
    ///     <c>MEISTER_BOOTSTRAP_ADMIN_PASSWORD</c> when no admin exists.
    ///     Throws <see cref="InvalidOperationException"/> if the vars are absent and no admin user exists.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var users = await userRepository.ListAsync(ct);
        if (users.Any(u => u.GlobalRole == AppUserRole.Admin && u.IsActive))
        {
            return;
        }

        var username = configuration["MEISTER_BOOTSTRAP_ADMIN_USER"];
        var password = configuration["MEISTER_BOOTSTRAP_ADMIN_PASSWORD"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "No active admin user exists in the database, and " +
                "MEISTER_BOOTSTRAP_ADMIN_USER / MEISTER_BOOTSTRAP_ADMIN_PASSWORD are not configured. " +
                "Set these environment variables to seed the initial admin account.");
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = passwordHashService.Hash(password),
            GlobalRole = AppUserRole.Admin,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await userRepository.AddAsync(user, ct);

        logger.LogInformation(
            "Admin bootstrap: created admin user '{Username}' (id={UserId}).",
            username,
            user.Id);
    }
}
