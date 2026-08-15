// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using MeisterDev.ProPR.Api.Controllers;

namespace MeisterDev.ProPR.Api.Validators;

/// <summary>Validates <see cref="CreateMentionConfigRequest" /> before a mention configuration is created.</summary>
/// <remarks>
///     The scan reports a configuration it cannot use by logging and moving on, so anything unusable that
///     gets stored looks to an operator exactly like a configuration that answers nothing. Refusing it here
///     is the only place they are told.
/// </remarks>
public sealed class CreateMentionConfigRequestValidator : AbstractValidator<CreateMentionConfigRequest>
{
    /// <summary>Initializes a new instance of <see cref="CreateMentionConfigRequestValidator" />.</summary>
    public CreateMentionConfigRequestValidator()
    {
        this.RuleFor(r => r.ClientId)
            .NotEmpty()
            .WithMessage("ClientId is required.");

        // Two rules rather than one chain. A When clause applies to every rule preceding it in the same
        // chain, so guarding the URL check with "when it is not blank" would switch off the NotEmpty check
        // for exactly the blank values it exists to catch, and a null scope path would reach the database.
        this.RuleFor(r => r.ProviderScopePath)
            .NotEmpty()
            .WithMessage("ProviderScopePath is required.");

        // Resolving the reviewer identity builds a ProviderHostRef, which rejects anything that is not an
        // absolute URL by throwing inside the scan where nobody sees it.
        this.RuleFor(r => r.ProviderScopePath)
            .Must(static url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .WithMessage("ProviderScopePath must be a valid absolute URL.")
            .When(r => !string.IsNullOrWhiteSpace(r.ProviderScopePath));

        this.RuleFor(r => r.ProviderProjectKey)
            .NotEmpty()
            .WithMessage("ProviderProjectKey is required.");

        this.RuleFor(r => r.ScanIntervalSeconds)
            .GreaterThanOrEqualTo(10)
            .WithMessage("ScanIntervalSeconds must be >= 10.")
            .LessThanOrEqualTo(86_400)
            .WithMessage("ScanIntervalSeconds must be at most 86400 (one day).")
            .When(r => r.ScanIntervalSeconds.HasValue);

        this.RuleFor(r => r.RepoFilters)
            .NotEmpty()
            .WithMessage("A mention configuration must name at least one repository.");

        // A JSON array may carry a null element whatever the element type says, and SetValidator skips a
        // null child rather than rejecting it, which would let the null reach the controller and be
        // dereferenced there.
        this.RuleForEach(r => r.RepoFilters)
            .NotNull()
            .WithMessage("A repository entry must not be null.")
            .SetValidator(new MentionRepoFilterRequestValidator()!);
    }
}

/// <summary>Validates one repository entry in a mention configuration request.</summary>
public sealed class MentionRepoFilterRequestValidator : AbstractValidator<MentionRepoFilterRequest>
{
    /// <summary>Initializes a new instance of <see cref="MentionRepoFilterRequestValidator" />.</summary>
    public MentionRepoFilterRequestValidator()
    {
        this.RuleFor(r => r.RepositoryId)
            .NotEmpty()
            .WithMessage("RepositoryId is required.")
            .MaximumLength(512)
            .WithMessage("RepositoryId must be at most 512 characters.");

        this.RuleFor(r => r.DisplayName)
            .MaximumLength(256)
            .WithMessage("DisplayName must be at most 256 characters.");

        this.RuleFor(r => r.CanonicalSourceRef)
            .MaximumLength(512)
            .WithMessage("CanonicalSourceRef must be at most 512 characters.");

        this.RuleFor(r => r.SourceProvider)
            .MaximumLength(64)
            .WithMessage("SourceProvider must be at most 64 characters.");
    }
}
