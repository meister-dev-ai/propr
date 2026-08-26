// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Api.Tests.Features.Licensing;

/// <summary>
///     What the <c>--verify-license</c> entrypoint mode reports and exits with, against the trust anchor this
///     build actually carries.
///     <para>
///         Only the refusal paths are covered here. A document this build accepts can only be signed with the
///         publisher's chain, which no test has, so the accepting path is proved by the release workflow running
///         the mode inside the built image against an issued license.
///     </para>
/// </summary>
public sealed class LicenseVerificationCommandTests
{
    [Fact]
    public async Task WithoutTheFlag_NothingIsVerifiedAndTheHostStarts()
    {
        var run = await RunAsync(["--generate-openapi"]);

        Assert.Null(run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Equal(string.Empty, run.Error);
    }

    [Fact]
    public async Task WithoutAPath_TheUsageIsNamedOnTheErrorStream()
    {
        var run = await RunAsync([LicenseVerificationCommand.Flag]);

        Assert.Equal(LicenseVerificationCommand.UsageExitCode, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains(LicenseVerificationCommand.Flag, run.Error, StringComparison.Ordinal);
        Assert.Contains("standard input", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingFile_IsReportedAsAReadFailure()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"propr-license-{Guid.NewGuid():N}.lic");

        var run = await RunAsync([LicenseVerificationCommand.Flag, missingPath]);

        Assert.Equal(LicenseVerificationCommand.UsageExitCode, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains("could not be read", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyFile_IsReportedAsHavingNothingToVerify()
    {
        var run = await RunWithDocumentFileAsync(string.Empty);

        Assert.Equal(LicenseVerificationCommand.UsageExitCode, run.ExitCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Contains("empty", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedDocument_IsRefusedAsMalformed()
    {
        var run = await RunWithDocumentFileAsync("not-a-license");

        Assert.Equal(LicenseVerificationCommand.RefusedExitCode, run.ExitCode);
        Assert.Equal(string.Empty, run.Error);
        Assert.StartsWith("License refused: malformed.", run.Output.Trim(), StringComparison.Ordinal);
        Assert.Single(NonEmptyLines(run.Output));
    }

    [Fact]
    public async Task ADocumentSignedByAChainThisBuildDoesNotAccept_IsRefusedAsAnUntrustedSigner()
    {
        var run = await RunWithDocumentFileAsync(SignWithAForeignChain());

        Assert.Equal(LicenseVerificationCommand.RefusedExitCode, run.ExitCode);
        Assert.Equal(string.Empty, run.Error);
        Assert.StartsWith("License refused: untrustedSigner.", run.Output.Trim(), StringComparison.Ordinal);
        Assert.Single(NonEmptyLines(run.Output));
    }

    // The path "-" reads the document from standard input, which is how the release step feeds it a secret
    // without writing the license to a file on the build agent.
    [Fact]
    public async Task ADocumentReadFromStandardInput_IsJudgedTheSameWayAsAFile()
    {
        var run = await RunAsync(
            [LicenseVerificationCommand.Flag, LicenseVerificationCommand.StandardInputPath],
            SignWithAForeignChain());

        Assert.Equal(LicenseVerificationCommand.RefusedExitCode, run.ExitCode);
        Assert.Equal(string.Empty, run.Error);
        Assert.StartsWith("License refused: untrustedSigner.", run.Output.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A license whose term is in force, signed by a chain generated for this test. It is a well-formed
    ///     document, so it reaches the trust decision rather than being turned away as malformed.
    /// </summary>
    private static string SignWithAForeignChain()
    {
        using var chain = LicenseTestChain.Create();
        var now = DateTimeOffset.UtcNow;

        return chain.Sign(LicenseTestChain.ClaimsFor(now.AddDays(-1), now.AddDays(365)));
    }

    private static string[] NonEmptyLines(string text)
    {
        return text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<CommandRun> RunWithDocumentFileAsync(string document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"propr-license-{Guid.NewGuid():N}.lic");
        await File.WriteAllTextAsync(path, document);

        try
        {
            return await RunAsync([LicenseVerificationCommand.Flag, path]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<CommandRun> RunAsync(string[] args, string standardInput = "")
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        using var input = new StringReader(standardInput);

        var exitCode = await LicenseVerificationCommand.TryRunAsync(args, output, error, input);

        return new CommandRun(exitCode, output.ToString(), error.ToString());
    }

    private sealed record CommandRun(int? ExitCode, string Output, string Error);
}
