// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Tests.DTOs;

public sealed class ClientAdoOrganizationScopeDtoTests
{
    [Fact]
    public void Constructor_PreservesVerificationState()
    {
        var verifiedAt = DateTimeOffset.UtcNow;
        var dto = new ClientAdoOrganizationScopeDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "Org",
            true,
            AdoOrganizationVerificationStatus.Verified,
            verifiedAt,
            null,
            verifiedAt,
            verifiedAt);

        Assert.Equal(AdoOrganizationVerificationStatus.Verified, dto.VerificationStatus);
        Assert.Equal(verifiedAt, dto.LastVerifiedAt);
        Assert.True(dto.IsEnabled);
    }
}
