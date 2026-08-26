# Meister DEV's ProPR Licensing Options

Meister DEV's ProPR uses one ELv2-licensed source tree.

Some files in that source tree implement commercial-only functionality and may be shipped in community or
self-hosted artifacts. That does not grant the right to activate or use those commercial-only features.

Whether you deploy ProPR yourself or ask someone else to host it for you, activating or using commercial-only
features requires a commercial license.

## 1. The Community Edition (Elastic License 2.0 / ELv2)

For individuals, home labs, internal teams and community contributors.

- **Cost:** Free.
- **Self-Hosting:** Allowed under ELv2, subject to its restrictions.
- **The Condition:** You may use, copy, modify, and redistribute the software, but you may not provide it to third
  parties as a hosted or managed service where users access a substantial set of ProPR's features or functionality.
- **Notices:** If you redistribute copies, they must include the license terms. If you modify the software, you must
  keep prominent notices that the software was modified.
- **Support:** Community-based (GitHub Issues).

## 2. The Commercial Edition

For businesses that need to activate or use commercial-only features, run ProPR as a hosted or managed service,
agree alternative commercial terms, or buy professional support.

A commercial license corresponds to the `Commercial` product edition inside ProPR. The right to activate or use
commercial-only features comes from that license, not from self-hosting and not from the presence of the code in
source or binaries.

| Feature                   | Community (ELv2)                    | Commercial                              |
|---------------------------|-------------------------------------|-----------------------------------------|
| License Cost              | $0                                  | Contact Us                              |
| Self-hosting rights       | Allowed under ELv2                  | Allowed under ELv2 + commercial terms   |
| Commercial feature use    | Not granted                         | Granted under commercial terms          |
| SaaS / Managed Hosting    | Not permitted under ELv2            | Available under commercial terms        |
| Professional Support      | Best effort                         | Priority support / negotiated terms     |
| Consulting & Setup        | Self-service                        | Available as add-on                     |

### Product capability availability

Nine capabilities require a commercial license: single sign-on, parallel review execution, distributed review
execution, multiple SCM providers, crawl configurations, mention answering, budgeting, Code Insights, and
multi-tenancy. A license unlocks the capabilities it names. Reviewing itself, all four SCM provider families,
every AI provider, per-client logical models, thread memory, ProCursor and the review diagnostics are available
in the Community edition.
[Editions and licensed features](docs/reference/editions.md) lists each capability, what an installation does
without it, and how a license is activated.

The admin UI shows the current product edition in the header, on the login screen, and under
**Administration -> Licensing**. When a Community installation calls a commercial-only feature, the API returns
a `premium_feature_unavailable` response and the UI explains why the action is blocked.

### Using one license across your installations

A commercial license authorizes an entitlement: the licensee, the term, the capabilities and the limits. It is
not tied to a single machine, and may be activated on the installations you operate, including production,
disaster-recovery standby, blue/green pairs, staging and development.
[The commercial license policy](COMMERCIAL-LICENSE-POLICY.md) states where it may be activated, how its limits
are counted across an estate, and what falls outside it.

### Why choose a Commercial License?

A commercial license grants rights that ELv2 does not. Consider one if your company:

- Plans to activate or use commercial-only features, self-hosted deployments included.
- Plans to offer Meister DEV's ProPR to third parties as a hosted or managed service.
- Needs alternative commercial terms, procurement language, or a negotiated contract.
- Wants a clean commercial grant for reseller, OEM, or managed-service scenarios.
- Requires professional support, SLAs, or a direct line to the core developers for bug fixes and architectural
  advice.
- Wants consulting for scaling, security hardening, or custom feature development.
- Wants access to commercial-only features that future releases may add.

## Get a Commercial License

Commercial licensing terms, pricing and arrangements are handled case by case. Contact Meister DEV to discuss
your use case.
