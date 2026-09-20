// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     The tolerant read for a stored value of a host-owned enumeration. It stands where an enum parse used to,
///     so it has to accept everything that parse accepted and report, rather than throw, for everything it did
///     not.
/// </summary>
public sealed class AiVocabularyTests
{
    // Covers whatever each vocabulary names today, so a member added later is read back without this test being
    // touched. A member that does not resolve from the name it is written under would be stored and then
    // unreadable.
    [Fact]
    public void EveryMemberResolvesFromTheNameItIsStoredUnder()
    {
        AssertEveryMemberResolves<AiOperationKind>();
        AssertEveryMemberResolves<AiVerificationStatus>();
        AssertEveryMemberResolves<AiVerificationFailureCategory>();
    }

    // The enum parse this replaced ignored case and surrounding space. A row written in another casing, or a
    // value that arrives padded, names the same member; reading either as unrecognised would report a value the
    // build does understand.
    [Theory]
    [InlineData("embedding")]
    [InlineData("EMBEDDING")]
    [InlineData("  Embedding  ")]
    public void AValueResolvesRegardlessOfCaseAndSurroundingSpace(string stored)
    {
        var resolution = AiVocabulary.Resolve<AiOperationKind>(stored);

        Assert.True(resolution.TryGetValue(out var operationKind));
        Assert.Equal(AiOperationKind.Embedding, operationKind);

        // Carried trimmed: a caller reports the value and writes it back, so padding that survived the read
        // would reach the row and the operator.
        Assert.Equal(stored.Trim(), resolution.Name);
    }

    // A value this build has no member for is an outcome the caller handles: no exception, and no substituted
    // member. The value is carried so the caller can name what it could not resolve.
    [Fact]
    public void AValueThisBuildCannotNameIsReportedUnresolved()
    {
        var resolution = AiVocabulary.Resolve<AiOperationKind>("SomeOperationALaterBuildNames");

        Assert.False(resolution.IsResolved);
        Assert.False(resolution.TryGetValue(out _));
        Assert.Equal("SomeOperationALaterBuildNames", resolution.Name);
    }

    // An absent or blank value is a column that was never written, which names no member either.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnAbsentValueIsReportedUnresolved(string? stored)
    {
        Assert.False(AiVocabulary.Resolve<AiOperationKind>(stored).IsResolved);
    }

    // The enum parse this replaced accepted a number as the member carrying that value, and a comma-separated
    // list as the bitwise combination of two members. Neither names a stored vocabulary value, and the second
    // resolved to a member nobody wrote.
    [Theory]
    [InlineData("1")]
    [InlineData("99")]
    [InlineData("Chat,Embedding")]
    public void AValueThatNamesNoMemberIsReportedUnresolved(string stored)
    {
        Assert.False(AiVocabulary.Resolve<AiOperationKind>(stored).IsResolved);
    }

    // An unresolved outcome reports the vocabulary's default member, which a caller that keeps going puts in the
    // object it is building. IsResolved, not the member, separates that from a stored value that names the
    // default member.
    [Fact]
    public void AnUnresolvedValueReportsTheVocabularysDefaultMember()
    {
        AiVocabulary.Resolve<AiOperationKind>("SomethingElse").TryGetValue(out var operationKind);

        Assert.Equal(default, operationKind);
    }

    // Which member that default is decides what an unreadable row looks like to an operator. A vocabulary whose
    // zero is a specific answer turns a value this build cannot name — and a value nobody ever set — into a
    // confident wrong one: a verification failure blamed on the credential, or a credential reported usable
    // without anything having checked it.
    [Fact]
    public void AVocabularyThatCanBeUnknownHoldsTheUnknownMemberAtZero()
    {
        Assert.Equal(AiVerificationFailureCategory.Unknown, default);
        Assert.Equal(AiVerificationStatus.NeverVerified, default);
        Assert.Equal(AiCredentialHealth.Unreported, default);
    }

    // A resolution nobody produced reads as unresolved. Without this a caller that forgot to assign one would
    // get the first member and report a value that was never stored.
    [Fact]
    public void AResolutionThatWasNeverProducedIsUnresolved()
    {
        var resolution = default(AiVocabularyResolution<AiOperationKind>);

        Assert.False(resolution.IsResolved);
        Assert.False(resolution.TryGetValue(out _));
        Assert.Equal(string.Empty, resolution.Name);
    }

    // An enum can declare two names for one value, which is how a member keeps reading under its old name after
    // a rename. A lookup built from the values holds only the name ToString returns for it, so the other name —
    // one this build declares — would be reported as a value it cannot name.
    [Theory]
    [InlineData("Canonical")]
    [InlineData("Legacy")]
    public void EachNameOfAnAliasedMemberResolves(string stored)
    {
        var resolution = AiVocabulary.Resolve<RenamedVocabulary>(stored);

        Assert.True(resolution.TryGetValue(out var member));
        Assert.Equal(RenamedVocabulary.Canonical, member);
    }

    // Matching ignores case, so two names differing only by case name the same key. The first the enum reports
    // wins; the alternative is a failure out of the lookup's construction, which reaches the caller as a
    // type-initialisation exception rather than the unresolved outcome this resolver documents.
    [Theory]
    [InlineData("SigV4")]
    [InlineData("Sigv4")]
    [InlineData("sigv4")]
    public void TwoNamesDifferingOnlyByCaseResolveToTheFirstOneDeclared(string stored)
    {
        var resolution = AiVocabulary.Resolve<CaseCollidingVocabulary>(stored);

        Assert.True(resolution.TryGetValue(out var member));
        Assert.Equal(CaseCollidingVocabulary.SigV4, member);
    }

    // A vocabulary that renamed a member and kept the old name reading.
    private enum RenamedVocabulary
    {
        None = 0,
        Canonical = 1,
        Legacy = 1,
    }

    // A vocabulary whose two names a case-insensitive lookup cannot tell apart.
    private enum CaseCollidingVocabulary
    {
        None = 0,
        SigV4 = 1,
        Sigv4 = 2,
    }

    private static void AssertEveryMemberResolves<TEnum>()
        where TEnum : struct, Enum
    {
        foreach (var member in Enum.GetValues<TEnum>())
        {
            var resolution = AiVocabulary.Resolve<TEnum>(member.ToString());

            Assert.True(resolution.IsResolved);
            Assert.True(resolution.TryGetValue(out var resolved));
            Assert.Equal(member, resolved);
        }
    }
}
