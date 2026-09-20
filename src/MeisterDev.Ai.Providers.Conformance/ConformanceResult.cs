// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Conformance;

/// <summary>What one check concluded about one family.</summary>
public enum ConformanceOutcome
{
    /// <summary>The family satisfies the property the check states.</summary>
    Passed = 0,

    /// <summary>The family does not satisfy it. A host skips such a family; a build fails on it.</summary>
    Failed = 1,

    /// <summary>
    ///     The check could not run against this family, and the result carries why. A family is never refused
    ///     for one: a check that could not run is not evidence of a defect.
    /// </summary>
    NotApplicable = 2,
}

/// <summary>The outcome of one check against one family.</summary>
/// <param name="Check">The check's name, stable across versions so a failure can be looked up.</param>
/// <param name="Outcome">What the check concluded.</param>
/// <param name="Detail">
///     Why, for anything other than a pass. Written for whoever has to act on it: the add-in author reading
///     their own build output, or the operator reading why a family was skipped.
/// </param>
public sealed record ConformanceResult(string Check, ConformanceOutcome Outcome, string? Detail = null)
{
    // Held in a field so that an object initializer and a `with` expression go through the same check as the
    // constructor. Both write over what the constructor set, and a property with an accessor body cannot carry a
    // field initializer of its own.
    private readonly ConformanceOutcome _outcome = Defined(Outcome);

    /// <summary>
    ///     What the check concluded. A value outside the enumeration is refused wherever it is set: the host
    ///     reads a result by asking whether it failed, so an undefined outcome is neither a pass nor a failure
    ///     and admits a family no check actually cleared.
    /// </summary>
    public ConformanceOutcome Outcome
    {
        get => this._outcome;
        init => this._outcome = Defined(value);
    }

    /// <summary>Whether this result refuses the family.</summary>
    public bool IsFailure => this.Outcome == ConformanceOutcome.Failed;

    /// <summary>The family satisfies the check.</summary>
    /// <param name="check">The check's name.</param>
    public static ConformanceResult Pass(string check)
    {
        return new ConformanceResult(check, ConformanceOutcome.Passed);
    }

    /// <summary>The family does not satisfy the check.</summary>
    /// <param name="check">The check's name.</param>
    /// <param name="detail">What is wrong, and what to do about it.</param>
    public static ConformanceResult Fail(string check, string detail)
    {
        return new ConformanceResult(check, ConformanceOutcome.Failed, detail);
    }

    /// <summary>The check could not run against this family.</summary>
    /// <param name="check">The check's name.</param>
    /// <param name="reason">What the check needed and did not have.</param>
    public static ConformanceResult NotApplicable(string check, string reason)
    {
        return new ConformanceResult(check, ConformanceOutcome.NotApplicable, reason);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Detail is null ? $"{this.Check}: {this.Outcome}" : $"{this.Check}: {this.Outcome} - {this.Detail}";
    }

    private static ConformanceOutcome Defined(ConformanceOutcome outcome)
    {
        return Enum.IsDefined(outcome)
            ? outcome
            : throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "A conformance outcome is a member of this set. A value outside it is read as neither a pass nor "
                + "a failure, which admits a family without a check having cleared it.");
    }
}
