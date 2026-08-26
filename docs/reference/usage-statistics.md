# Usage statistics

ProPR sends one report a day to `https://telemetry.meister-dev.ai/v1/ping`. The report describes the
installation: which version it runs, which edition, and how much it is used. It contains no code, no
repository names and no user names.

A Community installation's report is anonymous, and you can switch it off. A commercial installation also
sends its license identifier and its usage against the license limits, and keeps sending while the license is
in force.

**Administration → Usage Statistics** shows the exact request body your installation would send.

## What is sent

| Field | Type | Example | What it answers |
|---|---|---|---|
| `schemaVersion` | integer | `1` | Which version of this payload the installation speaks |
| `instanceId` | UUID | `9f1c2c8a-3f04-4d9c-9f1a-6d4a9a2b7c31` | Whether two reports came from the same installation |
| `productVersion` | string | `1.0.0.alpha.0049` | Which version you run, so releases stay supported long enough |
| `edition` | `community` or `commercial` | `community` | Which edition you run |
| `activeUsers` | range label: `1`, `2-5`, `6-20`, `21-50`, `50+` | `2-5` | The size of the installation |
| `pullRequestsPerWeek` | range label: `0`, `1-20`, `21-100`, `101-500`, `500+` | `21-100` | Review volume |
| `findingsRaisedPerWeek` | range label: `0`, `1-50`, `51-250`, `251-1000`, `1000+` | `51-250` | How much a review posts |
| `findingsAcceptedPerWeek` | range label, optional | `51-250` | How often authors act on a finding |
| `findingsDismissedPerWeek` | range label, optional | `1-50` | How often authors reject a finding |
| `licenseId` | string, commercial only | `0f4c1b7a-6d21-4f36-9e18-5a7b3c9d2e40` | Which license the installation runs under |
| `licensingIdentity` | UUID, commercial only | `4f6b1d02-9c58-4f7a-8f2e-1d3c5b7a9e04` | Which installation sent the report, as shown on the licensing page |
| `systemProfileHash` | 64 hex characters, commercial only | `2f0d9a71c48b3e5602d17ac9fb84e3d5a6c012bf7d94e8315a0bc6d729f4e81c` | Whether two installations under one license run on the same system |
| `consumedClients` | integer, commercial only | `12` | Clients held, against the limit the license states |
| `consumedRunners` | integer, commercial only | `4` | Runners holding a valid credential, against the limit the license states |
| `peakConcurrentReviews` | integer, commercial only | `2` | Most reviews running at once on the previous day, against the limit the license states |
| `consumedAuthorsPerMonth` | integer, commercial only | `37` | Distinct pull request authors this month, automation excluded, against the limit the license states |

Requests carry a `User-Agent` of `propr/<productVersion>`.

The two optional finding counters appear only where per-client code-insight collection has recorded an
outcome.

## The commercial fields

`licenseId` identifies the license, and a license is issued to a named organisation, so a commercial report is
not anonymous.

`systemProfileHash`, `peakConcurrentReviews` and `consumedAuthorsPerMonth` are sent only where the
installation has that measurement. A missing field means there is no measurement, not zero.

One license may cover several installations, such as production, a standby site and a staging system. How
their counts are combined depends on the license agreement and the terms of that particular license - see
[the commercial license policy](../../COMMERCIAL-LICENSE-POLICY.md).

## What is never collected

ProPR transmits nothing outside the fields listed above.

## When a report is sent

ProPR sends nothing until a platform administrator has been shown what the report contains. After that it
sends one report a day. A failed send is dropped and the next day builds a new report.

## Turning it off

On a Community installation, the toggle under **Administration → Usage Statistics** takes effect immediately.
Switched off, the installation makes no request.

On a commercial installation, sending is active while the license is in force and cannot be switched off.
Removing the license returns control to the Community toggle.

## What comes back

The response carries the newest published release and any security advisories affecting the version you
reported, which the administration UI shows as an update marker. Nothing in the response triggers an update.

## Where the data goes and how long it is kept

| Question | Answer |
|---|---|
| Endpoint | `https://telemetry.meister-dev.ai/v1/ping`, over TLS |
| Operator | Meister DEV, the vendor of ProPR |
| Hosting | Azure Container Apps and Azure Database for PostgreSQL, in the Switzerland North region |
| Storage shape | One row per installation per day |
| Retention | Rows are deleted 180 days after that installation's last report |
| Processors | None. No third-party analytics, no advertising service, no data broker |

## What the network sees

Any HTTPS request reveals the client's IP address to the receiving platform while the connection is open. The
receiver does not log it, does not store it, and associates none with an installation.

## Checking it yourself

1. **Administration → Usage Statistics** shows the request body your next report would carry. Opening the
   preview sends nothing.
2. The sending code is in this repository under
   `src/MeisterDev.ProPR.Application/Features/UsageStatistics`.
3. With the Community feature off, `telemetry.meister-dev.ai` never appears in your egress logs.

An installation with no internet access cannot reach the receiver, and reviews are unaffected - see
[running without internet access](../guides/air-gapped.md).

## Contact

Privacy questions about this report go to `privacy@meister-dev.ai`. To report a vulnerability, see
[SECURITY.md](../../SECURITY.md) instead.
