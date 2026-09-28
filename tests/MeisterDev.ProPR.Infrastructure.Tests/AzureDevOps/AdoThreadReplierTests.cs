// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoThreadReplierTests
{
    [Fact]
    public void FormatReplyText_PreservesReadableQuotesWhileNeutralizingUnsafeMarkup()
    {
        const string input = "Run dotnet \"$ProCursorDll\" after removing <script>alert('xss')</script>.";

        var reply = AdoThreadReplier.FormatReplyText(input, TestPostedCommentComposer.Default);

        Assert.Contains("\"$ProCursorDll\"", reply);
        Assert.DoesNotContain("&quot;", reply);
        Assert.Equal(-1, reply.IndexOf("<script>", StringComparison.Ordinal));
        Assert.Contains("<\u200Bscript>", reply);
    }

    [Fact]
    public void FormatReplyText_EndsWithTheMarker()
    {
        var reply = AdoThreadReplier.FormatReplyText(
            "Fixed in the latest push.",
            TestPostedCommentComposer.Default);

        Assert.StartsWith("Fixed in the latest push.", reply, StringComparison.Ordinal);
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, reply, StringComparison.Ordinal);
    }
}
