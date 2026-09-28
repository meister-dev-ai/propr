// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     Verifies how the AI-generated marker is rendered onto a body and taken off it again.
/// </summary>
public sealed class PostedCommentComposerTests
{
    [Fact]
    public void Text_WithoutConfiguredWording_IsTheDefaultWordingEmphasized()
    {
        var composer = Composer(null);

        Assert.Equal("*" + PostedCommentMarkerOptions.DefaultMarker + "*", composer.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Text_WithBlankConfiguredWording_IsTheDefaultWordingEmphasized(string configured)
    {
        var composer = Composer(configured);

        Assert.Equal("*" + PostedCommentMarkerOptions.DefaultMarker + "*", composer.Text);
    }

    [Fact]
    public void Text_WithConfiguredWording_IsThatWordingEmphasizedAndNothingElse()
    {
        var composer = Composer("Erzeugt von ProPR.");

        Assert.Equal("*Erzeugt von ProPR.*", composer.Text);
    }

    [Fact]
    public void Text_WithConfiguredWordingPaddedWithSpaces_DropsThePadding()
    {
        var composer = Composer("  Erzeugt von ProPR.  ");

        Assert.Equal("*Erzeugt von ProPR.*", composer.Text);
    }

    [Fact]
    public void Append_PutsTheMarkerOnItsOwnLineAfterABlankLine()
    {
        var composer = Composer(null);

        var marked = composer.Append("A finding about a null check.");

        Assert.Equal("A finding about a null check.\n\n" + composer.Text, marked);
    }

    [Fact]
    public void Append_ToAnAlreadyMarkedBody_ChangesNothing()
    {
        var composer = Composer(null);
        var marked = composer.Append("A finding about a null check.");

        Assert.Equal(marked, composer.Append(marked));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Append_ToABodyWithNothingInIt_ReturnsTheMarkerAlone(string? body)
    {
        var composer = Composer(null);

        Assert.Equal(composer.Text, composer.Append(body));
    }

    [Fact]
    public void Append_ToABodyEndingInAQuotedMarker_AddsTheMarker()
    {
        var composer = Composer(null);
        var quoting = "The reviewer wrote:\n\n> " + composer.Text;

        var marked = composer.Append(quoting);

        Assert.Equal(quoting + "\n\n" + composer.Text, marked);
    }

    [Fact]
    public void Strip_ReturnsTheBodyTheMarkerWasAppendedTo()
    {
        var composer = Composer(null);
        const string body = "A finding about a null check.\n\nWith a second paragraph.";

        Assert.Equal(body, composer.Strip(composer.Append(body)));
    }

    [Fact]
    public void Strip_LeavesAnUnmarkedBodyAlone()
    {
        var composer = Composer(null);
        const string body = "A finding posted before the marker existed.";

        Assert.Equal(body, composer.Strip(body));
    }

    [Fact]
    public void Strip_RemovesTheDefaultMarkerAfterTheWordingWasChanged()
    {
        var posted = Composer(null).Append("A finding about a null check.");
        var composer = Composer("Erzeugt von ProPR.");

        Assert.Equal("A finding about a null check.", composer.Strip(posted));
    }

    [Fact]
    public void Strip_OfNull_ReturnsAnEmptyString()
    {
        Assert.Equal(string.Empty, Composer(null).Strip(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Strip_OfABodyWithNothingInIt_ReturnsItUnchanged(string body)
    {
        Assert.Equal(body, Composer(null).Strip(body));
    }

    [Fact]
    public void Strip_OfTheMarkerAlone_ReturnsAnEmptyString()
    {
        var composer = Composer(null);

        Assert.Equal(string.Empty, composer.Strip(composer.Text));
    }

    [Fact]
    public void Strip_LeavesAQuotedMarkerInPlace()
    {
        var composer = Composer(null);
        var quoting = "The reviewer wrote:\n\n> " + composer.Text;

        Assert.Equal(quoting, composer.Strip(quoting));
    }

    private static IPostedCommentComposer Composer(string? marker)
    {
        return new PostedCommentComposer(Microsoft.Extensions.Options.Options.Create(new PostedCommentMarkerOptions { Marker = marker }));
    }
}
