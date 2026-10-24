# Tenant data

ProPR has four endpoints for the data of a tenant: tenant deletion, tenant export, protocol retention and
identity erasure. They are independent of each other and are under `/api/admin/tenants/{tenantId}`. Only a
platform administrator can call them.

## Tenant deletion

`DELETE /api/admin/tenants/{tenantId}` first stops the work of the tenant, and a later request deletes it. Each
request deactivates the tenant, its clients and their webhook, crawl, mention and ProCursor configurations, so the
intake accepts no new work for them. It closes the discussion synchronizations of the clients, stops their
unfinished reviews and cancels their waiting thread passes, mention replies and ProCursor index jobs.

While work of the tenant may still be running, the request answers `409` with the error `tenant_work_running`, and
the `Retry-After` header gives the seconds to wait:

- After a request has stopped any work, the deletion waits one minute. By then an intake that read an active
  configuration before the stop has stored its job, and the next request stops that job.
- A stopped review can run until its lease expires, and the deletion waits until then.
- A thread pass or a mention reply that is running holds no lease. The deletion waits up to 15 minutes after it
  started.
- A discussion synchronization that holds a lease ends when the lease expires.
- A running ProCursor index job is left to finish. The deletion waits up to one hour after it started.

A request after the wait deletes the tenant, its clients and every row that carries the identifier of the tenant or
of one of its clients. ProPR deletes the ProCursor data first, while the knowledge sources that identify it still
exist: the index jobs, the snapshots with their chunks and symbols, and the token usage. The ProCursor data can be in
a separate ProCursor database and has a transaction of its own. The rows of the ProPR database follow in one
transaction. The answer lists the rows deleted per table. Rows that reach the tenant through a foreign key, such as
file results, protocols and their events, are deleted with the row they reference.

Accounts that belong to this tenant alone are deleted with it. ProPR keeps an account that is a platform
administrator, is a member of another tenant, or has a role on a client of another tenant. The system tenant
cannot be deleted.

The licensing records, which count pull request authors per month, belong to the installation, and ProPR keeps
them.

## Tenant export

`GET /api/admin/tenants/{tenantId}/export` returns one JSON document in the format `propr.tenant-export`, version 1:

| Part | Content |
| --- | --- |
| `tenant` | Identifier, slug, display name, state, creation time and protocol retention. |
| `clients` | Each client with its display name, review settings, SCM connections and scopes, thread memory and review jobs. |
| `reviewJobs` | The pull request, its author, the status, the summary, the result and the file results with their comments. |
| `protocols` | With `includeProtocols=true`: the protocols of each review job and their events. |

The export leaves out credentials, secret material and embeddings. It is read in one database snapshot, so its
parts agree with each other.

## Protocol retention

`PUT /api/admin/tenants/{tenantId}/protocol-retention` with `{"days": 90}` sets how many days the protocol events of
the tenant are kept, from 1 to 3650; `{"days": null}` keeps them. Once an hour, ProPR deletes the events that
are older. It keeps the review jobs, their results, the file results and the protocol totals, so a job report
still shows its findings and its token counts, and the event timeline of an older protocol is empty.

## Identity erasure

`POST /api/admin/tenants/{tenantId}/identity-erasures` erases one developer's identity on the instruction of the
customer, who is the controller of the review records:

```json
{ "externalUserId": "4711", "login": "dev", "instruction": "DPA-2026-17" }
```

ProPR clears the pull request author of the review jobs and the comment author of the mention replies that name
the identifier or the login, and replaces the author of retained thread comments with `erased`. It deletes the
protocol events of those executions, because their prompts can quote the developer. Records of other tenants are
not changed. The tenant audit records the erasure with the instruction reference and a hash of the identifier,
not the identifier itself.
