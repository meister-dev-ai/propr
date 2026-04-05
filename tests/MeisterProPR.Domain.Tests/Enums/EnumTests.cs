// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Tests.Enums;

public class EnumTests
{
    [Fact]
    public void ChangeType_HasAddValue()
    {
        Assert.True(Enum.IsDefined(typeof(ChangeType), ChangeType.Add));
    }

    [Fact]
    public void ChangeType_HasDeleteValue()
    {
        Assert.True(Enum.IsDefined(typeof(ChangeType), ChangeType.Delete));
    }

    [Fact]
    public void ChangeType_HasEditValue()
    {
        Assert.True(Enum.IsDefined(typeof(ChangeType), ChangeType.Edit));
    }

    [Theory]
    [InlineData("Add")]
    [InlineData("Edit")]
    [InlineData("Delete")]
    public void ChangeType_ValuesHaveExpectedNames(string name)
    {
        Assert.True(Enum.TryParse<ChangeType>(name, out _));
    }

    [Fact]
    public void CommentSeverity_HasErrorValue()
    {
        Assert.True(Enum.IsDefined(typeof(CommentSeverity), CommentSeverity.Error));
    }

    [Fact]
    public void CommentSeverity_HasInfoValue()
    {
        Assert.True(Enum.IsDefined(typeof(CommentSeverity), CommentSeverity.Info));
    }

    [Fact]
    public void CommentSeverity_HasSuggestionValue()
    {
        Assert.True(Enum.IsDefined(typeof(CommentSeverity), CommentSeverity.Suggestion));
    }

    [Fact]
    public void CommentSeverity_HasWarningValue()
    {
        Assert.True(Enum.IsDefined(typeof(CommentSeverity), CommentSeverity.Warning));
    }

    [Theory]
    [InlineData("Info")]
    [InlineData("Warning")]
    [InlineData("Error")]
    [InlineData("Suggestion")]
    public void CommentSeverity_ValuesHaveExpectedNames(string name)
    {
        Assert.True(Enum.TryParse<CommentSeverity>(name, out _));
    }

    [Fact]
    public void JobStatus_HasCompletedValue()
    {
        Assert.True(Enum.IsDefined(typeof(JobStatus), JobStatus.Completed));
    }

    [Fact]
    public void JobStatus_HasFailedValue()
    {
        Assert.True(Enum.IsDefined(typeof(JobStatus), JobStatus.Failed));
    }

    [Fact]
    public void JobStatus_HasPendingValue()
    {
        Assert.True(Enum.IsDefined(typeof(JobStatus), JobStatus.Pending));
    }

    [Fact]
    public void JobStatus_HasProcessingValue()
    {
        Assert.True(Enum.IsDefined(typeof(JobStatus), JobStatus.Processing));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Processing")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    public void JobStatus_ValuesHaveExpectedNames(string name)
    {
        Assert.True(Enum.TryParse<JobStatus>(name, out _));
    }

    [Fact]
    public void CommentResolutionBehavior_HasDisabledValue()
    {
        Assert.True(Enum.IsDefined(typeof(CommentResolutionBehavior), CommentResolutionBehavior.Disabled));
    }

    [Fact]
    public void CommentResolutionBehavior_HasSilentValue()
    {
        Assert.True(Enum.IsDefined(typeof(CommentResolutionBehavior), CommentResolutionBehavior.Silent));
    }

    [Fact]
    public void CommentResolutionBehavior_HasWithReplyValue()
    {
        Assert.True(Enum.IsDefined(typeof(CommentResolutionBehavior), CommentResolutionBehavior.WithReply));
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Silent")]
    [InlineData("WithReply")]
    public void CommentResolutionBehavior_ValuesHaveExpectedNames(string name)
    {
        Assert.True(Enum.TryParse<CommentResolutionBehavior>(name, out _));
    }

    [Fact]
    public void CommentResolutionBehavior_SilentIsDefaultValue()
    {
        Assert.Equal(1, (int)CommentResolutionBehavior.Silent);
    }

    [Fact]
    public void CommentResolutionBehavior_DisabledIsZero()
    {
        Assert.Equal(0, (int)CommentResolutionBehavior.Disabled);
    }
}
