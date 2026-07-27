# Azure DevOps

Everything needed to connect ProPR to Azure DevOps, hosted or self-hosted: which authentication mode
each host variant accepts, what to enter, where to get it, how to register the webhook, and what to fix
when verification fails. The fields themselves are described once in
[common provider connection fields](index.md#common-provider-connection-fields).

Azure DevOps authentication depends on the host variant:

- Azure DevOps Services on `https://dev.azure.com` or `*.visualstudio.com` uses `oauthClientCredentials`.
- Self-hosted Azure DevOps Server uses `personalAccessToken` or `windowsUserAccount`.
- `appInstallation` is not accepted for Azure DevOps.

Either way the organization or collection goes into a provider scope, never into `hostBaseUrl`.

## Azure DevOps Services

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | `https://dev.azure.com` | Fixed value for Azure DevOps Services |
| `authenticationKind` | `oauthClientCredentials` | The only mode hosted Azure DevOps accepts |
| `oAuthTenantId` | Microsoft Entra tenant ID (directory ID) | Azure Portal -> Microsoft Entra ID -> Overview -> Tenant ID, or App registrations -> your app -> Overview -> Directory (tenant) ID |
| `oAuthClientId` | Application (client) ID | Azure Portal -> App registrations -> your app -> Overview -> Application (client) ID |
| `secret` | Client secret **value** | Azure Portal -> App registrations -> your app -> Certificates & secrets -> Client secrets -> Value |
| `displayName` | Friendly label | Any descriptive value |

Use the secret value, not the secret ID, and the tenant ID, not a subscription ID or the service
principal object ID. The service principal must be usable against the target organization and project.

## Azure DevOps Server

Do not use `https://dev.azure.com` for a self-hosted server; that host is only for Azure DevOps
Services. Both self-hosted modes require an HTTPS `hostBaseUrl` — HTTP is rejected when you save.

Prepare the endpoint before creating the connection:

1. Expose Azure DevOps Server over HTTPS, and make sure the certificate's subject alternative names
   cover the exact host name or IP address ProPR will call.
2. From the machine or container ProPR runs in — not your browser — run
   `curl https://<ado-server-host>/`. Expect the normal Azure DevOps page or a `401` challenge, not a
   timeout and not a certificate error.
3. If the certificate is self-signed or issued by a private CA, install it or its issuing CA into that
   runtime's trust store. ProPR does not manage certificate trust. On Debian- or Ubuntu-based
   runtimes:

   ```bash
   sudo cp <your-cert>.crt /usr/local/share/ca-certificates/
   sudo update-ca-certificates
   ```

   Then re-run `curl` without `-k` or any other insecure override.
4. If the TCP connection itself times out, fix the firewall or routing before touching credentials.
5. Give the account ProPR authenticates as at least Basic access level, plus permission to read the
   target collection, its repositories, and its pull requests.

### Personal access token

Use `personalAccessToken` when PATs are enabled on the server.

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | Your Azure DevOps Server base URL | Example: `https://ado-server.example.com/tfs` |
| `authenticationKind` | `personalAccessToken` | Select PAT mode for the self-hosted host |
| `secret` | Azure DevOps Server PAT | Create it from the Azure DevOps Server user settings area |
| `userName`, `oAuthTenantId`, `oAuthClientId` | leave empty | Not used in this mode |

### Windows user account

Use `windowsUserAccount` when the server accepts explicit Windows credentials.

| ProPR field | Expected value | Where to get it |
|---|---|---|
| `hostBaseUrl` | Your Azure DevOps Server base URL | Example: `https://ado-server.example.com/tfs` |
| `authenticationKind` | `windowsUserAccount` | Select Windows user-account mode for the self-hosted host |
| `userName` | Windows login recognized by the server | Example: `CONTOSO\ado-user` |
| `secret` | Password for that Windows account | Enter the account password directly |
| `oAuthTenantId`, `oAuthClientId` | leave empty | Not used in this mode |

Notes for this mode:

- Prefer the domain-qualified login shape `DOMAIN\user` so the runtime can split domain from account
  name for NTLM or Negotiate authentication.
- `userName` is stored separately from the password and is returned in API responses so edit screens
  can show it. The password stays protected secret material and is never returned.
- ProPR authenticates with the stored credentials only. Host-integrated Windows authentication — the
  identity of the ProPR process itself — is not used.
- ProPR enables managed NTLM on Linux and WSL runtimes by default.

## Scope examples

| ProPR field | Azure DevOps Services | Azure DevOps Server |
|---|---|---|
| `scopeType` | `organization` | `organization` |
| `externalScopeId` | `my-org` | `DefaultCollection` |
| `scopePath` | `https://dev.azure.com/my-org` | `https://ado-server.example.com/tfs/DefaultCollection` |
| `displayName` | `My Org` | `Default Collection` |

The scope must point at the real organization or collection URL and use the same host as the
connection. A scope left pointing at an old host or an old collection is a common cause of
verification failures.

## Global Azure fallback

If a client has no Azure DevOps provider connection of its own, hosted Azure DevOps operations can
fall back to the global Azure credential of the backend process. Self-hosted Azure DevOps Server never
falls back: without stored credentials it fails with an explicit error telling you to re-save the
connection or re-add the scope.

That credential is configured on the backend process itself rather than per client; for the variables
that supply it, see
[a process-wide Azure credential](../operate/configuration.md#a-process-wide-azure-credential).

## Webhook registration

Create the configuration in ProPR first; see
[create the webhook configuration](webhooks.md#create-the-webhook-configuration). In the repository
filters, prefer the canonical repository entry discovery offers — it stores the repository GUID and
avoids the name-versus-id mismatch described below.

Register the listener under **Project settings → Service hooks → new subscription → Web Hooks**. The
secret travels as **Basic** authentication: any user name, and the generated secret as the password.

Event mapping:

| ProPR event | Azure DevOps event |
|---|---|
| PR Created | Pull request created |
| PR Updated | Pull request updated |
| PR Commented | Pull request commented on |

ProPR classifies the Azure DevOps comment hook, so **PR Commented** takes effect here.

### Repository and project identifiers

Azure DevOps payloads carry `resource.repository.id`, usually a repository GUID. Some Azure DevOps
APIs require a project *name* rather than a project GUID when a repository is referenced by name. The
mismatch surfaces as:

> A project name is required in order to reference a Git repository by name

Either fix works:

- Configure the repository filter using the canonical repository reference (the GUID) the frontend's
  discovery offers. Recommended.
- Or make sure the configuration stores the project **name**, not a GUID, when the repository is
  referenced by name.

### Testing the listener

The Azure DevOps invocation of the
[synthetic-event helpers](webhooks.md#testing-with-synthetic-events):

```bash
# sends created, updated and commented for PR 24
bash scripts/send-ado-webhook.sh \
  -u "https://propr.example.com/webhooks/v1/providers/ado/<pathKey>" \
  -s "<generated-secret>" -r "<repository-guid-or-name>" -i 24
```

## Troubleshooting

A `400` when saving the connection is usually one of the Azure DevOps field rules in
[HTTP 400 when saving a connection](index.md#http-400-when-saving-a-connection).

### Which Azure ID is the tenant ID?

Use the Microsoft Entra tenant or directory ID — not the subscription ID, not the service principal
object ID, and not the secret ID.

### Azure DevOps Server verification or reviewer resolution fails

| Error or symptom | What to fix |
|---|---|
| `Basic authentication requires a secure connection to the server.` | The connection host **and** the provider scope must both use `https://`. If the connection was first created against an HTTP or different host, re-save it and re-point the scope at the current HTTPS host. |
| SSL, trust, or certificate-validation error | The certificate's subject alternative names must cover the host or IP ProPR calls, and the issuing CA must be trusted inside the ProPR runtime. Re-test with `curl https://<ado-server-host>/` without `-k`. |
| Timeout on connect | A firewall or routing problem, not a credential problem. |
| Authentication rejected in PAT mode | PATs may be disabled on that server instance; use `windowsUserAccount` instead. |
| PAT works but `windowsUserAccount` does not | Save `userName` in the server-accepted format, preferably `DOMAIN\user`, then restart ProPR and retry. If the runtime still behaves as unauthenticated, install the Linux NTLM support package and restart: `sudo apt update && sudo apt install -y gss-ntlmssp`. |

For anything not listed here — reviewer identity, deliveries, reviews — start at
[troubleshooting](../operate/troubleshooting.md).
