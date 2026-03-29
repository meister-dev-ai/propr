using FluentValidation;
using MeisterProPR.Api.Controllers;

namespace MeisterProPR.Api.Validators;

/// <summary>Validates <see cref="CreateAdminCrawlConfigRequest" /> before a new admin crawl configuration is created.</summary>
public sealed class CreateAdminCrawlConfigRequestValidator : AbstractValidator<CreateAdminCrawlConfigRequest>
{
    /// <summary>Initializes a new instance of <see cref="CreateAdminCrawlConfigRequestValidator" />.</summary>
    public CreateAdminCrawlConfigRequestValidator()
    {
        this.RuleFor(r => r.ClientId)
            .NotEmpty()
            .WithMessage("ClientId is required.");

        this.RuleFor(r => r.OrganizationUrl)
            .NotEmpty()
            .WithMessage("OrganizationUrl is required.")
            .Must(static url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .WithMessage("OrganizationUrl must be a valid URL.");

        this.RuleFor(r => r.ProjectId)
            .NotEmpty()
            .WithMessage("ProjectId is required.");

        this.RuleFor(r => r.CrawlIntervalSeconds)
            .GreaterThanOrEqualTo(10)
            .WithMessage("CrawlIntervalSeconds must be >= 10.");
    }
}
