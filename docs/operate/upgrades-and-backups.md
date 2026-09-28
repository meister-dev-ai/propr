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

### A relative key-ring path now resolves against the service's own directory

`MEISTER_DATA_PROTECTION_KEYS_PATH` resolved a relative path against the working directory and now resolves
it against the directory the service's own files sit in. Surrounding whitespace is trimmed. A service started
from a different directory than its own therefore reads a different key ring after the upgrade, and
everything the databases hold encrypted becomes unreadable. Make the path absolute before you roll forward,
and move the existing key files to it. A path that is already absolute needs nothing.

### Private addresses and plain HTTP on source-control connections need the egress opt-in

A source-control connection follows the same address rule as an AI endpoint. A `hostBaseUrl` naming a
private, loopback or link-local address is refused when the connection is created, patched or verified
unless the installation sets `MEISTER_ALLOW_PRIVATE_EGRESS=true`. Plain HTTP reaches a private, loopback or
link-local host once that opt-in is set, and reaches a public host under no setting:
`http://git.example.com` stays refused. Set the opt-in before you upgrade if you run a self-hosted GitLab,
Forgejo, GitHub Enterprise or Azure DevOps Server on an internal address. `AI_ALLOW_PRIVATE_EGRESS` is read
as the earlier name of the same setting.

## What to back up

These things, and they only work together:

| Back up | Why |
|---|---|
| The ProPR database | Clients, connections, reviews, findings, protocols, thread memory |
| The ProCursor database, if it is separate | Indexes, snapshots and ProCursor token usage |
| The encryption key ring, at the path `MEISTER_DATA_PROTECTION_KEYS_PATH` names | Everything the databases hold encrypted - see [the encryption key ring](../reference/security.md#the-encryption-key-ring) |
| The certificate at `MEISTER_DATA_PROTECTION_CERTIFICATE_PATH`, where the certificate protector is configured | The key ring is encrypted with it, and the key files alone read nothing. See [the certificate protector](configuration.md#the-certificate-protector) |
| The vault key `MEISTER_DATA_PROTECTION_AZURE_KEY_VAULT_KEY_ID` names, where the Azure Key Vault protector is configured | The key ring is wrapped with it, and a restored deployment needs an identity that can unwrap. See [protecting the key ring with Azure Key Vault](deploy.md#protecting-the-key-ring-with-azure-key-vault) |

Restore them together. A restore without the key ring is a failed restore, not a partial one, and under a
key-ring protector the same holds for what protects the key files: where the certificate protector is
configured, hold the certificate as carefully as the key ring. Keep its password in a secret store apart from
the certificate backup, so one copied backup set carries neither the key ring nor the password on its own,
and make sure whoever runs the restore can reach both. Where a key service protects the ring, the restored
installation needs access to the same key, and its identifier goes in the backup notes with everything
else.

The review workspace directory does not need backing up; it is a cache - see
[review workspace](deploy.md#review-workspace).
