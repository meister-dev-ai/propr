// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     Runs the two checks a submitted declared value passes, in the order that keeps one beneath the other.
/// </summary>
/// <remarks>
///     The installation's egress rules go first and are kept whatever the family answers, so a family can refuse
///     more than the installation does and can never admit what it refuses. The family's own validator runs
///     second, and its message reaches the operator against the field it names.
/// </remarks>
internal static class DeclaredValueValidation
{
    /// <summary>
    ///     The most refusals reported from one submission, counting the installation's own and the family's.
    /// </summary>
    /// <remarks>
    ///     A family declares far fewer fields than this, so a submission that reaches the cap has a family
    ///     returning more refusals than it has fields to refuse.
    /// </remarks>
    private const int MaximumRefusals = 100;

    /// <summary>What is wrong with the submitted declared values, keyed by declared field name.</summary>
    /// <param name="driver">The family the connection is configured against.</param>
    /// <param name="submission">The values the request carried, as the host collected them.</param>
    /// <param name="egressPolicy">What this installation permits an address to reach.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Refusals(
        IAiProviderDriver driver,
        DeclaredValueSubmission submission,
        EgressUrlPolicy egressPolicy)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(submission);

        var entered = new Dictionary<string, string>(submission.Settings, StringComparer.Ordinal);
        foreach (var (name, value) in submission.Secrets)
        {
            entered[name] = value;
        }

        // Capped here as well as below. The floor answers once per declared address field, and how many a family
        // declares is the family's own number, so a declaration with a thousand of them fills the response
        // before the loop below has a cap to apply.
        var refusals = new List<KeyValuePair<string, string>>(
            DeclaredUrlFloor.FindRefusedAddressFields(driver.Declaration, entered, egressPolicy).Take(MaximumRefusals));

        // The family sees every value the operator entered, including the secret-marked ones, because a rule
        // about a credential's shape is the family's to state. It is asked after the floor so a value the
        // installation already refused stays refused whatever it answers.
        //
        // What comes back is capped and scrubbed, because a family handed the operator's credential to validate
        // is one quoting position away from putting it in the message it returns: "'sk-live-…' is not a project
        // key" reaches the form, the model-state response and any log line that renders it. The scrub runs
        // against the secret-marked values of this submission alone, which are exactly the ones the family was
        // given; a non-secret value is left intact, because it is usually what the message is about.
        var submittedSecrets = submission.Secrets.Values;

        // A refusal reaches the form as a model-state key, and a form can only place one against a field it
        // renders. A refusal naming something the family never declared has nowhere to go: it is reported at the
        // top of the form with no input beside it, or it is dropped, and either way the operator is told a value
        // is wrong without being shown which. The count is capped for the same reason the strings are: what comes
        // back is a family's own output, and a family returning one refusal per row of a response would fill the
        // response with them.
        var declaredNames = driver.Declaration.Fields
            .Select(declared => declared.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (field, message) in driver.ValidateDeclaredValues(entered))
        {
            // Read before the add rather than after it, so the cap is the count and not one past it.
            if (refusals.Count >= MaximumRefusals)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(message) || !declaredNames.Contains(field))
            {
                continue;
            }

            // The name is the key the form binds by, so it goes through unchanged. Sanitising it could shorten
            // or rewrite it, and a refusal under a key no input carries attaches to nothing and is never shown.
            // It is safe to pass on: the filter above admits only names the family declared, and the response is
            // JSON, which escapes whatever a name contains. Only the message is scrubbed.
            refusals.Add(new KeyValuePair<string, string>(field, ProviderMessageGuard.Sanitize(message, submittedSecrets)));
        }

        return refusals;
    }
}
