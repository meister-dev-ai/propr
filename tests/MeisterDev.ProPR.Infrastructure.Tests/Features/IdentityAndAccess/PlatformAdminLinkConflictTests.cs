// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess;

/// <summary>
///     A first sign-in that loses the race to create the link of its identity is refused as a conflict. Every other
///     write failure is no conflict, so the sign-in service lets it surface as an error.
/// </summary>
public sealed class PlatformAdminLinkConflictTests
{
    [Fact]
    public void IsLinkConflict_ForTheUniqueIndexOfThePlatformLink_IsTrue()
    {
        Assert.True(PlatformAdminSignInService.IsLinkConflict(Failure(PostgresErrorCodes.UniqueViolation, "ix_external_identities_platform_subject")));
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, "ix_app_users_username")]
    [InlineData(PostgresErrorCodes.CheckViolation, "ck_external_identities_tenant_and_provider")]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, "ix_external_identities_platform_subject")]
    public void IsLinkConflict_ForAnyOtherWriteFailure_IsFalse(string sqlState, string constraintName)
    {
        Assert.False(PlatformAdminSignInService.IsLinkConflict(Failure(sqlState, constraintName)));
    }

    [Fact]
    public void IsLinkConflict_WithoutADatabaseError_IsFalse()
    {
        Assert.False(PlatformAdminSignInService.IsLinkConflict(new DbUpdateException("The write failed.")));
    }

    private static DbUpdateException Failure(string sqlState, string constraintName)
    {
        return new DbUpdateException(
            "The write failed.",
            new PostgresException("The write failed.", "ERROR", "ERROR", sqlState, constraintName: constraintName));
    }
}
