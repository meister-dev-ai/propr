// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;
using MeisterDev.ProPR.Application.Options;

namespace MeisterDev.ProPR.Application.Tests.Options;

/// <summary>
///     The advertised replica address is validated at startup, because a replica that advertises a bad
///     address poisons every job it grants: each runner would either leak its credential to it or hand
///     every lease straight back.
/// </summary>
public sealed class ReviewLeaseOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("https://replica-2.internal:8443")]
    [InlineData("http://localhost:8080")]
    public void AnAbsentSecureOrLoopbackAdvertisedUrl_Validates(string? advertised)
    {
        var options = new ReviewLeaseOptions { AdvertisedRunnerUrl = advertised };

        Assert.Empty(Validate(options));
    }

    [Theory]
    [InlineData("http://replica-2.internal:8080")]
    [InlineData("not a url")]
    public void AnInsecureOrMalformedAdvertisedUrl_FailsAtStartup(string advertised)
    {
        var options = new ReviewLeaseOptions { AdvertisedRunnerUrl = advertised };

        var failure = Assert.Single(Validate(options));
        Assert.Contains("RUNNER_ADVERTISED_URL", failure.ErrorMessage!, StringComparison.Ordinal);
    }

    private static List<ValidationResult> Validate(ReviewLeaseOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
