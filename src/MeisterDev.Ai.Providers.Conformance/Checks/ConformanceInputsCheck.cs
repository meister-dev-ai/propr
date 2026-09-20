// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Conformance.Checks;

/// <summary>
///     The family states the inputs the checks cannot derive from it.
/// </summary>
/// <remarks>
///     A family that leaves the authentication mode empty would be measured against a blank credential, and every
///     check below it would report the family refusing something the checks never gave it. This check names the
///     missing input, so the remedy is the stated inputs and not the driver.
/// </remarks>
internal sealed class ConformanceInputsCheck : DriverConformanceCheck
{
    /// <inheritdoc />
    public override string Name => "conformance-inputs";

    /// <inheritdoc />
    protected override ConformanceResult Evaluate(ConformanceSubject subject)
    {
        var inputs = subject.ResolvedInputs;

        if (string.IsNullOrWhiteSpace(inputs.CredentialAuthMode))
        {
            return ConformanceResult.Fail(
                this.Name,
                $"The family states no '{nameof(ProviderConformanceInputs.CredentialAuthMode)}'. The checks read "
                + "the credential fields of one authentication mode, and there is no way to pick one from the "
                + "driver on the family's behalf.");
        }

        return ConformanceResult.Pass(this.Name);
    }
}
