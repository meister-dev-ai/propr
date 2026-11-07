// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     The bounds the host puts on what a provider family produces and on how long one of its runs may last.
/// </summary>
/// <remarks>
///     None of these reaches a database. What they are about is the arithmetic the host does on a family's own
///     values, which is where a wrong answer is silent: a message that keeps a credential, a cut that produces
///     text no column accepts, and a window read as unstated when it has already lapsed.
/// </remarks>
public sealed class ProviderHostBoundsTests
{
    // A key of the length OpenAI issues and a longer secret. The first characters of each occur nowhere else in the
    // messages the tests build, so finding them in a result means that a part of that value reached it.
    private const string EchoedKey = "sk-proj-4f9c2a7e1b8d3f6a0c5e9b2d";

    private const string ClientSecret = "cs-live-9d4b7e2a6c1f8e3d5a0b9c7e2f4a6d8b1c3e5f7a9b0d2c4e6f8a1b3c";

    // Eliding one value writes the elision text into the message. A second value that occurs inside that text
    // would be replaced within it, which destroys the marker an operator reads as "a credential was here" and
    // leaves the result looking like a value the family produced.
    [Fact]
    public void AValueOccurringInsideTheElisionTextIsNotMatchedWithinIt()
    {
        var scrubbed = ProviderMessageGuard.Sanitize("token was sk-live-abc123", ["sk-live-abc123", "red"]);

        Assert.Equal("token was [redacted]", scrubbed);
    }

    // The same value elsewhere in the message is still elided: what is not read again is the text the scrub
    // wrote, not the text the family did.
    [Fact]
    public void AValueOccurringInTheMessageItselfIsStillElided()
    {
        var scrubbed = ProviderMessageGuard.Sanitize("red token was sk-live-abc123", ["sk-live-abc123", "red"]);

        Assert.Equal("[redacted] token was [redacted]", scrubbed);
    }

    // At each position the longest value wins, so a value that contains a shorter one is elided whole rather
    // than leaving the remainder of it in place.
    [Fact]
    public void TheLongestValueAtEachPositionIsTheOneElided()
    {
        var scrubbed = ProviderMessageGuard.Sanitize("presented sk-live-abc123", ["sk-live-abc123", "sk-live"]);

        Assert.Equal("presented [redacted]", scrubbed);
    }

    [Fact]
    public void EveryOccurrenceOfAValueIsElided()
    {
        var scrubbed = ProviderMessageGuard.Sanitize("abab", ["ab"]);

        Assert.Equal("[redacted][redacted]", scrubbed);
    }

    // A cut between the halves of a surrogate pair emits a lone surrogate, which is not text: a text column
    // rejects it or stores a replacement character, and a JSON writer refuses the response it is in.
    [Fact]
    public void CuttingAMessageNeverEmitsHalfOfACharacter()
    {
        // Each emoji is a surrogate pair, so every odd-numbered cut would land between two halves.
        var message = string.Concat(Enumerable.Repeat("\U0001F600", 40));

        for (var maximumLength = 2; maximumLength <= message.Length; maximumLength++)
        {
            var capped = ProviderMessageGuard.Sanitize(message, maximumLength: maximumLength);

            Assert.True(capped.Length <= maximumLength, $"A cut at {maximumLength} produced {capped.Length} characters.");
            Assert.All(capped, character => Assert.False(char.IsSurrogate(character) && !IsPaired(capped, character)));
            Assert.DoesNotContain(capped, character => char.IsLowSurrogate(character) && capped.IndexOf(character) == 0);
        }
    }

    [Fact]
    public void ACutMessageSaysThatItWasCut()
    {
        var capped = ProviderMessageGuard.Sanitize(new string('a', 200), maximumLength: 20);

        Assert.Equal(20, capped.Length);
        Assert.EndsWith("…", capped, StringComparison.Ordinal);
    }

    // A provider can echo the presented key more than once. The second echo here starts two characters past the
    // cap, and the elision of the first echo makes the text 22 characters shorter, so a part of the second echo
    // that the scrub did not match whole would move inside the cap.
    [Fact]
    public void ARepeatedKeyCrossingTheCapLeavesNoPartOfItInTheCutMessage()
    {
        const int cap = ProviderMessageGuard.MaximumMessageLength;
        var message = EchoedKey + Dots(cap + 2 - EchoedKey.Length) + EchoedKey + Dots(200);

        var sanitized = ProviderMessageGuard.Sanitize(message, [EchoedKey]);

        Assert.DoesNotContain(EchoedKey[..4], sanitized, StringComparison.Ordinal);
        Assert.Equal(cap, sanitized.Length);
        Assert.EndsWith("…", sanitized, StringComparison.Ordinal);
    }

    // The same with two echoes before the third and a short tail. The two elisions make the scrubbed message
    // shorter than the cap, so it is returned without a cut, and a part of the third echo would be returned with
    // it.
    [Fact]
    public void ARepeatedKeyCrossingTheCapLeavesNoPartOfItInAMessageThatFitsOnceScrubbed()
    {
        var filler = Dots(ProviderMessageGuard.MaximumMessageLength + 2 - (2 * EchoedKey.Length));
        var message = EchoedKey + EchoedKey + filler + EchoedKey + "!";

        var sanitized = ProviderMessageGuard.Sanitize(message, [EchoedKey]);

        Assert.Equal("[redacted][redacted]" + filler + "[redacted]!", sanitized);
    }

    // A scrub that bounds its input first keeps the cap plus the length of the longest held value. The key here
    // starts 31 characters before that point, so such a cut keeps every character of the key but the last, and
    // the elision of the longer secret before it moves that part inside the cap.
    [Fact]
    public void AKeyStraddlingTheCapPlusTheLongestHeldValueLeavesNoPartOfIt()
    {
        const int cap = ProviderMessageGuard.MaximumMessageLength;
        var start = cap + ClientSecret.Length + 1 - (EchoedKey.Length - 1);
        var message = ClientSecret + Dots(start - ClientSecret.Length) + EchoedKey + Dots(200);

        var sanitized = ProviderMessageGuard.Sanitize(message, [EchoedKey, ClientSecret]);

        Assert.DoesNotContain(EchoedKey[..4], sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientSecret[..4], sanitized, StringComparison.Ordinal);
        Assert.Equal(cap, sanitized.Length);
        Assert.EndsWith("…", sanitized, StringComparison.Ordinal);
    }

    // A window at or below zero is one that has already lapsed. Read as unstated and granted the maximum, an
    // expired invocation gets a fully live signal and a context capable of acting on it for half an hour.
    [Fact]
    public void AnInvocationWhoseWindowHasLapsedGetsASignalThatIsAlreadyTripped()
    {
        using var cancellations = new ProviderInvocationCancellation();

        Assert.True(cancellations.Open(Guid.NewGuid(), TimeSpan.Zero).IsCancellationRequested);
        Assert.True(cancellations.Open(Guid.NewGuid(), TimeSpan.FromMinutes(-5)).IsCancellationRequested);
    }

    [Fact]
    public void AWindowLongerThanTheHostMaximumIsCappedRatherThanRefused()
    {
        using var cancellations = new ProviderInvocationCancellation();

        var token = cancellations.Open(Guid.NewGuid(), ProviderHostLimits.MaximumInvocationWindow * 2);

        Assert.False(token.IsCancellationRequested);
        Assert.True(token.CanBeCanceled);
    }

    // The guard elides the values it is handed and nothing else, so a message scrubbed against none is the
    // family's own text. Several providers quote the presented key back in a refusal and this message is stored
    // and shown, so a credential the host cannot read costs the message rather than the credential.
    [Fact]
    public async Task AMessageThatCannotBeScrubbedIsReplaced()
    {
        var scrubbed = await ProviderReportedMessage.ScrubbedAsync(
            "the provider refused sk-live-0123456789",
            _ => throw new InvalidOperationException("this payload was written by another family"));

        Assert.Equal(ProviderReportedMessage.Withheld, scrubbed);
        Assert.DoesNotContain("sk-live-0123456789", scrubbed, StringComparison.Ordinal);
    }

    // The stored credential is read from the database and the submitted values are in hand. A read that fails
    // does not take the ones the host is already holding with it.
    [Fact]
    public async Task ASubmittedSecretStillScrubsWhenTheStoredCredentialCannotBeRead()
    {
        var secrets = ProviderActionDispatcher.Combined(
            _ => throw new InvalidOperationException("this payload was written by another family"),
            ["pasted-authorization-code"]);

        var scrubbed = await ProviderReportedMessage.ScrubbedAsync(
            "the provider echoed pasted-authorization-code back",
            secrets);

        Assert.DoesNotContain("pasted-authorization-code", scrubbed, StringComparison.Ordinal);
        Assert.Contains("the provider echoed", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancellationWhileReadingTheCredentialIsNotSwallowed()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => ProviderReportedMessage.ScrubbedAsync(
            "anything", _ => throw new OperationCanceledException()));
    }

    private static string Dots(int count)
    {
        return new string('.', count);
    }

    private static bool IsPaired(string text, char surrogate)
    {
        var index = text.IndexOf(surrogate);

        return char.IsHighSurrogate(surrogate)
            ? index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
            : index > 0 && char.IsHighSurrogate(text[index - 1]);
    }
}
