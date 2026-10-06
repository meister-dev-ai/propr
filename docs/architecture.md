# Runtime architecture

The API composes application modules and provider adapters. Application ports carry provider-neutral
review and crawl evidence; Domain owns durable state and evidence enums; Infrastructure owns PostgreSQL
mapping, migrations and SCM adapters. Review execution remains separate from analytics collection and
queries. ProCursor is accessed through its bounded gateway contract.

| Ownership | Reference |
|---|---|
| Review intake, execution, publication and diagnostics | [Reviews](concepts/reviews.md), [runner architecture](reference/runner-architecture.md) |
| Code Insights evidence, projections, score queries and reports | [Code Insights](architecture/code-insights.md) |
| Credentials and tenant isolation | [Security](reference/security.md) |
| Durable deployment state and backups | [Upgrades and backups](operate/upgrades-and-backups.md) |
