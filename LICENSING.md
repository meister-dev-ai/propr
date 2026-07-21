# Licensing Map

This repository uses one source tree licensed under the Elastic License 2.0 unless another license is stated for a specific path.

Some files in this repository implement commercial-only functionality. Those files may be present in the public source tree and may be shipped in self-hosted or community artifacts. Their presence does not grant the right to activate or use the commercial-only functionality they implement.

A separate commercial license is required to activate or use commercial-only features, including in self-hosted deployments.

## Source License Map

The current source license map is maintained in `docs/source-license-map.md`.

To regenerate it, run:

```bash
./scripts/update-source-license-map.cs
```

## Commercial-Only Capability Map

**The authoritative definition of what is commercial-only lives in the code's licensing feature
definitions — not in this document and not in the per-file header notices.** The capability keys are
defined in `src/MeisterProPR.Application/Features/Licensing/Models/PremiumCapabilityKey.cs` and their
policy (commercial-required, default-when-commercial) in
`src/MeisterProPR.Infrastructure/Features/Licensing/Support/StaticPremiumCapabilityCatalog.cs`; the runtime
gate is `ILicensingCapabilityService` resolving those against the installation edition and any overrides.
That gating is what actually governs activation and use.

The commercial-only capability keys (kept in sync with `PremiumCapabilityKey.cs`) are:

- `sso-authentication`
- `parallel-review-execution`
- `multiple-scm-providers`
- `crawl-configs`
- `budgeting`

Multi-tenancy (the tenants feature) is also commercial-only. It is gated by the installation edition
rather than by a dedicated capability key.

These capability keys describe product rights, not separate source-code licenses.

- The related implementation code may ship in community artifacts.
- The legal boundary is activation and use of commercial-only functionality, not mere possession of the source or binaries.

## Header Conventions

- ELv2 files keep the standard ELv2 header.
- Files that implement or gate commercial-only functionality may add a short notice stating that the file
  implements commercial-only functionality and that a commercial license is required to activate or use
  that functionality.
- **The in-file notice is best-effort and informational only.** It is a convenience marker, maintained by
  hand and by `scripts/update-source-license-map.cs`, and it may be incomplete or lag the code. The
  authoritative determination of what is commercial-only is the licensing feature definitions above
  (`PremiumCapabilityKey.cs`, the capability catalog, and the runtime edition/capability gating), not the
  presence or absence of the notice on any given file. A missing notice never widens a community grant, and
  a present notice never narrows one, beyond what those definitions establish.
- Mixed/shared files should not claim a different source-code license unless the repo's actual licensing model changes.
