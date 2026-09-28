# Restrict where your code goes

Bound the set of hosts your code can be sent to, and be able to demonstrate that it is bounded.

Start from the default - [where your code goes](../reference/security.md#where-your-code-goes). Everything
below narrows it further, in the order worth doing it.

## 1. Restrict the endpoint hosts

Under **Tenant → Compliance** a tenant states which AI provider families and which endpoint hosts its
clients may reach - see [compliance](../ai/compliance.md).

Set the host list, not only the family list. Add the family list as well only if you also want to keep a
family out of the tenant entirely.

Do this first: it is what bounds every step below. The step is Commercial only - see
[editions](../reference/editions.md).

## 2. Read the connections you already have

A policy says what is permitted; the connection list says what is configured. Read the base URL of every
active AI connection.

Read the **AI provider add-ins** page as well. A family reaches hosts of its own beyond the base URL an
operator entered, and the host list has to cover those too - see [compliance](../ai/compliance.md).

On an AWS Bedrock connection, read its `region` default query parameter too, not only the host - the
parameter is what decides where inference runs. See [AWS Bedrock](../ai/credentials.md#aws-bedrock).

## 3. Leave the private-egress default alone unless you host the model or the source-control server

Outbound AI and source-control traffic refuses private addresses until you turn on the private-egress opt-in, which relaxes
less than its name suggests - see
[outbound request protection](../reference/security.md#outbound-request-protection). One opt-in,
`MEISTER_ALLOW_PRIVATE_EGRESS`, covers AI endpoints and source-control hosts, so turning it on for a
self-hosted model also admits a source-control connection on a private address.

If you do turn it on, set the host list from step 1 first: it bounds the AI endpoints a client reaches. It
does not bound source-control. A `hostBaseUrl` is held to the address and scheme rules alone, so a private
source-control host outside the tenant's permitted endpoints is still reachable. Two controls bound the
source-control destinations. Leave `MEISTER_ALLOW_PRIVATE_EGRESS` unset on an installation whose
source-control servers are all public, which keeps every private address refused. Where you do need it, allow
outbound traffic from the API, worker and runner hosts to your source-control servers and to your AI
endpoints in the network policy or firewall of the environment they run in, and refuse the rest. Write each
rule as one destination host name on the TCP port that host is published on, `443` for an `https` endpoint
and the published port for a self-hosted server. A rule written as an IP range covers every other service in
that range, and one written without a port covers every service on those hosts.

## 4. Decide what is kept, not only where it is sent

Two switches change what a copy of your code persists into: per-connection archiving of comment threads
and diffs, and the capture of model reasoning into the protocol. Decide both; do not leave them as found - see
[what ProPR stores](../reference/security.md#what-propr-stores).

Where one controller forbids storing reasoning and another does not, set that controller's tenant to
`disabled` under **Tenant → Compliance** and leave `AI_CAPTURE_REASONING_IN_PROTOCOL` at the value the other
tenants need. A tenant set to `disabled` keeps no reasoning in the traces of the reviews it starts next,
whether that switch is `true` or `false`. Reviews are the work this setting governs, because they are the
work that asks a model for its reasoning. A thread pass and a mention answer ask for none and record the
answer text and the token counts, so their traces hold no reasoning for the setting to withhold. A review
inside ProPR reads the policy once, as it starts, so a save reaches the jobs that start after it. A review on an enrolled runner is checked again at each relayed completion and each
ingested batch, so a save also governs the completions relayed and the batches stored after it for a job
already running. It changes nothing that is already stored: reasoning written to a trace before the save stays
there, so plan the change for the retention you need going forward. See
[model reasoning in the job trace](../ai/compliance.md#model-reasoning-in-the-job-trace).

## 5. Keep tenants apart, and keep your edge closed

An AI credential never crosses a tenant boundary - see
[tenant isolation of AI credentials](../reference/security.md#tenant-isolation-of-ai-credentials).

While you are here: block `/metrics` at your edge on any public deployment - see
[what to block at your edge](../reference/security.md#what-to-block-at-your-edge).

## Confirm it worked

1. Try to save a connection on a host the policy does not permit. It is refused, not saved, with a message
   naming the tenant's permitted endpoint list - see
   [common messages](../ai/credentials.md#common-messages).
2. Read the tenant audit log: the restriction and the person who set it are both evidence - see
   [auditing](../reference/security.md#auditing).
3. Run one review and read your own network egress for it. The only hosts ProPR should have reached are
   your SCM host, the model endpoints on the permitted list, and any collector you configured yourself.

If something is refused that you expected to work, [troubleshooting](../operate/troubleshooting.md) routes
the symptom.
