// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Conformance;
using MeisterDev.Ai.Providers.ExampleAddIn;

namespace MeisterDev.Ai.Providers.Tests.Conformance;

/// <summary>
///     What the base class does with whatever a check or a family throws at it.
/// </summary>
/// <remarks>
///     Run exists so a check never throws, and it is the one path written to contain everything. What it carries
///     out of a throw goes into a report an operator reads and a log line quoting it.
/// </remarks>
public sealed class DriverConformanceCheckTests
{
    // A line break ends the log line and lets the rest be read as a further entry, and the text comes from code
    // the host did not compile.
    [Fact]
    public void AThrownMessageLosesItsControlCharacters()
    {
        var result = new Throwing(new InvalidOperationException("refused\nlevel=error msg=activated")).Run(Subject);

        Assert.True(result.IsFailure);
        Assert.DoesNotContain('\n', result.Detail!);
        Assert.Contains("refused level=error", result.Detail!, StringComparison.Ordinal);
    }

    // A family whose exception message carries a serialised request would otherwise fill the report and every
    // line quoting it.
    [Fact]
    public void AThrownMessageIsBounded()
    {
        var result = new Throwing(new InvalidOperationException(new string('x', 5_000))).Run(Subject);

        Assert.True(result.Detail!.Length < 600);
        Assert.EndsWith("…", result.Detail, StringComparison.Ordinal);
    }

    // Run is the handler that exists so a check never throws, and Name is implemented outside this assembly, so
    // reading it inside that handler is the one way a throw could escape through it.
    [Fact]
    public void ACheckWhoseNameThrowsStillReportsAFailure()
    {
        var result = new Nameless().Run(Subject);

        Assert.True(result.IsFailure);
        Assert.Equal(nameof(Nameless), result.Check);
    }

    // The example add-in's driver, because these cases are about the base class and not about a family.
    private static ConformanceSubject Subject => new(new ExampleProviderDriver());

    private sealed class Throwing(Exception failure) : DriverConformanceCheck
    {
        public override string Name => "throws";

        protected override ConformanceResult Evaluate(ConformanceSubject subject) => throw failure;
    }

    private sealed class Nameless : DriverConformanceCheck
    {
        public override string Name => throw new InvalidOperationException("this getter throws");

        protected override ConformanceResult Evaluate(ConformanceSubject subject) =>
            throw new InvalidOperationException("and so does the check");
    }
}
