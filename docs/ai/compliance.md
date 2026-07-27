# Tenant compliance

Restricting which AI provider families and which endpoint hosts a tenant's clients may reach.

**Commercial only.** These restrictions are set on a tenant, and a Community installation has no editable tenant —
so in Community both lists stay empty and AI traffic is unrestricted. See [editions](../reference/editions.md).

Under **Tenant → Compliance**, a tenant can state where its AI traffic may go. Both lists are independent and
both are empty by default, and **empty means unrestricted** — a tenant that states no policy is unaffected.

| Setting | Meaning |
|---|---|
| `allowedAiProviderKinds` | Only these provider families may be used by this tenant's clients |
| `allowedAiEndpointHosts` | Only these endpoint hosts may be reached |

Host entries match exactly, or match any subdomain when written with a leading dot: `.openai.azure.com` permits
`contoso.openai.azure.com`. A base URL that cannot be parsed is refused rather than allowed through.

The host list is the one that answers "where does our code actually go". The provider family says how traffic is
shaped; for an `openAiCompatible` connection, which can point anywhere, the family alone constrains nothing.

Policy is enforced when a connection is saved **and** again before a credential is used at review time, so
tightening a policy takes effect on existing connections without any cleanup on your side. Every change here is
[audited](../reference/security.md#auditing).

These lists are a policy layer on top of the rules that apply to every installation, licensed or not — see
[outbound request protection](../reference/security.md#outbound-request-protection).

For the order to apply these controls in, and how to prove afterwards that they hold, see
[restricting where your code goes](../guides/restrict-where-code-goes.md).
