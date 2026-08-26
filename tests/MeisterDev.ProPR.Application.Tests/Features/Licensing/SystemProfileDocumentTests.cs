// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Support;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     The canonical rendering of the stable components and the hash over it. Two observations of one
///     installation have to agree on both, whichever replica made them and in whatever order the components
///     were collected.
/// </summary>
public sealed class SystemProfileDocumentTests
{
    [Fact]
    public void TheRendering_WritesEveryMemberInOrdinalOrderWithoutWhitespace()
    {
        var json = SystemProfileDocument.Render(FullComponents());

        Assert.Equal(
            """
            {"databaseName":"propr","databaseOid":16401,"identityCreatedAtUnixSeconds":1755000000,"postgresSystemIdentifier":"7412345678901234567","scmHostHashes":["1a","2b"]}
            """,
            json);
    }

    // Absence is part of the identity: an installation whose role may not read the cluster identifier is not
    // the same installation as one that reports a value there.
    [Fact]
    public void AnAbsentComponent_IsWrittenAsNullRatherThanOmitted()
    {
        var json = SystemProfileDocument.Render(new SystemProfileStableComponents());

        Assert.Equal(
            """
            {"databaseName":null,"databaseOid":null,"identityCreatedAtUnixSeconds":null,"postgresSystemIdentifier":null,"scmHostHashes":null}
            """,
            json);
    }

    [Fact]
    public void AnAbsentComponentAndAPresentOne_HashDifferently()
    {
        var absent = FullComponents() with { PostgresSystemIdentifier = null };

        Assert.NotEqual(SystemProfileDocument.Hash(FullComponents()), SystemProfileDocument.Hash(absent));
    }

    // No license means no salt, which is not the same as a license with no configured host.
    [Fact]
    public void AnAbsentHostHashSet_HashesDifferentlyFromAnEmptyOne()
    {
        var absent = FullComponents() with { ScmHostHashes = null };
        var empty = FullComponents() with { ScmHostHashes = [] };

        Assert.NotEqual(SystemProfileDocument.Hash(absent), SystemProfileDocument.Hash(empty));
    }

    // The hosts come out of the database in whatever order it returns them, and a host configured twice comes
    // out twice. Neither is a property of the installation.
    [Fact]
    public void TheHostHashes_AreWrittenAsASortedSet()
    {
        var components = FullComponents() with { ScmHostHashes = ["2b", "1a", "2b"] };

        Assert.Equal(SystemProfileDocument.Render(FullComponents()), SystemProfileDocument.Render(components));
    }

    [Fact]
    public void TheHash_IsTheSha256OfTheRenderingInLowerCaseHex()
    {
        var components = FullComponents();

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SystemProfileDocument.Render(components))));

        Assert.Equal(expected, SystemProfileDocument.Hash(components));
    }

    // The installation is re-observed on every sweep. A hash that moved on its own would report drift on an
    // installation that has not moved.
    [Fact]
    public void UnchangedComponents_HashTheSameOnEveryObservation()
    {
        var first = SystemProfileDocument.Hash(FullComponents());
        var second = SystemProfileDocument.Hash(FullComponents());

        Assert.Equal(first, second);
    }

    [Fact]
    public void TheDiff_NamesEveryComponentThatChanged()
    {
        var changed = FullComponents() with
        {
            DatabaseName = "propr_restored",
            DatabaseOid = 20000,
            ScmHostHashes = ["1a"],
        };

        Assert.Equal(
            ["databaseName", "databaseOid", "scmHostHashes"],
            SystemProfileDocument.Diff(FullComponents(), changed));
    }

    [Fact]
    public void TheDiff_IsEmptyWhenOnlyTheOrderOfTheHostHashesDiffers()
    {
        var reordered = FullComponents() with { ScmHostHashes = ["2b", "1a"] };

        Assert.Empty(SystemProfileDocument.Diff(FullComponents(), reordered));
    }

    [Fact]
    public void TheDiff_NamesAComponentThatBecameAbsent()
    {
        var absent = FullComponents() with { ScmHostHashes = null };

        Assert.Equal(["scmHostHashes"], SystemProfileDocument.Diff(FullComponents(), absent));
    }

    private static SystemProfileStableComponents FullComponents() => new()
    {
        PostgresSystemIdentifier = "7412345678901234567",
        DatabaseName = "propr",
        DatabaseOid = 16401,
        IdentityCreatedAtUnixSeconds = 1755000000,
        ScmHostHashes = ["1a", "2b"],
    };
}
