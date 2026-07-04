// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>
///     Validates the conservative semantic-merge rules against a fake <see cref="IFindingMergeJudge" /> (no model
///     calls). The corpus is drawn from the real near-duplicate surface of the stored file-by-file k-run
///     artifacts (fixtures 901/209): each pair is hand-labeled merge / keep-both, and the fake judge decides
///     same-defect-class from a defect-class tag encoded in the finding id. The suite includes the failure mode
///     the semantic merge exists to prevent — distinct bugs with overlapping vocabulary in the same file at the
///     same anchor must be kept separate.
/// </summary>
public sealed class SemanticFindingDeduplicatorTests
{
    private const string Parse = "packages/app-store/_utils/oauth/parseRefreshTokenResponse.ts";
    private const string Refresh = "packages/app-store/_utils/oauth/refreshOAuthTokens.ts";
    private const string Salesforce = "packages/app-store/salesforce/lib/CalendarService.ts";
    private const string Office365 = "packages/app-store/office365calendar/lib/CalendarService.ts";
    private const string Constants = "packages/lib/constants.ts";
    private const string Hubspot = "packages/app-store/hubspot/api/add.ts";
    private const string O365Video = "packages/app-store/office365video/api/add.ts";
    private const string Stripe = "packages/app-store/stripepayment/api/callback.ts";
    private const string ZohoBigin = "packages/app-store/zoho-bigin/api/callback.ts";
    private const string Webex = "packages/app-store/webex/api/callback.ts";
    private const string Tandem = "packages/app-store/tandemvideo/api/callback.ts";
    private const string SalesforceAdd = "packages/app-store/salesforce/api/add.ts";
    private const string ZohoCrmAdd = "packages/app-store/zohocrm/api/_getAdd.ts";
    private const string ZohoCrmCallback = "packages/app-store/zohocrm/api/callback.ts";

    private static readonly IReadOnlyDictionary<string, CorpusCase> Corpus = BuildCorpus();

    private static int _sequence;

    public static IEnumerable<object[]> CorpusCaseNames => Corpus.Keys.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(CorpusCaseNames))]
    public async Task Corpus_MergesOnlyWhenSameFileOverlappingAnchorAndSameDefectClass(string caseName)
    {
        var testCase = Corpus[caseName];
        var judge = new DefectClassMergeJudge();
        var sut = new SemanticFindingDeduplicator(judge);

        var result = await sut.DeduplicateFindingsAsync([testCase.First, testCase.Second], Guid.NewGuid(), CancellationToken.None);

        if (testCase.ExpectMerge)
        {
            Assert.Single(result);
        }
        else
        {
            Assert.Equal(2, result.Count);
        }

        // Conservatism: the judge is consulted only after the deterministic pre-filters (same file + overlapping
        // anchor) pass. Pairs rejected on file or anchor must never reach the judge.
        Assert.Equal(testCase.ExpectJudgeConsulted ? 1 : 0, judge.CallCount);
    }

    [Fact]
    public async Task FailureMode_DistinctBugsWithOverlappingVocabularySameAnchor_AreKeptSeparate()
    {
        // Two genuinely distinct defects reported at the same line of the same file, phrased with heavily
        // overlapping vocabulary ("token", "fetch", "endpoint", "response") — exactly the case token-Jaccard
        // would wrongly collapse. The judge (correctly) reports different defect classes, so both survive.
        var contractBreak = Finding(
            "return-contract",
            Refresh,
            8,
            CommentSeverity.Error,
            "Contract break: the credential-sharing branch returns the raw fetch Response object while the caller still reads response.data from the token refresh.");
        var ssrf = Finding(
            "ssrf-endpoint",
            Refresh,
            8,
            CommentSeverity.Error,
            "SSRF risk: the server-side fetch posts the token refresh request to the unvalidated CALCOM_CREDENTIAL_SYNC_ENDPOINT response endpoint.");

        var judge = new DefectClassMergeJudge();
        var sut = new SemanticFindingDeduplicator(judge);

        var result = await sut.DeduplicateFindingsAsync([contractBreak, ssrf], Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, judge.CallCount); // same file + same anchor ⇒ the judge is the deciding guard.
    }

    [Fact]
    public async Task DeterministicPreFilters_DifferentFileOrAnchor_NeverConsultTheJudge()
    {
        // Same defect class + same line but different files (module-scope client_id) — cross-file, so it must be
        // kept per file; and same file + same class but far-apart anchors — kept by the anchor pre-filter.
        var hubspot = Finding(
            "module-scope-clientid", Hubspot, 9, CommentSeverity.Warning,
            "Module-level client_id is reassigned per request and can leak across concurrent requests.");
        var o365 = Finding(
            "module-scope-clientid", O365Video, 9, CommentSeverity.Warning,
            "The module-level client_id cache persists across requests and can reuse a stale value.");
        var staleNear = Finding(
            "stale-token", Salesforce, 95, CommentSeverity.Error, "Stale access token used after refresh: jsforce is built with the old credential.");
        var staleFar = Finding(
            "stale-token", Salesforce, 174, CommentSeverity.Error,
            "Stale access token used after refresh: the refreshed payload is persisted but not used for the connection.");

        var judge = new DefectClassMergeJudge();
        var sut = new SemanticFindingDeduplicator(judge);

        var result = await sut.DeduplicateFindingsAsync([hubspot, o365, staleNear, staleFar], Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(4, result.Count);
        Assert.Equal(0, judge.CallCount);
    }

    [Fact]
    public async Task Merge_KeepsHigherSeverityRepresentative_AndUnionsSourcePasses()
    {
        var baselineWarning = Finding(
            "non-boolean-flag", Constants, 103, CommentSeverity.Warning,
            "APP_CREDENTIAL_SHARING_ENABLED is assigned a string, so any non-empty value enables the feature.");
        var resampleError = Finding(
            "non-boolean-flag", Constants, 104, CommentSeverity.Error,
            "APP_CREDENTIAL_SHARING_ENABLED is not boolean; the && expression returns the second env string instead of true.", ReviewPassKind.MultiPassUnion);

        var judge = new DefectClassMergeJudge();
        var sut = new SemanticFindingDeduplicator(judge);

        var result = await sut.DeduplicateFindingsAsync([baselineWarning, resampleError], Guid.NewGuid(), CancellationToken.None);

        var merged = Assert.Single(result);
        Assert.Equal(CommentSeverity.Error, merged.Severity); // higher-severity representative wins
        Assert.NotNull(merged.MergedFinding);
        Assert.Equal("semantic_same_defect_class", merged.MergedFinding!.MergeDecision);
        Assert.Contains(ReviewPassKind.Baseline, merged.MergedFinding.SourcePasses);
        Assert.Contains(ReviewPassKind.MultiPassUnion, merged.MergedFinding.SourcePasses);
    }

    [Fact]
    public async Task Merge_CollapsesSameDefectCluster_ButKeepsDistinctDefectAtSameAnchor()
    {
        // Three near-duplicate reports of the impossible HTTP condition collapse to one; a distinct defect at an
        // overlapping anchor (reject([]) losing the failure) is kept.
        var httpA = Finding(
            "impossible-http", Office365, 494, CommentSeverity.Error, "The condition !response.ok && status < 200 && status >= 300 can never be true.");
        var httpB = Finding(
            "impossible-http", Office365, 495, CommentSeverity.Error, "HTTP error detection is unreachable: status cannot be both < 200 and >= 300.");
        var httpC = Finding(
            "impossible-http", Office365, 498, CommentSeverity.Error, "The non-OK response check combines mutually exclusive ranges and never throws.");
        var distinct = Finding(
            "reject-empty", Office365, 496, CommentSeverity.Error,
            "return Promise.reject([]) replaces the caught exception with an empty array, losing the failure.");

        var judge = new DefectClassMergeJudge();
        var sut = new SemanticFindingDeduplicator(judge);

        var result = await sut.DeduplicateFindingsAsync([httpA, httpB, httpC, distinct], Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, f => f.FindingId.StartsWith("impossible-http", StringComparison.Ordinal));
        Assert.Contains(result, f => f.FindingId.StartsWith("reject-empty", StringComparison.Ordinal));
    }

    private static CandidateReviewFinding Finding(
        string defectClass,
        string filePath,
        int? lineNumber,
        CommentSeverity severity,
        string message,
        ReviewPassKind passKind = ReviewPassKind.Baseline)
    {
        // The defect class is encoded in the finding id so the fake judge can compare classes without a side map.
        var findingId = $"{defectClass}#{Interlocked.Increment(ref _sequence)}";
        return new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                filePath,
                reviewPassKind: passKind),
            severity,
            message,
            CandidateReviewFinding.PerFileCommentCategory,
            filePath,
            lineNumber);
    }

    private static CorpusCase Merge(CandidateReviewFinding a, CandidateReviewFinding b)
    {
        return new CorpusCase(a, b, true, true);
    }

    private static CorpusCase KeepBothByJudge(CandidateReviewFinding a, CandidateReviewFinding b)
    {
        return new CorpusCase(a, b, false, true);
    }

    private static CorpusCase KeepBothByPreFilter(CandidateReviewFinding a, CandidateReviewFinding b)
    {
        return new CorpusCase(a, b, false, false);
    }

    private static Dictionary<string, CorpusCase> BuildCorpus()
    {
        return new Dictionary<string, CorpusCase>(StringComparer.Ordinal)
        {
            // --- MERGE: same file, overlapping anchor, same defect class ---
            ["zod-schema 7 vs 8"] = Merge(
                Finding(
                    "zod-schema", Parse, 7, CommentSeverity.Error,
                    "The fallback schema is invalid: computed keys z.string().toString() become literal object keys and reject real responses."),
                Finding(
                    "zod-schema", Parse, 8, CommentSeverity.Error,
                    "Invalid Zod schema key: z.string().toString() evaluates to a literal string key, not a dynamic matcher.")),
            ["zod-schema 5 vs 7"] = Merge(
                Finding("zod-schema", Parse, 5, CommentSeverity.Error, "Invalid Zod schema construction breaks token parsing in credential-sharing mode."),
                Finding(
                    "zod-schema", Parse, 7, CommentSeverity.Error,
                    "Broken validation in credential-sharing mode: the computed keys are literal property names.")),
            ["placeholder-token 25 vs 25"] = Merge(
                Finding("placeholder-token", Parse, 25, CommentSeverity.Error, "A hard-coded placeholder refresh token is written when none is returned."),
                Finding(
                    "placeholder-token", Parse, 25, CommentSeverity.Error,
                    "Invalid credential injected: refresh_token is set to the literal string \"refresh_token\".")),
            ["stale-token 95 vs 99"] = Merge(
                Finding(
                    "stale-token", Salesforce, 95, CommentSeverity.Error,
                    "Stale access token used after refresh; the connection is built with the old credential key."),
                Finding(
                    "stale-token", Salesforce, 99, CommentSeverity.Error,
                    "Stale token used after a successful refresh: the new connection still uses credentialKey.access_token.")),
            ["whoid 174 vs 175"] = Merge(
                Finding(
                    "whoid-shape", Salesforce, 174, CommentSeverity.Error,
                    "The INVALID_EVENTWHOIDS fallback sets WhoId to contacts[0], the whole contact object, not the Id."),
                Finding(
                    "whoid-shape", Salesforce, 175, CommentSeverity.Error,
                    "Fallback event creation sends the wrong WhoId shape: contacts[0] is an object with Id and Email.")),
            ["whoid 188 vs 190"] = Merge(
                Finding(
                    "whoid-shape", Salesforce, 188, CommentSeverity.Error, "The fallback event payload passes the wrong value type for WhoId (contacts[0])."),
                Finding(
                    "whoid-shape", Salesforce, 190, CommentSeverity.Error,
                    "Wrong value type for WhoId in the fallback path: contacts[0] is a contact object.")),
            ["deleteevent 298 vs 299"] = Merge(
                Finding(
                    "deleteevent-return", Salesforce, 298, CommentSeverity.Error,
                    "deleteEvent never returns or throws the promise result, so it always resolves undefined."),
                Finding(
                    "deleteevent-return", Salesforce, 299, CommentSeverity.Error,
                    "deleteEvent creates promises without returning them; the async function always resolves undefined.")),
            ["non-boolean-flag 103 vs 104"] = Merge(
                Finding(
                    "non-boolean-flag", Constants, 103, CommentSeverity.Error,
                    "APP_CREDENTIAL_SHARING_ENABLED is not a boolean; the && expression returns the second env string."),
                Finding(
                    "non-boolean-flag", Constants, 104, CommentSeverity.Error,
                    "APP_CREDENTIAL_SHARING_ENABLED uses raw env values, so empty strings still evaluate truthy.")),
            ["impossible-http 494 vs 498"] = Merge(
                Finding("impossible-http", Office365, 494, CommentSeverity.Error, "Broken HTTP error check: status < 200 && status >= 300 can never be true."),
                Finding(
                    "impossible-http", Office365, 498, CommentSeverity.Error,
                    "The non-OK response check combines mutually exclusive ranges and never throws.")),
            ["return-contract 7 vs 8"] = Merge(
                Finding(
                    "return-contract", Refresh, 7, CommentSeverity.Error,
                    "Contract break with the Google Calendar caller: the sharing branch returns the raw fetch result."),
                Finding(
                    "return-contract", Refresh, 8, CommentSeverity.Error,
                    "Contract break: the sharing branch now returns the raw fetch Response the caller cannot read.")),

            // --- KEEP-BOTH failure mode: same file, overlapping anchor, DIFFERENT defect class, shared vocabulary ---
            ["refresh 8 return-contract vs ssrf"] = KeepBothByJudge(
                Finding(
                    "return-contract", Refresh, 8, CommentSeverity.Error,
                    "Contract break: the sharing branch returns the raw fetch Response object to the token refresh caller."),
                Finding(
                    "ssrf-endpoint", Refresh, 8, CommentSeverity.Error,
                    "SSRF: the fetch posts the token refresh to the unvalidated CALCOM_CREDENTIAL_SYNC_ENDPOINT.")),
            ["refresh 8 ssrf vs idor"] = KeepBothByJudge(
                Finding("ssrf-endpoint", Refresh, 8, CommentSeverity.Error, "SSRF risk: unvalidated sync endpoint URL used for the server-side token fetch."),
                Finding(
                    "idor-unauth", Refresh, 8, CommentSeverity.Error,
                    "Unauthenticated token-sync request enables IDOR-style token access with only user identifiers.")),
            ["refresh 5 userid vs 7 missing-validation"] = KeepBothByJudge(
                Finding("userid-truthy", Refresh, 5, CommentSeverity.Error, "Falsy check && userId rejects a valid user id of 0 and takes the wrong branch."),
                Finding(
                    "missing-validation", Refresh, 7, CommentSeverity.Warning,
                    "Missing response validation: the raw fetch response is returned without checking response.ok.")),
            ["office365 494 impossible vs 496 reject"] = KeepBothByJudge(
                Finding("impossible-http", Office365, 494, CommentSeverity.Error, "Impossible HTTP condition: status < 200 && status >= 300 never throws."),
                Finding(
                    "reject-empty", Office365, 496, CommentSeverity.Error,
                    "return Promise.reject([]) replaces the caught error response with an empty array.")),

            // --- KEEP-BOTH by anchor pre-filter: same file, same defect class, anchors too far apart ---
            ["stale-token 95 vs 105 far"] = KeepBothByPreFilter(
                Finding("stale-token", Salesforce, 95, CommentSeverity.Error, "Stale access token used after refresh (getClient path)."),
                Finding("stale-token", Salesforce, 105, CommentSeverity.Error, "Refreshed OAuth tokens are never used for the new Salesforce connection.")),
            ["non-boolean-flag 89 vs 103 far"] = KeepBothByPreFilter(
                Finding("non-boolean-flag", Constants, 89, CommentSeverity.Warning, "APP_CREDENTIAL_SHARING_ENABLED no longer resolves to a boolean."),
                Finding(
                    "non-boolean-flag", Constants, 103, CommentSeverity.Error,
                    "APP_CREDENTIAL_SHARING_ENABLED is not boolean; returns the second env string.")),
            ["impossible-http 494 vs 501 far"] = KeepBothByPreFilter(
                Finding("impossible-http", Office365, 494, CommentSeverity.Error, "Impossible HTTP condition at 494."),
                Finding(
                    "impossible-http", Office365, 501, CommentSeverity.Error,
                    "Incorrect error condition makes non-2xx responses fall through as success at 501.")),
            ["parse 25 placeholder vs 8 zod far"] = KeepBothByPreFilter(
                Finding("placeholder-token", Parse, 25, CommentSeverity.Error, "Hard-coded placeholder refresh token written when none returned."),
                Finding("zod-schema", Parse, 8, CommentSeverity.Error, "Invalid Zod schema computed key rejects valid token responses.")),

            // --- KEEP-BOTH by file pre-filter: different files ---
            ["module-clientid hubspot vs o365video"] = KeepBothByPreFilter(
                Finding(
                    "module-scope-clientid", Hubspot, 9, CommentSeverity.Warning,
                    "Module-level client_id reassigned per request; concurrent requests can clobber it."),
                Finding(
                    "module-scope-clientid", O365Video, 9, CommentSeverity.Warning,
                    "Module-level client_id cache persists across requests and reuses a stale value.")),
            ["open-redirect stripe vs zoho"] = KeepBothByPreFilter(
                Finding(
                    "open-redirect", Stripe, 15, CommentSeverity.Error,
                    "returnTo taken directly from req.query.state enables an open redirect to an arbitrary URL."),
                Finding(
                    "ssrf-endpoint", ZohoBigin, 23, CommentSeverity.Error,
                    "accountsServer from req.query is interpolated into the token endpoint URL with no allow-list.")),
            ["cross-file stale vs flag"] = KeepBothByPreFilter(
                Finding("stale-token", Salesforce, 95, CommentSeverity.Error, "Stale token used after refresh."),
                Finding("non-boolean-flag", Constants, 103, CommentSeverity.Error, "APP_CREDENTIAL_SHARING_ENABLED is not boolean.")),

            // --- More MERGE pairs (same file, overlapping anchor, same defect class) ---
            ["open-redirect stripe 14 vs 15"] = Merge(
                Finding(
                    "open-redirect", Stripe, 14, CommentSeverity.Error,
                    "The redirect target is taken directly from req.query.state via returnTo with no allow-list or same-origin check."),
                Finding(
                    "open-redirect", Stripe, 15, CommentSeverity.Error,
                    "returnTo is returned straight from req.query.state and the final redirect uses it unchanged, allowing an external redirect.")),
            ["ssrf zoho 23 vs 25"] = Merge(
                Finding(
                    "ssrf-endpoint", ZohoBigin, 23, CommentSeverity.Error,
                    "accountsServer from req.query is interpolated directly into the token endpoint URL, so a chosen accounts-server posts the code to an attacker host."),
                Finding(
                    "ssrf-endpoint", ZohoBigin, 25, CommentSeverity.Error,
                    "accountsServer is taken straight from req.query into accountsUrl with no allow-list, letting a request POST the OAuth code to an arbitrary host.")),
            ["url-encode webex 16 vs 18"] = Merge(
                Finding(
                    "url-encode", Webex, 16, CommentSeverity.Error,
                    "The token exchange puts client_id, client_secret, code, and redirect_uri directly into the URL; reserved characters change request semantics."),
                Finding(
                    "url-encode", Webex, 18, CommentSeverity.Warning,
                    "The code value is interpolated into the Webex token URL without encodeURIComponent, so reserved characters produce a malformed request.")),
            ["non-boolean-flag 103 warn vs error same anchor"] = Merge(
                Finding(
                    "non-boolean-flag", Constants, 103, CommentSeverity.Warning,
                    "APP_CREDENTIAL_SHARING_ENABLED becomes truthy whenever both environment variables are non-empty strings."),
                Finding(
                    "non-boolean-flag", Constants, 103, CommentSeverity.Error,
                    "APP_CREDENTIAL_SHARING_ENABLED is not boolean-normalized; the && expression returns the second env var's string value.")),
            ["zod-schema 8 vs 8 same anchor"] = Merge(
                Finding(
                    "zod-schema", Parse, 8, CommentSeverity.Error,
                    "Invalid schema construction causes false negatives on valid token responses; the key expression is a literal string key."),
                Finding(
                    "zod-schema", Parse, 8, CommentSeverity.Error,
                    "Schema validation bug: the computed key [z.string().toString()] evaluates to a literal key, not a dynamic key constraint.")),
            ["stale-token 105 vs 107"] = Merge(
                Finding(
                    "stale-token", Salesforce, 105, CommentSeverity.Error,
                    "Stale credentials are used after a successful token refresh; the jsforce connection is still built with the old key."),
                Finding(
                    "stale-token", Salesforce, 107, CommentSeverity.Error,
                    "Refreshed OAuth credentials are written to storage but never used for the live Salesforce connection.")),
            ["whoid 180 vs 183"] = Merge(
                Finding(
                    "whoid-shape", Salesforce, 180, CommentSeverity.Error,
                    "WhoId is assigned the wrong value type in the fallback path: contacts[0] is an object with Id and Email."),
                Finding(
                    "whoid-shape", Salesforce, 183, CommentSeverity.Error,
                    "Wrong value passed for WhoId in the fallback event creation path; contacts[0] is an object, not an Id.")),

            // --- More KEEP-BOTH by anchor pre-filter (same file, same defect class, far apart) ---
            ["reject-empty 171 vs 181 far"] = KeepBothByPreFilter(
                Finding("reject-empty", Office365, 171, CommentSeverity.Error, "return Promise.reject([]) replaces the caught exception with an empty array."),
                Finding(
                    "reject-empty", Office365, 181, CommentSeverity.Error,
                    "The catch block turns all failures into a successful empty-result shape via reject([]).")),
            ["whoid 174 vs 188 far"] = KeepBothByPreFilter(
                Finding("whoid-shape", Salesforce, 174, CommentSeverity.Error, "The fallback WhoId is set to contacts[0], the whole contact object."),
                Finding(
                    "whoid-shape", Salesforce, 188, CommentSeverity.Error, "The fallback event payload passes the wrong value type for WhoId (contacts[0]).")),
            ["stale-token 77 vs 95 far"] = KeepBothByPreFilter(
                Finding("stale-token", Salesforce, 77, CommentSeverity.Error, "Wrong token values are used after a successful refresh (getClient path at 77)."),
                Finding("stale-token", Salesforce, 95, CommentSeverity.Error, "Stale access token used after refresh at 95.")),
            ["zohocrm callback 14 guard vs 43 expiry far"] = KeepBothByPreFilter(
                Finding(
                    "guard-and-skips-400", ZohoCrmCallback, 14, CommentSeverity.Error,
                    "The && guard skips the 400 response when code is undefined, allowing the token exchange to continue."),
                Finding(
                    "expiry-ms-bug", ZohoCrmCallback, 43, CommentSeverity.Error,
                    "expiryDate uses Math.round(Date.now() + 60*60), adding 3,600 ms instead of one hour.")),

            // --- More KEEP-BOTH by file pre-filter (different files) ---
            ["module-clientid salesforce-add vs zohocrm-add"] = KeepBothByPreFilter(
                Finding(
                    "module-scope-clientid", SalesforceAdd, 9, CommentSeverity.Warning,
                    "Module-level consumer_key persists across requests and can reuse a stale value."),
                Finding(
                    "module-scope-clientid", ZohoCrmAdd, 9, CommentSeverity.Warning,
                    "client_id declared once at module scope can reuse a cached value for a different tenant.")),
            ["open-redirect stripe vs webex diff file"] = KeepBothByPreFilter(
                Finding("open-redirect", Stripe, 15, CommentSeverity.Error, "returnTo from req.query.state enables an open redirect."),
                Finding("url-encode", Webex, 16, CommentSeverity.Error, "Raw query concatenation in the Webex token URL.")),

            // --- More KEEP-BOTH by judge (same file, overlapping anchor, distinct defect, shared vocabulary) ---
            ["tandem 45 delete-before-check vs 47 no-response"] = KeepBothByJudge(
                Finding(
                    "delete-before-check", Tandem, 45, CommentSeverity.Error,
                    "Deleting the existing Tandem credential happens before checking result.ok, so a failed token request still removes the credential."),
                Finding(
                    "no-response-on-failure", Tandem, 47, CommentSeverity.Error,
                    "When result.ok is false the handler falls through without calling res.status/res.redirect, so the request hangs until timeout.")),
        };
    }

    public sealed record CorpusCase(
        CandidateReviewFinding First,
        CandidateReviewFinding Second,
        bool ExpectMerge,
        bool ExpectJudgeConsulted);

    // Fake judge: same defect class iff the class tag encoded in each finding id matches. Counts calls so tests
    // can assert the deterministic pre-filters short-circuit before the judge is consulted.
    private sealed class DefectClassMergeJudge : IFindingMergeJudge
    {
        public int CallCount { get; private set; }

        public Task<bool> AreSameDefectClassAsync(
            CandidateReviewFinding first,
            CandidateReviewFinding second,
            Guid clientId,
            CancellationToken ct = default)
        {
            this.CallCount++;
            return Task.FromResult(string.Equals(DefectClassOf(first), DefectClassOf(second), StringComparison.Ordinal));
        }

        private static string DefectClassOf(CandidateReviewFinding finding)
        {
            var hash = finding.FindingId.IndexOf('#', StringComparison.Ordinal);
            return hash < 0 ? finding.FindingId : finding.FindingId[..hash];
        }
    }
}
