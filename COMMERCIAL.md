# Meister DEV's ProPR Licensing Options

Meister DEV's ProPR uses one ELv2-licensed source tree.

Some files in that source tree implement commercial-only functionality and may be shipped in community or self-hosted artifacts.
That does not grant the right to activate or use those commercial-only features.

Whether you deploy ProPR yourself or ask someone else to host it for you, activating or using commercial-only
features requires a commercial license.

## 1. The Community Edition (Elastic License 2.0 / ELv2)

Perfect for individuals, home labs, internal teams, and community contributors.

- **Cost:** Free.
- **Self-Hosting:** Allowed under ELv2, subject to ELv2 restrictions.
- **The Condition:** You may use, copy, modify, and redistribute the software, but you may not provide it to third
  parties as a hosted or managed service where users access a substantial set of ProPR's features or functionality.
- **Notices:** If you redistribute copies, they must include the license terms. If you modify the software, you must
  keep prominent notices that the software was modified.
- **Support:** Community-based (GitHub Issues).

## 2. The Commercial Edition

Designed for businesses that need rights to activate or use commercial-only features, managed-service rights,
alternative commercial terms, or professional support.

Commercial licensing maps to the `Commercial` product edition inside ProPR, but the legal right comes from the separate
commercial license, not from self-hosting and not from the presence of the code in source or binaries.

| Feature                   | Community (ELv2)                    | Commercial                              |
|---------------------------|-------------------------------------|-----------------------------------------|
| License Cost              | $0                                  | Contact Us                              |
| Self-hosting rights       | Allowed under ELv2                  | Allowed under ELv2 + commercial terms   |
| Commercial feature use    | Not granted                         | Granted under commercial terms          |
| SaaS / Managed Hosting    | Not permitted under ELv2            | Available under commercial terms        |
| Professional Support      | Best effort                         | Priority support / negotiated terms     |
| Consulting & Setup        | Self-service                        | Available as add-on                     |

### Product capability availability

| Runtime capability | Community | Commercial |
|--------------------|-----------|------------|
| Password sign-in | Available | Available |
| Single sign-on | Upgrade required | Available |
| One active review at a time | Enforced | Not enforced |
| One active SCM provider connection | Enforced | Not enforced |
| Premium capability toggles per installation | Not available | Available |

The admin UI surfaces the current product edition in the header, the login screen, and `Settings -> Licensing`.
When a Community deployment hits a premium-only path, the API returns a structured
`premium_feature_unavailable` response so the UI can explain why the action is blocked.

### Why choose a Commercial License?

Most businesses choose the Commercial License when they need rights that ELv2 does not grant by default. If your
company:

- Plans to activate or use commercial-only features, even in a self-hosted deployment.
- Plans to offer Meister DEV's ProPR to third parties as a hosted or managed service.
- Needs alternative commercial terms, procurement language, or a negotiated contract.
- Requires professional support, SLAs, or direct engineering engagement.
- Wants a clean commercial grant for reseller, OEM, or managed-service scenarios.
- Needs a direct line to the core developers for bug fixes and architectural advice.
- Premium support and consulting for scaling, security hardening, or custom feature development.
- Premium features that may be added in the future (TBD).

> [!NOTE]
> **Building a Business or Running Premium Features?**
> If you intend to use ProPR's commercial-only features or build a managed service or SaaS offering on top of ProPR,
> a commercial license is required. Self-hosting alone does not grant those rights.

## Get a Commercial License

Commercial licensing terms, pricing, and arrangements are currently handled on a case-by-case basis. We are happy to
discuss your use case and find a structure that works for both sides.
