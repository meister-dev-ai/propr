# Tenant compliance

Restricting which AI provider families and which endpoint hosts a tenant's clients may reach, and whether its
reviews keep the model's reasoning.

**Commercial only.** These settings live on a tenant, and a Community installation has no editable tenant, so in
Community both lists stay empty, AI traffic is unrestricted, and reasoning capture follows the installation
switch. See [editions](../reference/editions.md).

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

## Model reasoning in the job trace

A reasoning model can return a summary of how it reached its answer, and that summary can quote the code under
review. ProPR keeps it in the job trace beside the assistant text and the tool calls.

Under **Tenant → Compliance**, a tenant states one of three values.

| Value | Meaning |
|---|---|
| `installationDefault` | The installation switch `AI_CAPTURE_REASONING_IN_PROTOCOL` decides |
| `enabled` | Reviews of this tenant keep the reasoning |
| `disabled` | Newly started reviews of this tenant keep no reasoning |

`disabled` also stops the review asking the provider for a reasoning summary, so less text crosses the wire.
Reasoning token counts arrive in the provider's usage report, which is sent whether or not a summary was asked
for, so the counts are recorded under all three values and spend and budgets stay accurate. The control names
what the installation switch is currently set to, so an operator can see what `installationDefault` does here.

### When a change takes effect

A review that runs inside ProPR resolves the policy once, as the job starts, and records the whole review under
that decision.

A review that runs on an enrolled runner is checked again at every crossing. The control plane resolves the
policy at dispatch and carries it in the job manifest, and it applies the policy in force at the moment it
serves each relayed completion and stores each spooled trace batch. Saving a policy while such a job is running
therefore governs the rest of that job, and not only the jobs started after the save.

Each stored batch keeps the decision that applied when it was stored. Serving that batch back to a reader
applies no second check, and a policy saved afterwards redacts nothing that is already written. A trace
written earlier keeps what it holds; ProPR deletes no protocol rows.

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
