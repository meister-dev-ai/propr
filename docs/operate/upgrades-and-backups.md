# Upgrades and backups

How to roll forward to a new release, and what has to be in a backup for a restore to actually work.

## Upgrading

Pending database migrations are applied automatically the first time a new image starts, for both the API
and ProCursor. There is no downgrade path, so snapshot the databases before rolling forward.

Review jobs left mid-flight by a crash or a restart are reset to pending on the next start and picked up
again.

For release-based deployments, move the three runtime images - listed under
[running published images](deploy.md#running-published-images) - to the same `<tag>` together. Stable
releases also publish `latest` for all three; pre-release tags do not move `latest`.

If the first start on the new tag does not come up, work from [troubleshooting](troubleshooting.md).

## What to back up

Three things, and they only work together:

| Back up | Why |
|---|---|
| The ProPR database | Clients, connections, reviews, findings, protocols, thread memory |
| The ProCursor database, if it is separate | Indexes, snapshots and ProCursor token usage |
| The encryption key ring, at the path `MEISTER_DATA_PROTECTION_KEYS_PATH` names | Everything the databases hold encrypted - see [the encryption key ring](../reference/security.md#the-encryption-key-ring) |

Restore all three together. A restore without the key ring is a failed restore, not a partial one.

The review workspace directory does not need backing up; it is a cache - see
[review workspace](deploy.md#review-workspace).
