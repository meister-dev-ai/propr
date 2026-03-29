using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Tests.Enums;

/// <summary>Tests for <see cref="JobStatus" /> enum values — documents the full state-machine set.</summary>
public class JobStatusTests
{
    [Fact]
    public void JobStatus_Cancelled_IsDefined()
    {
        Assert.True(Enum.IsDefined(typeof(JobStatus), JobStatus.Cancelled));
    }

    [Fact]
    public void JobStatus_Cancelled_HasValueFour()
    {
        Assert.Equal(4, (int)JobStatus.Cancelled);
    }

    [Fact]
    public void JobStatus_Cancelled_IsNotEqualToFailed()
    {
        Assert.NotEqual(JobStatus.Failed, JobStatus.Cancelled);
    }

    [Fact]
    public void JobStatus_Cancelled_HasExpectedName()
    {
        Assert.Equal("Cancelled", Enum.GetName(typeof(JobStatus), JobStatus.Cancelled));
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Processing)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void JobStatus_AllValues_AreDefined(JobStatus status)
    {
        Assert.True(Enum.IsDefined(typeof(JobStatus), status));
    }
}
