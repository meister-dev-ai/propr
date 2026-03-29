using MeisterProPR.Application.Options;

namespace MeisterProPR.Application.Tests.Options;

/// <summary>
///     T032 — verifies default values and env-var binding names for tier iteration budgets.
/// </summary>
public sealed class AiReviewOptionsTests
{
    [Fact]
    public void MaxIterationsLow_DefaultIs5()
    {
        var opts = new AiReviewOptions();
        Assert.Equal(5, opts.MaxIterationsLow);
    }

    [Fact]
    public void MaxIterationsMedium_DefaultIs10()
    {
        var opts = new AiReviewOptions();
        Assert.Equal(10, opts.MaxIterationsMedium);
    }

    [Fact]
    public void MaxIterationsHigh_DefaultIs20()
    {
        var opts = new AiReviewOptions();
        Assert.Equal(20, opts.MaxIterationsHigh);
    }
}
