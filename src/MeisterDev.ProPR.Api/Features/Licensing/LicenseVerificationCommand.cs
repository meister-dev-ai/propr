// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Licensing;

namespace MeisterDev.ProPR.Api.Features.Licensing;

/// <summary>
///     The <c>--verify-license</c> entrypoint mode: reads a license document, verifies it against the trust
///     anchor compiled into this build, reports the outcome on one line, and exits with a code a script can read.
///     <para>
///         The mode exists so a released image can be checked from outside itself. Running it with a license the
///         publisher issued establishes that the image carries the publisher's anchor and verifies a license
///         signed under it end to end, which is what no test in this repository can establish: the suites sign
///         with chains they generate, so they can only cover the refusal paths. The release workflow runs this
///         mode inside the built image, and that run is the proof of the accepting path.
///     </para>
///     <para>
///         Nothing here opens a database or a socket, and the host is never built: the trust anchor is an
///         embedded resource, and the document arrives as a file or on standard input. The mode therefore runs
///         in an image with no configuration at all.
///     </para>
/// </summary>
internal static class LicenseVerificationCommand
{
    /// <summary>The entrypoint flag, followed by the document's path.</summary>
    internal const string Flag = "--verify-license";

    /// <summary>The path value that reads the document from standard input.</summary>
    internal const string StandardInputPath = "-";

    /// <summary>The document comes from a signer this build accepts and its term is in force.</summary>
    internal const int VerifiedExitCode = 0;

    /// <summary>
    ///     The document was refused: it did not verify, or it verified and its term is outside the window in
    ///     which it grants anything.
    /// </summary>
    internal const int RefusedExitCode = 2;

    /// <summary>The flag was used wrongly, or the document could not be read. Nothing was verified.</summary>
    internal const int UsageExitCode = 3;

    /// <summary>
    ///     Verification could not be carried out. Nothing was verified, and the reason is not a property of the
    ///     document.
    ///     <para>
    ///         Distinct from a refusal because the two mean opposite things to a release step: a refusal is an
    ///         answer about the document, and this is the absence of an answer. Both are non-zero, so neither
    ///         can be mistaken for the verified outcome.
    ///     </para>
    /// </summary>
    internal const int FaultExitCode = 4;

    /// <summary>
    ///     Runs the mode when the flag is present.
    ///     <para>
    ///         The exit code is returned rather than assigned to <see cref="Environment.ExitCode" /> so the
    ///         caller keeps one place that ends the process, and the streams are parameters so a test can read
    ///         what the mode wrote. The outcome of verification goes to the output stream, including a refusal,
    ///         because a refusal is what the check was asked to determine. Usage and read failures go to the
    ///         error stream, because nothing was verified.
    ///     </para>
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="output">Where the outcome line is written.</param>
    /// <param name="error">Where a usage or read failure is written.</param>
    /// <param name="input">Standard input, read when the path is <see cref="StandardInputPath" />.</param>
    /// <returns>The exit code, or <see langword="null" /> when the flag is absent and the host should start.</returns>
    internal static async Task<int?> TryRunAsync(string[] args, TextWriter output, TextWriter error, TextReader input)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(input);

        var flagIndex = Array.FindIndex(args, argument => string.Equals(argument, Flag, StringComparison.OrdinalIgnoreCase));

        if (flagIndex < 0)
        {
            return null;
        }

        if (flagIndex + 1 >= args.Length)
        {
            await error.WriteLineAsync($"Usage: {Flag} <path to the license document>, or {Flag} {StandardInputPath} to read it from standard input.");

            return UsageExitCode;
        }

        var path = args[flagIndex + 1];
        string compactLicense;

        try
        {
            compactLicense = path == StandardInputPath
                ? await input.ReadToEndAsync()
                : await File.ReadAllTextAsync(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            await error.WriteLineAsync($"The license document could not be read: {exception.Message}");

            return UsageExitCode;
        }

        compactLicense = compactLicense.Trim();

        if (compactLicense.Length == 0)
        {
            await error.WriteLineAsync("The license document is empty, so there is nothing to verify.");

            return UsageExitCode;
        }

        try
        {
            return await VerifyAsync(compactLicense, output);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A document can reach the verifier in a shape that makes a cryptographic primitive raise rather
            // than refuse. Without this the exception would leave the entry point, which reports a fatal error
            // and ends the process without an exit code, and the zero that results is the code that means the
            // license verified. The release check reads that code as its proof that the image accepts a
            // genuine license, so an unhandled failure here would read as a pass.
            await error.WriteLineAsync($"The license could not be verified: {exception.Message}");

            return FaultExitCode;
        }
    }

    private static async Task<int> VerifyAsync(string compactLicense, TextWriter output)
    {
        using var trustAnchor = LicenseTrustAnchor.FromThisBuild();

        // The term is judged against the host clock. An installation judges it against the licensing clock,
        // which never reads earlier than the highest instant the installation has recorded, so a host clock set
        // back cannot extend a term; that recorded instant lives in the database, and this mode opens none. The
        // host clock is enough for what this check answers, because the check grants nothing and what it
        // establishes is that this build accepts a license signed under its anchor, which no clock affects.
        var now = DateTimeOffset.UtcNow;
        var verification = new LicenseVerifier(trustAnchor).Verify(compactLicense, now);

        if (!verification.IsVerified)
        {
            await output.WriteLineAsync(RefusalLine(verification.FailureReason.Value, verification.FailureDetail));

            return RefusedExitCode;
        }

        var claims = verification.License.Claims;
        var stage = LicenseState.StageFor(claims, now);

        if (StageRefusal(stage) is { } stageRefusal)
        {
            await output.WriteLineAsync(RefusalLine(stageRefusal.Reason, stageRefusal.Detail));

            return RefusedExitCode;
        }

        await output.WriteLineAsync($"License verified: licensee \"{claims.Licensee}\", license id {claims.LicenseId}, term stage {CamelCaseName(stage)}.");

        return VerifiedExitCode;
    }

    /// <summary>
    ///     Refuses the two stages in which the document grants nothing, and lets the three in which it is in
    ///     force through. This is the rule activation applies, so a document this check accepts is one an
    ///     installation would accept as well.
    /// </summary>
    private static (LicenseFailureReason Reason, string Detail)? StageRefusal(LicenseStage stage)
    {
        return stage switch
        {
            LicenseStage.NotYetValid => (LicenseFailureReason.NotYetValid, "The license term has not started yet."),
            LicenseStage.Reverted => (
                LicenseFailureReason.Expired,
                "The license term and the grace window after it have both ended."),
            _ => null,
        };
    }

    private static string RefusalLine(LicenseFailureReason reason, string detail)
    {
        return $"License refused: {CamelCaseName(reason)}. {detail}";
    }

    /// <summary>
    ///     Names an enumeration value the way the API and the licensing page name it, so the line can be matched
    ///     against what the product reports elsewhere.
    /// </summary>
    private static string CamelCaseName<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        return JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    }
}
