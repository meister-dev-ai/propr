// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class AiReviewOptionsRetiredKeysTests
{
    // A deployment configured for an earlier version may still set the retired screening and ranking options. The
    // review options still bind from such a configuration, keep the values the deployment set for current options,
    // and pass validation.
    [Fact]
    public void Bind_WithRetiredScreeningAndRankingKeys_KeepsCurrentValuesAndStaysValid()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CommentScreeningSimilarityThreshold"] = "0.4",
                    ["ImportanceRankingKeepTopN"] = "3",
                    ["ImportanceRankingMinScore"] = "7",
                    ["AiReview:ImportanceRankingKeepTopN"] = "3",
                    ["AI_IMPORTANCE_RANKING_KEEP_TOP_N"] = "3",
                    ["AI_MAX_REVIEW_ITERATIONS"] = "9",
                })
            .Build();
        var options = new AiReviewOptions();

        AiReviewOptionsBinder.Bind(options, configuration);

        Assert.Equal(9, options.MaxIterations);
        var results = new List<ValidationResult>();
        Assert.True(Validator.TryValidateObject(options, new ValidationContext(options), results, true), string.Join("; ", results));
    }
}
