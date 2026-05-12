// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoThreadReplierTests
{
    [Fact]
    public void FormatReplyText_PreservesReadableQuotesWhileNeutralizingUnsafeMarkup()
    {
        const string input = "Run dotnet \"$ProCursorDll\" after removing <script>alert('xss')</script>.";

        var reply = AdoThreadReplier.FormatReplyText(input);

        Assert.Contains("\"$ProCursorDll\"", reply);
        Assert.DoesNotContain("&quot;", reply);
        Assert.Equal(-1, reply.IndexOf("<script>", StringComparison.Ordinal));
        Assert.Contains("<\u200Bscript>", reply);
    }
}
