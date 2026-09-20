# Tenant compliance

Restricting which AI provider families and which endpoint hosts a tenant's clients may reach.

**Commercial only.** These restrictions are set on a tenant, and a Community installation has no editable tenant -
so in Community both lists stay empty and AI traffic is unrestricted. See [editions](../reference/editions.md).

Under **Tenant → Compliance**, a tenant can state where its AI traffic may go. Both lists are independent and
both are empty by default, and **empty means unrestricted** - a tenant that states no policy is unaffected.

| Setting | Meaning |
|---|---|
| `allowedAiProviderKinds` | Only these provider families may be used by this tenant's clients |
| `allowedAiEndpointHosts` | Only these endpoint hosts may be reached |

Host entries match exactly, or match any subdomain when written with a leading dot: `.openai.azure.com` permits
`contoso.openai.azure.com`. A base URL that cannot be parsed is refused.

The host list answers where traffic actually goes. The provider family answers how it is shaped, and a
`meisterdev/openAiCompatible` connection can point anywhere, so on its own the family constrains no destination.

## Host list verification

An AI connection's base URL and every host its provider family declares it reaches are checked against the list,
and all of them must be on it. A family whose endpoint is fixed by its vendor carries no base URL, so for that
family the check runs against the declared hosts alone.

A declared host is checked by containment: the entry has to name at least every host the declared pattern admits.
An entry `.example.com` permits a family declaring `api.example.com`. An entry `api.example.com` refuses a family
declaring `.example.com`, because that family reaches subdomains the entry does not name.

A family that reaches an authorization host as well as a model host needs both permitted. Until you permit the
second, that family is refused, and the refusal names the host to add. The **AI provider add-ins** page under
Administration lists what each family declares.

Policy is enforced when a connection is saved **and** again before a credential is used at review time, so
tightening a policy takes effect on existing connections without any cleanup on your side. Every change here is
[audited](../reference/security.md#auditing).

These lists are a policy layer on top of the rules that apply to every installation, licensed or not - see
[outbound request protection](../reference/security.md#outbound-request-protection).

For the order to apply these controls in, and how to prove afterwards that they hold, see
[restricting where your code goes](../guides/restrict-where-code-goes.md).
