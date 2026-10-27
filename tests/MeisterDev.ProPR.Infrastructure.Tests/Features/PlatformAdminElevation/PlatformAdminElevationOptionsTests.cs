// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Infrastructure.Features.PlatformAdminElevation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.PlatformAdminElevation;

/// <summary>
///     The longest elevation an installation permits comes from <c>MEISTER_PLATFORM_ADMIN_ELEVATION_MAX_MINUTES</c>. A
///     value ProPR cannot use fails the start, so the installation never runs with another limit than the one set.
/// </summary>
public sealed class PlatformAdminElevationOptionsTests
{
    [Theory]
    [InlineData(null, PlatformAdminElevationOptions.DefaultMaxDurationMinutes)]
    [InlineData("90", 90)]
    [InlineData(" 1440 ", 1440)]
    public void AWholeNumberFrom1To1440OrNoValueSetsTheMaximum(string? value, int expected)
    {
        Assert.Equal(expected, Resolve(value).MaxDurationMinutes);
    }

    [Theory]
    [InlineData("four hours")]
    [InlineData("90.5")]
    [InlineData("0")]
    [InlineData("1441")]
    public void AnyOtherValueFailsWithTheNameOfTheSetting(string value)
    {
        var failure = Assert.Throws<OptionsValidationException>(() => Resolve(value));

        Assert.Contains(PlatformAdminElevationOptions.MaxDurationMinutesKey, failure.Message, StringComparison.Ordinal);
    }

    private static PlatformAdminElevationOptions Resolve(string? value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PlatformAdminElevationOptions.MaxDurationMinutesKey] = value })
            .Build();
        using var provider = new ServiceCollection()
            .AddPlatformAdminElevationModule(configuration)
            .BuildServiceProvider();
        return provider.GetRequiredService<IOptions<PlatformAdminElevationOptions>>().Value;
    }
}
