// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Generic;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeisterProPR.Infrastructure.Tests.Features.IdentityAndAccess;

public sealed class SessionPolicyFactoryTests
{
    [Fact]
    public void FromConfiguration_WhenKeysAbsent_UsesEightHourIdleAndSeventyTwoHourAbsoluteDefaults()
    {
        var configuration = BuildConfiguration();

        var policy = SessionPolicyFactory.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromHours(8), policy.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(72), policy.AbsoluteLifetime);
    }

    [Fact]
    public void FromConfiguration_WhenKeysSet_HonoursConfiguredValues()
    {
        var configuration = BuildConfiguration(
            (SessionPolicyFactory.IdleMinutesKey, "30"),
            (SessionPolicyFactory.AbsoluteHoursKey, "24"));

        var policy = SessionPolicyFactory.FromConfiguration(configuration);

        Assert.Equal(TimeSpan.FromMinutes(30), policy.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(24), policy.AbsoluteLifetime);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    public void FromConfiguration_WhenIdleValueIsNotPositiveInteger_Throws(string value)
    {
        var configuration = BuildConfiguration((SessionPolicyFactory.IdleMinutesKey, value));

        Assert.Throws<InvalidOperationException>(() => SessionPolicyFactory.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_WhenAbsoluteValueIsNotPositiveInteger_Throws()
    {
        var configuration = BuildConfiguration((SessionPolicyFactory.AbsoluteHoursKey, "0"));

        Assert.Throws<InvalidOperationException>(() => SessionPolicyFactory.FromConfiguration(configuration));
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] entries)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in entries)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
