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

The current commercial-only capability keys are defined in `src/MeisterProPR.Application/Features/Licensing/Models/PremiumCapabilityKey.cs`.

- `sso-authentication`
- `parallel-review-execution`
- `multiple-scm-providers`
- `crawl-configs`
- `procursor`

These capability keys describe product rights, not separate source-code licenses.

- The related implementation code may ship in community artifacts.
- The legal boundary is activation and use of commercial-only functionality, not mere possession of the source or binaries.

## Header Conventions

- ELv2 files keep the standard ELv2 header.
- Files that primarily implement commercial-only functionality may add a short notice stating that the file implements commercial-only functionality and that a commercial license is required to activate or use that functionality.
- Mixed/shared files should not claim a different source-code license unless the repo's actual licensing model changes.
