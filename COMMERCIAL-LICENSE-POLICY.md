# Commercial License Policy

A ProPR commercial license covers the installations you operate.

[COMMERCIAL.md](COMMERCIAL.md) describes the two licensing options and what a commercial license grants.
[LICENSE](LICENSE) and your signed commercial agreement are the terms that bind, and where they differ from
this document, they govern. For what the product does at runtime, see
[editions and licensed features](docs/reference/editions.md).

## What a license authorizes

The signed license file states an entitlement in four parts:

| Part | What it states |
|---|---|
| Licensee | The organization the entitlement belongs to |
| Term | The period it runs for, with a grace window after it |
| Capabilities | Which commercial capabilities are included |
| Limits | The ceilings for pull request authors per month, clients, runners, and concurrent reviews |

The file names no machine, database, network address, installation or user account, and activating it contacts
nothing. This document sets where you may activate it.

## Where you may activate it

You may activate the same license file on any installation the licensee operates in support of the entitlement
it states. That covers:

- production;
- a disaster-recovery standby, whether it is warm and idle or restored on demand;
- blue/green and equivalent paired deployments, including the period in which both halves are running;
- staging, test, QA, demonstration and training installations;
- development installations, including one per developer;
- an installation rebuilt from backup, which is the same installation coming back.

None of these need a second license, and none need to be requested. Each installation records its own
activations in its own license history, and no count is kept across installations.

An installation is one ProPR deployment with its own database. Several API replicas and workers that share one
database form a single installation. They share its identity, its license and its capability state, and the
runners enrolled with them belong to it. An installation holds one license file at a time, and activating
another replaces it immediately.

## How limits are counted

Each installation counts its own clients, enrolled runners, concurrent reviews and authors. For clients,
runners and concurrent reviews the license covers the total across your estate: an entitlement for 8 runners
is 8 runners across the estate, not 8 on each installation.

Authors are the exception and are held per installation. Only a count of distinct authors travels, never a
name or an account, so two installations that both reviewed work by the same person each count that person,
and adding the two together would count that person twice. Each installation is therefore held to the authors
number by itself.

Non-production installations count the same way, and most hold few clients and run few reviews. An
installation labelled "staging" that carries the client set and the review volume of a second production
counts as a second production.

**Enforcement happens on each installation.** Clients, enrolled runners and concurrent reviews each have a
ceiling on the installation that holds them. Creating a client, enrolling a runner or claiming a concurrent
review is refused once that installation reaches its ceiling. Distinct pull request authors per month are
counted and recorded, and going above that number never refuses, delays or degrades any work. See
[the limits it states](docs/reference/editions.md#the-limits-it-states).

**The estate-wide total is contractual.** No installation can see what the others hold, so the product does
not compute the total. An administrator sees what the license states beside what that installation uses. A
commercial installation also reports its own consumption to us once a day, and that is how the estate total is
reconciled at renewal or on request. The report carries no license document, no licensee name and no host,
repository or project names, and it changes nothing about what an installation may do - see
[usage statistics](docs/reference/usage-statistics.md).

Tell us when the estate outgrows the entitlement. Installation ceilings keep applying while it is reconciled,
and an author overage stays reporting-only throughout.

## What needs a license of its own

- **Another legal entity.** The entitlement belongs to the licensee named in the license. A subsidiary, a
  parent company, a joint venture, a customer, a contractor's own organization or an acquired business is a
  different legal entity and needs its own license unless the commercial agreement names it.
- **Independent productions that each consume the full entitlement.** The permitted estate is the one serving
  the production workload the license was issued for, together with its standby and its pre-production copies.
  Two divisions each running their own production installation, with their own client sets and their own review
  volumes, are using two entitlements under one license.
- **Providing ProPR to third parties as a hosted or managed service.** This is a separate grant, and the
  Elastic License 2.0 restricts it in either edition. See [COMMERCIAL.md](COMMERCIAL.md).
- **Changing, disabling or removing the licensing mechanism.** The source is available and the mechanism can be
  edited out of it. Doing so breaches the Elastic License 2.0 restriction on circumventing license key
  functionality, and the rights it granted terminate. See
  [what this mechanism does and does not do](docs/reference/editions.md#what-this-mechanism-does-and-does-not-do).

## Rebuilding, renewing, and the grace window

You can activate the same license file again at any point up to the end of its grace window, which runs for 14
days after the term ends. An installation rebuilt from a backup therefore comes back entitled on the file it
already has. After the grace window ProPR refuses the file, because storing it would grant nothing. Ask for a
renewal instead.

A renewal is a new file with a later term. Activating it replaces the file on the installation at any stage:
inside the term, in the warning window, in the grace window, or after the installation has reverted to
Community. See
[what happens as a license runs out](docs/reference/editions.md#what-happens-as-a-license-runs-out).

## Questions

If this document does not answer your situation, ask: an estate shape that does not fit the descriptions
above, an entity boundary that is unclear, or an entitlement that needs changing mid-term. Commercial terms
are handled case by case. See [COMMERCIAL.md](COMMERCIAL.md).
