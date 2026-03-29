using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="PatchAdminCrawlConfigRequest" /> before a crawl configuration is updated by an admin.</summary>
public sealed class PatchAdminCrawlConfigRequestValidator : AbstractValidator<PatchAdminCrawlConfigRequest>
{
    /// <summary>Initializes a new instance of <see cref="PatchAdminCrawlConfigRequestValidator" />.</summary>
    public PatchAdminCrawlConfigRequestValidator()
    {
        this.RuleFor(r => r.CrawlIntervalSeconds)
            .GreaterThanOrEqualTo(10)
            .WithMessage("CrawlIntervalSeconds must be >= 10.")
            .When(r => r.CrawlIntervalSeconds.HasValue);
    }
}
