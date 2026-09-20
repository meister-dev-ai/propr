// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Conformance;

/// <summary>
///     One property every provider family owes its caller, stated so it can be measured against any family.
/// </summary>
/// <remarks>
///     <para>
///         A check never throws. <see cref="Run" /> is where that is enforced, so an implementation can be
///         written as though the family behaves and an implementation cannot forget. The host runs these while
///         it starts, over code it did not compile, where a throw would take the process down or be read as a
///         load error.
///     </para>
///     <para>
///         The set of checks is the kit's, in <see cref="DriverConformance.Checks" />. This type is public so a
///         caller can run one check on its own and name it in its own output.
///     </para>
/// </remarks>
public abstract class DriverConformanceCheck
{
    /// <summary>
    ///     The longest a thrown message is carried into a result. A family writes its own exception messages and
    ///     one of them serialising a request body would otherwise fill the report and the log line quoting it.
    /// </summary>
    private const int MaximumThrownMessageLength = 400;

    /// <summary>
    ///     What this check is called wherever its outcome is reported. Stable across versions: it is what an
    ///     operator reads beside a skipped family and what an author searches for.
    /// </summary>
    public abstract string Name { get; }

    /// <summary>Measures one family, reporting anything it threw as a failure of this check.</summary>
    /// <param name="subject">The family to measure.</param>
    public ConformanceResult Run(ConformanceSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        try
        {
            return this.Evaluate(subject);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return ConformanceResult.Fail(
                this.NamedSafely(),
                $"The family threw a {exception.GetType().Name} instead of answering the check: "
                + Quoted(exception.Message));
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return this.Name;
    }

    /// <summary>Measures one family. Called through <see cref="Run" />, which contains anything this throws.</summary>
    /// <param name="subject">The family to measure.</param>
    protected abstract ConformanceResult Evaluate(ConformanceSubject subject);

    /// <summary>
    ///     A message a family wrote, bounded and with its control characters replaced.
    /// </summary>
    /// <remarks>
    ///     The text comes from code the host did not compile and goes into a result an operator reads and a log
    ///     line quoting it. A line break in it ends the log line and lets the rest be read as a further entry,
    ///     and a message carrying a serialised request would fill both. Nothing here can tell whether it also
    ///     carries a credential: a family that puts one in an exception message has put it there, and the check
    ///     reports what it was told. The host scrubs a family's own messages where it holds the values to scrub
    ///     for; this runs in an author's build as well, where it holds none.
    /// </remarks>
    /// <param name="message">The message as the family wrote it.</param>
    private static string Quoted(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "it said nothing.";
        }

        var flattened = new string([.. message.Select(character => char.IsControl(character) ? ' ' : character)]);

        return flattened.Length <= MaximumThrownMessageLength
            ? flattened
            : flattened[..MaximumThrownMessageLength] + "…";
    }

    // Read defensively, because this runs inside the handler that exists so a check never throws. Name is
    // abstract and a caller outside this assembly implements it, so a getter that throws would escape Run
    // through the one path written to contain everything.
    private string NamedSafely()
    {
        try
        {
            return string.IsNullOrWhiteSpace(this.Name) ? this.GetType().Name : this.Name;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return this.GetType().Name;
        }
    }
}
