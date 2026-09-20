// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     What the host tells an operator before a declared action starts, and what a run still open is waiting for.
/// </summary>
/// <remarks>
///     A vendor-fixed loopback redirect resolves on the machine running the browser, and the host cannot observe
///     where that browser is. So the requirement is stated rather than gated, and what it drives is this text:
///     read before the action starts by an operator whose deployment may not satisfy it, and recorded on the run
///     so one that expires says what it was waiting for. Both are composed from the declaration and the
///     connection's own values, so a console renders one string and carries no knowledge of any family.
/// </remarks>
public sealed class ProviderActionNoticeTests
{
    private const string ConnectAction = "connect";

    private const string PortField = "listenerPort";

    [Fact]
    public void AFamilyThatRequiresCoLocationStatesTheRequirementAndThePortItWillBind()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: true);

        var notice = ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null);

        Assert.NotNull(notice);
        Assert.Contains("1455", notice, StringComparison.Ordinal);
        Assert.Contains("machine running this host", notice, StringComparison.Ordinal);
    }

    // A family that states no requirement its deployment has to meet gets no notice, so a console shows one only
    // where there is something to say.
    [Fact]
    public void AFamilyThatDoesNotRequireCoLocationStatesNothing()
    {
        var declaration = Declaring(requiresCoLocation: false, opensListener: false);

        Assert.Null(ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null));
    }

    // The port an operator moved is the port the connection will bind, so it is the one they are told to publish.
    // Showing the family's default would send them to register the wrong address with the vendor.
    [Fact]
    public void ThePortStatedIsTheOneThisConnectionIsConfiguredWithAndNotTheFamilysDefault()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: true);
        var configured = new Dictionary<string, string>(StringComparer.Ordinal) { [PortField] = "8321" };

        var notice = ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], configured);

        Assert.Contains("8321", notice!, StringComparison.Ordinal);
        Assert.DoesNotContain("1455", notice, StringComparison.Ordinal);
    }

    // The paste path is the answer for an operator administering a host that runs somewhere else, and it is
    // offered only where the action can actually take a pasted value.
    [Fact]
    public void ThePasteAlternativeIsOfferedWhereTheActionCollectsAValueAndNotWhereItCollectsNone()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: true);
        var collecting = declaration.Actions[0];
        var collectingNothing = collecting with { Inputs = [] };

        Assert.Contains(
            "pasting the address",
            ProviderActionNotices.CoLocationNotice(declaration, collecting, null)!,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "pasting the address",
            ProviderActionNotices.CoLocationNotice(declaration, collectingNothing, null)!,
            StringComparison.Ordinal);
    }

    // Composed from what the family declared, so a family the host has never seen gets a usable notice and no
    // family is named in the host's own code.
    [Fact]
    public void TheNoticeNamesTheFamilyFromItsOwnDeclaration()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: true) with
        {
            Label = "A family nobody here wrote",
        };

        Assert.Contains(
            "A family nobody here wrote",
            ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null)!,
            StringComparison.Ordinal);
    }

    // A family that requires co-location without opening a socket of its own still states the requirement: the
    // flow finishes in the operator's browser either way. What it does not state is a port it never binds.
    [Fact]
    public void AFamilyThatOpensNoListenerStatesTheRequirementWithoutNamingAPort()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: false) with { Fields = [] };

        var waiting = ProviderActionNotices.WaitingFor(declaration, declaration.Actions[0], null);

        Assert.Contains("machine running this host", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("sends your browser back to port", waiting, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunOfAFamilyThatStatesNoRequirementSaysOnlyWhatItIsWaitingFor()
    {
        var declaration = Declaring(requiresCoLocation: false, opensListener: false);

        var waiting = ProviderActionNotices.WaitingFor(declaration, declaration.Actions[0], null);

        Assert.Contains("report that it finished", waiting, StringComparison.Ordinal);
        Assert.DoesNotContain("machine running this host", waiting, StringComparison.Ordinal);
    }

    // A computed field is derived from the others and is never a port an operator sets, so naming one would tell
    // the operator to publish a value they cannot change.
    [Fact]
    public void AComputedFieldIsNotReadAsAPortToPublish()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: true) with
        {
            Fields =
            [
                new ProviderDeclaredField("derived", "Derived number", ProviderFieldKind.Int)
                {
                    IsComputed = true,
                    DefaultValue = "9999",
                },
            ],
        };

        var notice = ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null);

        Assert.DoesNotContain("9999", notice!, StringComparison.Ordinal);
    }

    // A family that opens no socket has no port to name, and the whole-number settings it does declare are
    // something else entirely — a timeout, a page size. Announced as "port N", the operator is told their
    // browser will be sent back to a number that means nothing.
    [Fact]
    public void AFamilyThatOpensNoListenerNamesNoPort()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: false);

        var notice = ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null);

        Assert.NotNull(notice);
        Assert.DoesNotContain("port", notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1455", notice, StringComparison.Ordinal);
    }

    // The paste fallback is the answer for an operator administering a host somewhere else, and it needs an
    // input a pasted address fits. An action whose only inputs are a checkbox and a number has nowhere to put
    // one, so the operator is sent to a form that cannot take what they were told to paste.
    [Fact]
    public void AnActionWithNoInputAPastedAddressFitsDoesNotOfferThePastePath()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: true) with
        {
            Actions =
            [
                new ProviderDeclaredAction(
                    ConnectAction,
                    "Connect account",
                    [
                        new ProviderDeclaredField("useCache", "Use cache", ProviderFieldKind.Bool)
                        {
                            Scope = ProviderFieldScope.ActionInput,
                        },
                    ]),
            ],
        };

        var notice = ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null);

        Assert.NotNull(notice);
        Assert.DoesNotContain("pasting", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnActionThatTakesTheRedirectAsTextStillOffersThePastePath()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: true);

        var notice = ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null);

        Assert.NotNull(notice);
        Assert.Contains("pasting", notice, StringComparison.OrdinalIgnoreCase);
    }

    // A family that opens a listener can declare a whole-number setting that is not a port. Read off the field
    // shape, a timeout in seconds is announced to the operator as the port their browser will be sent back to.
    // The family names the port field instead, and the host reads that.
    [Fact]
    public void OnlyTheFieldTheFamilyNamesAsItsPortIsStatedAsOne()
    {
        var declaration = WithTimeoutField(Declaring(requiresCoLocation: true, opensListener: true)) with
        {
            ListenerPortFieldNames = [PortField],
        };

        var notice = ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null);

        Assert.Contains("1455", notice!, StringComparison.Ordinal);
        Assert.DoesNotContain("Request timeout", notice, StringComparison.Ordinal);
    }

    // A family that names none keeps the reading it had, so declaring nothing changes nothing.
    [Fact]
    public void AFamilyThatNamesNoPortFieldIsReadAsItWasBefore()
    {
        var declaration = WithTimeoutField(Declaring(requiresCoLocation: true, opensListener: true));

        var notice = ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null);

        Assert.Contains("1455", notice!, StringComparison.Ordinal);
        Assert.Contains("Request timeout", notice, StringComparison.Ordinal);
    }

    // A name matching no whole-number field states one port fewer, never a wrong one.
    [Fact]
    public void APortFieldNameMatchingNothingStatesNoPort()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: true) with
        {
            ListenerPortFieldNames = ["portTheFamilyRenamed"],
        };

        var notice = ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null);

        Assert.NotNull(notice);
        Assert.DoesNotContain("1455", notice, StringComparison.Ordinal);
        Assert.Contains("machine running this host", notice, StringComparison.Ordinal);
    }

    // The paste hint belongs to the input the redirect goes into. An action that also collects a free-text
    // account name would otherwise have the hint stated for both, because free text is the shape of each.
    [Fact]
    public void ThePasteAlternativeFollowsTheInputTheFamilyMarked()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: true);
        var marked = new ProviderDeclaredAction(
            ConnectAction,
            "Connect account",
            [
                new ProviderDeclaredField("accountName", "Account name", ProviderFieldKind.String)
                {
                    Scope = ProviderFieldScope.ActionInput,
                },
                new ProviderDeclaredField("callbackUrl", "Callback URL", ProviderFieldKind.String)
                {
                    Scope = ProviderFieldScope.ActionInput,
                    AcceptsRedirectAddress = true,
                },
            ]);

        Assert.Contains(
            "pasting the address",
            ProviderActionNotices.CoLocationNotice(declaration, marked, null)!,
            StringComparison.Ordinal);
    }

    // An action whose only inputs are free text the family did not mark keeps the reading it had.
    [Fact]
    public void AnActionThatMarksNoInputIsReadAsItWasBefore()
    {
        var declaration = Declaring(requiresCoLocation: true, opensListener: true);

        Assert.Contains(
            "pasting the address",
            ProviderActionNotices.CoLocationNotice(declaration, declaration.Actions[0], null)!,
            StringComparison.Ordinal);
    }

    private static ProviderDeclaration WithTimeoutField(ProviderDeclaration declaration)
    {
        return declaration with
        {
            Fields =
            [
                .. declaration.Fields,
                new ProviderDeclaredField("requestTimeoutSeconds", "Request timeout", ProviderFieldKind.Int)
                {
                    DefaultValue = "30",
                },
            ],
        };
    }

    private static ProviderDeclaration Declaring(bool requiresCoLocation, bool opensListener)
    {
        return new ProviderDeclaration
        {
            Key = "example/subscription",
            Label = "Example subscription",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            Fields =
            [
                new ProviderDeclaredField(PortField, "Callback port", ProviderFieldKind.Int)
                {
                    DefaultValue = "1455",
                },
            ],
            Actions =
            [
                new ProviderDeclaredAction(
                    ConnectAction,
                    "Connect account",
                    [
                        new ProviderDeclaredField("callbackUrl", "Callback URL", ProviderFieldKind.String)
                        {
                            Scope = ProviderFieldScope.ActionInput,
                        },
                    ]),
            ],
            AuthModes =
            [
                new ProviderDeclaredAuthMode("example/subscription:ApiKey", [AiCredentialFieldSupport.ApiKey]),
            ],
            ProtocolModes = new ProviderDeclaredProtocolModes(["example/subscription:ChatCompletions"]),
            RequiresBrowserCoLocation = requiresCoLocation,
            OpensListener = opensListener,
            ConformanceInputs = new ProviderConformanceInputs("example/subscription:ApiKey"),
        };
    }
}
