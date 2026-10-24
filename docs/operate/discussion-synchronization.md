# Pull-request discussion synchronization

ProPR refreshes discovered pull-request discussions every five minutes by default. Accepted webhook
notifications request an earlier refresh. Notifications are coalesced and remain pending across restarts.
Provider notification coverage varies, so periodic reconciliation is required. Pull-request discovery
uses its own `PR_CRAWL_INTERVAL_SECONDS` setting.

The worker checks PostgreSQL once per second for due work when processing capacity is available.
Each check reads database time and a bounded list of due pull requests; it does not fetch provider discussions.
The default bounds are eight operations globally, four per provider host, and one per client and host.
An operation has a 90-second deadline. Failed operations retain pending work and retry after 15 seconds.
Unavailable connections retry at the reconciliation interval.

Active review admission reads current discussion and pull-request evidence without waiting for
resolution-memory generation. The discussion worker processes pending memory under its existing
deadline. Completed memory work survives cancellation of a later thread. Discussion edits, resolution
or grounding changes, and AI configuration changes can require memory processing; an unrelated
commit does not regenerate unchanged memory. Closed observations still require final consumer evidence.

An admitted review can fail before making a model call if its configured AI provider is unavailable on
the API host. Inspect the job error and the provider add-in inventory. Local launches must supply
`AI_PLUGIN_DIRECTORY` when an activated private provider is installed outside the default directory.

Provider load scales with the number of due pull requests and the pages needed to enumerate their
discussions. Notifications can add reads between reconciliations. Providers with native thread reads
can fetch only identified threads when a full reconciliation is current. Other refreshes enumerate
the discussion. Unchanged discussions still require provider reads and pull-request progress writes;
current consumers skip per-thread classification and content writes. Enabled archive retention adds
encrypted writes for changed comments. Slow providers and queued work can delay completion.

[Configuration](configuration.md#discussion-synchronization) lists cadence, concurrency and deadline
settings. Raising the reconciliation interval reduces periodic provider traffic. Reducing concurrency
reduces simultaneous database and provider work.

The `MeisterDEV.ProPR` meter exposes `meisterpropr_discussion_*` instruments for synchronization duration
and outcomes, changed and unchanged threads, database commands and affected rows, scheduling delay, and
provider requests. Provider transport byte and timing measurements currently cover Azure DevOps.
Unknown response lengths have a separate counter; reported lengths come from `Content-Length`.
Database command counts exclude explicit advisory-lock and fence-validation commands.
