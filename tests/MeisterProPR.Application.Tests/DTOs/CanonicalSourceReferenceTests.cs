// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.DTOs;

public sealed class CanonicalSourceReferenceTests
{
    [Fact]
    public void Constructor_TrimsProviderAndValue()
    {
        var reference = new CanonicalSourceReference(" azureDevOps ", " repo-123 ");

        Assert.Equal("azureDevOps", reference.Provider);
        Assert.Equal("repo-123", reference.Value);
    }

    [Fact]
    public void Constructor_Throws_WhenProviderMissing()
    {
        Assert.Throws<ArgumentException>(() => new CanonicalSourceReference(" ", "repo-123"));
    }
}
