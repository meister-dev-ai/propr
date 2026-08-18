# Anonymous usage statistics

Every field this installation sends about itself, what each one is for, what is never collected, how long it
is kept, and how to switch it off.

Once a day a ProPR installation posts an anonymous snapshot of itself to
`https://telemetry.meister-dev.ai/v1/ping`. The response reports newer releases and security advisories
affecting the version it runs. The snapshot contains nothing about your code, your repositories, your
organization or the people using it. Every field it does contain is listed below. **Administration → Usage
Statistics** shows the request body your own installation would send.

## What is sent

Nine fields, and no others. A test in the product's own suite compares this table against the sending code, so
a field added to the payload without an entry here fails the build.

| Field | Type | Example | What it answers |
|---|---|---|---|
| `schemaVersion` | integer | `1` | Which version of this payload the installation speaks |
| `instanceId` | UUID | `9f1c2c8a-3f04-4d9c-9f1a-6d4a9a2b7c31` | Whether two pings on different days came from the same installation, so installations can be counted rather than pings |
| `productVersion` | string | `1.0.0.alpha.0049` | How far behind installations run, which informs how long a release stays supported |
| `edition` | `community` or `commercial` | `community` | How the two editions are used, which informs where engineering effort goes |
| `activeUsers` | range label | `2-5` | The size of a typical installation, which informs performance targets |
| `pullRequestsPerWeek` | range label | `21-100` | Review volume per installation, which informs the throughput the product is designed for |
| `findingsRaisedPerWeek` | range label | `51-250` | How much a review posts |
| `findingsAcceptedPerWeek` | range label, optional | `51-250` | How often authors act on a finding |
| `findingsDismissedPerWeek` | range label, optional | `1-50` | How often authors reject a finding as unwanted, which indicates review noise |

Requests also carry a `User-Agent` of `propr/<productVersion>`, which repeats the version field.

The two optional fields are present only on installations that record finding outcomes, which happens when
per-client code-insight collection has produced at least one. Elsewhere they are left out of the payload
rather than reported as zero, because a zero would be indistinguishable from an installation that measures
nothing.

Their population also differs from `findingsRaisedPerWeek`, which counts every finding posted anywhere in the
installation while the two outcome counters cover only the clients that record outcomes. Divide the two
outcome counters into each other rather than into `findingsRaisedPerWeek`.

## Counters are ranges

Each count is converted to a range label before the payload is built, so the number itself does not leave the
installation. The table below lists every value each counter can carry.

| Counter | Range labels |
|---|---|
| `activeUsers` | `1`, `2-5`, `6-20`, `21-50`, `50+` |
| `pullRequestsPerWeek` | `0`, `1-20`, `21-100`, `101-500`, `500+` |
| `findingsRaisedPerWeek`, `findingsAcceptedPerWeek`, `findingsDismissedPerWeek` | `0`, `1-50`, `51-250`, `251-1000`, `1000+` |

Each top label means "more than the range below it": `50+` is 51 accounts or more, `500+` is 501 pull requests
or more, and `1000+` is 1001 findings or more.

`activeUsers` is a count of accounts that can currently sign in, taken at the moment the snapshot is built.
The three per-week counters cover the period since the previous delivered snapshot, normalized to one week, so
an installation that was offline for a fortnight reports its rate rather than its backlog. Before the first
delivered snapshot the period is the preceding week. The period is bounded to between one day and thirty days,
which keeps a short gap from being extrapolated into an implausible rate and a long one from reporting a stale
average.

`instanceId` is a random value generated on the installation the first time it is needed. It is not derived
from the hostname, the hardware, the license or any account. It is the only value in the payload that persists
across days. Deleting the `usage_statistics_identity` row in your own database is the only way to change it.

## What is never collected

None of the following is in the payload, and no field exists that could carry it.

- Source code, diffs, file names, file paths, or any part of a review's content.
- Repository, project, organization or tenant names and identifiers.
- User names, email addresses, account identifiers, or anything else about a person.
- Pull request titles, descriptions, comments, or the text of any finding.
- AI provider names, endpoint hosts, model names, prompts, or token counts.
- Raw counts of anything. Every counter is a range label.
- License keys, license states, expiry dates, or customer identity of any kind.
- Geographic location, timezone, locale, operating system, or hardware.
- IP addresses. See [what the network sees](#what-the-network-sees) for the transient handling at the edge.

There is no event stream behind this. Each snapshot is computed when it is sent, by querying tables ProPR
already keeps for its own purposes, so switching usage statistics off leaves nothing to collect or delete.

## When a snapshot is sent

Nothing is sent until a platform administrator has been shown what sending means. On a fresh install, and on
an upgrade to the release that introduced this, the gate starts shut and stays shut until one of the
following:

- **Community.** The notice describing the payload is rendered for a platform administrator. Rendering is the
  trigger; dismissing only hides the notice.
- **Commercial.** A platform administrator signs in with a local account. The license relationship covers the
  notice, so no banner is shown.

An installation that no administrator signs in to sends nothing.

After the gate opens, one snapshot is sent per day, at a time that varies within the day so that arrival times
do not act as a second identifier. A send that fails is dropped, with no queue and no retry; the next day's
cycle builds a new snapshot. Delivery does not block, delay or compete with review work, and a failure is not
reported as an operator-facing error.

**Send now** on the administration page runs a cycle immediately instead of waiting. It applies the same rules,
so it sends nothing from an installation that is switched off or has not shown the notice.

## Turning it off

**Community.** Under **Administration → Usage Statistics**, the toggle turns sending off, and it takes effect
immediately. In the off state the installation performs no request and resolves no name for this feature, and a
test in the product's own suite asserts that.

**Commercial.** Sending is active while a commercial license is installed. The control stays visible and
labeled as governed by the license, so administrators can read the current state. Removing the license returns
control to the community toggle in the state it was last left.

There is no environment variable for this, and `DO_NOT_TRACK` is not consulted. The administration control is
the only mechanism, so whether an installation is sending is readable in one place.

## What comes back

The response carries the newest published release and any security advisories that apply to the version you
reported, which the administration UI renders as an update marker. Every field of the response is optional; an
installation that gets an empty answer, or no answer, shows nothing rather than an error, and nothing about
the response triggers an automatic update.

## Where the data goes and how long it is kept

Everything in this section describes the receiving service, which the vendor operates and does not publish.
These are commitments about that service rather than statements you can check against the source in this
repository. What you can check here is what leaves your installation.

| Question | Answer |
|---|---|
| Endpoint | `https://telemetry.meister-dev.ai/v1/ping`, over TLS |
| Operator | Meister DEV, the vendor of ProPR |
| Hosting | Azure Container Apps and Azure Database for PostgreSQL, in the Switzerland North region |
| Storage shape | One row per installation per day, keyed on the instance identifier and the date |
| Retention | Rows for an installation are deleted 180 days after that installation's last ping |
| Processors | None. No third-party analytics, no advertising service, no data broker |

Duplicate pings on the same day overwrite each other rather than accumulating, so a restart loop cannot
inflate what is stored about you.

## What the network sees

Any HTTPS request reveals the client's IP address to the receiving platform while the connection is open. The
handling is bounded: the receiver never logs `X-Forwarded-For` or the remote address, the hosting
environment's HTTP access logs are off, and no monitoring configuration anywhere in the receiver un-masks
addresses. No IP address is written to the database, and none is associated with an instance identifier.

## Where a build sends

The address is fixed when the product is compiled. There is no environment variable and no runtime setting, so
a running installation cannot be redirected, and it cannot be silenced that way either. Where a given build
sends is a property of that build, and the administration page shows the value that build will use.

## Checking it for yourself

1. **The payload preview.** **Administration → Usage Statistics** shows the request body your next snapshot
   would carry, built by the same code that sends it. Opening the preview sends nothing.
2. **The sending code.** It ships in this repository, under
   `src/MeisterDev.ProPR.Application/Features/UsageStatistics`, and the type that defines the wire payload is
   `UsageStatisticsSnapshot`.
3. **Your own egress logs.** With the feature off, `telemetry.meister-dev.ai` never appears in them.

## Running without internet access

An installation on an isolated network cannot reach the receiver, and nothing degrades as a result. The daily
attempt fails, the snapshot is discarded, and reviews are unaffected. Turning the feature off avoids the
attempt altogether. See [running without internet access](../guides/air-gapped.md).

## Contact

Privacy questions about this payload go to `privacy@meister-dev.ai`. To report a vulnerability, see
[SECURITY.md](../../SECURITY.md) instead.
