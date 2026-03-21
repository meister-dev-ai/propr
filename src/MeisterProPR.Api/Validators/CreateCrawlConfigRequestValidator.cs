using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="CreateCrawlConfigRequest" /> before a crawl configuration is added.</summary>
public sealed class CreateCrawlConfigRequestValidator : AbstractValidator<CreateCrawlConfigRequest>
{
    /// <summary>Initializes a new instance of <see cref="CreateCrawlConfigRequestValidator" />.</summary>
    public CreateCrawlConfigRequestValidator()
    {
        this.RuleFor(r => r.OrganizationUrl)
            .NotEmpty()
            .WithMessage("OrganizationUrl is required.");

        this.RuleFor(r => r.ProjectId)
            .NotEmpty()
            .WithMessage("ProjectId is required.");

        this.RuleFor(r => r.CrawlIntervalSeconds)
            .GreaterThanOrEqualTo(10)
            .WithMessage("CrawlIntervalSeconds must be >= 10.");
    }
}
