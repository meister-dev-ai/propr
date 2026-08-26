# Licensing Map

This repository uses one source tree licensed under the Elastic License 2.0 unless another license is stated for a specific path.

Some files in this repository implement commercial-only functionality. Those files may be present in the public source tree and may be shipped in self-hosted or community artifacts. Their presence does not grant the right to activate or use the commercial-only functionality they implement.

A separate commercial license is required to activate or use commercial-only features, including in self-hosted deployments.

## Source License Map

The current source license map is maintained in `docs/reference/source-license-map.md`.

To regenerate it, run:

```bash
./scripts/update-source-license-map.cs
```

## Commercial-Only Capability Map

**The authoritative definition of what is commercial-only lives in the code, not in this document and not in
the per-file header notices.**
`src/MeisterDev.ProPR.Application/Features/Licensing/Models/PremiumCapabilityKey.cs` defines the capability
keys. `src/MeisterDev.ProPR.Infrastructure/Features/Licensing/Support/StaticPremiumCapabilityCatalog.cs`
defines their policy: commercial-required and default-when-commercial. `ILicensingCapabilityService` resolves
both against the installation edition and any overrides, and that resolution governs activation and use.

The commercial-only capability keys (kept in sync with `PremiumCapabilityKey.cs`, in its canonical order) are:

- `sso-authentication`
- `parallel-review-execution`
- `distributed-execution`
- `multiple-scm-providers`
- `crawl-configs`
- `mention-answering`
- `budgeting`
- `code-insights`
- `multi-tenancy`

An installation runs the built-in System tenant alone unless `multi-tenancy` is effective, which means the
license names it and no override has disabled it. What each key
covers, and what an installation does without it, is in
[editions and licensed features](docs/reference/editions.md).

These capability keys describe product rights, not separate source-code licenses. The implementation code may
ship in community artifacts. A commercial license is required to activate or use commercial-only functionality,
not to possess the source or the binaries.

## How Commercial-Only Functionality Is Activated

A platform administrator activates the commercial edition by uploading the signed license file issued to the
organization, or by pasting its contents. The file states the licensee, the term, the capability keys it grants
and the limits it sets. ProPR verifies it offline against a root certificate committed in this source tree, and
makes no network call on any path. The verified license is then stored in the installation's own database,
protected by its data-protection key ring. A term has a warning window of 30 days before it ends and a grace
window of 14 days after it, and the same file can be activated again at any point up to the end of that grace
window. [Editions and licensed features](docs/reference/editions.md) documents the mechanism, the refusal
reasons, and what an installation does at each stage.

[The commercial license policy](COMMERCIAL-LICENSE-POLICY.md) states which installations one license may be
activated on and how its limits are counted across them.

The mechanism ships as source, so it can be edited out. Moving, changing, disabling or circumventing it breaches
the Elastic License 2.0 restriction on license key functionality, and under the same license the rights it
granted terminate. See [LICENSE](LICENSE) and
[what this mechanism does and does not do](docs/reference/editions.md#what-this-mechanism-does-and-does-not-do).

## Header Conventions

- ELv2 files keep the standard ELv2 header.
- Files that implement or gate commercial-only functionality may add a short notice: the file implements
  commercial-only functionality, and a commercial license is required to activate or use that functionality.
- Files that implement the license key functionality itself may add a short notice: the file implements license
  key functionality, and license logic may not be moved, changed, disabled or circumvented. This notice is
  separate from the commercial-only one. The licensing mechanism runs in both editions and decides
  what an installation is entitled to, so it is not itself something an entitlement unlocks. The restriction it
  names is the license key limitation under Limitations in [LICENSE](LICENSE), and it binds every recipient of
  the source whether or not a file carries the notice. A file that both implements license key functionality
  and gates a commercial capability carries both notices.
- **A file whose only type is an enum carries neither notice.** It declares a set of names and no mechanism, so
  the notices go on the code that reads those names.
- **The in-file notices are informational.** They are convenience markers, maintained by hand and by
  `scripts/update-source-license-map.cs`, and they may be incomplete or lag the code. The authoritative
  determination of what is commercial-only is the licensing feature definitions above, not the presence or
  absence of a notice on any given file. A missing notice never widens a community grant, and a present notice
  never narrows one, beyond what those definitions establish. The Elastic License 2.0 restriction on license key
  functionality likewise applies from the license text, not from the notice.
- Mixed or shared files should not claim a different source-code license unless the repository's licensing model changes.
