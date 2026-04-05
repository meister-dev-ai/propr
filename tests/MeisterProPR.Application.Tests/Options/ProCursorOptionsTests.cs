// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;
using MeisterProPR.Application.Options;

namespace MeisterProPR.Application.Tests.Options;

/// <summary>
///     Verifies ProCursor option defaults and DataAnnotations validation rules.
/// </summary>
public sealed class ProCursorOptionsTests
{
    [Fact]
    public void MaxIndexConcurrency_DefaultIs2()
    {
        var options = new ProCursorOptions();
        Assert.Equal(2, options.MaxIndexConcurrency);
    }

    [Fact]
    public void MaxSourcesPerQuery_DefaultIs20()
    {
        var options = new ProCursorOptions();
        Assert.Equal(20, options.MaxSourcesPerQuery);
    }

    [Fact]
    public void EmbeddingDimensions_DefaultIs1536()
    {
        var options = new ProCursorOptions();
        Assert.Equal(1536, options.EmbeddingDimensions);
    }

    [Fact]
    public void DefaultOptions_PassValidation()
    {
        var options = new ProCursorOptions();
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, true);

        Assert.True(isValid, string.Join(", ", results.Select(result => result.ErrorMessage)));
    }

    [Fact]
    public void MaxQueryResults_Zero_FailsValidation()
    {
        var options = new ProCursorOptions { MaxQueryResults = 0 };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, true);

        Assert.False(isValid);
        Assert.Contains(
            results,
            result => result.MemberNames.Contains(nameof(ProCursorOptions.MaxQueryResults)));
    }

    [Fact]
    public void MaxSourcesPerQuery_Zero_FailsValidation()
    {
        var options = new ProCursorOptions { MaxSourcesPerQuery = 0 };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, true);

        Assert.False(isValid);
        Assert.Contains(
            results,
            result => result.MemberNames.Contains(nameof(ProCursorOptions.MaxSourcesPerQuery)));
    }
}
