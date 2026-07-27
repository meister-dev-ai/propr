# Restrict where your code goes

Bound the set of hosts your code can be sent to, and be able to demonstrate that it is bounded.

Start from the default — [where your code goes](../reference/security.md#where-your-code-goes). Everything
below narrows it further, in the order worth doing it.

## 1. Restrict the endpoint hosts

Under **Tenant → Compliance** a tenant states which AI provider families and which endpoint hosts its
clients may reach — see [compliance](../ai/compliance.md).

Set the host list, not only the family list. Add the family list as well only if you also want to keep a
family out of the tenant entirely.

Do this first: it is what bounds every step below. The step is Commercial only — see
[editions](../reference/editions.md).

## 2. Read the connections you already have

A policy says what is permitted; the connection list says what is configured. Read the base URL of every
active AI connection.

On an AWS Bedrock connection, read its `region` default query parameter too, not only the host — the
parameter is what decides where inference runs. See [AWS Bedrock](../ai/credentials.md#aws-bedrock).

## 3. Leave the private-egress default alone unless you host the model

Outbound AI traffic refuses private addresses until you turn on the private-egress opt-in, which relaxes
less than its name suggests — see
[outbound request protection](../reference/security.md#outbound-request-protection).

If you do turn it on, the host list from step 1 is what keeps the widened reach narrow. Set it first.

## 4. Decide what is kept, not only where it is sent

Two switches change what a copy of your code persists into: per-connection archiving of comment threads
and diffs, and the capture of model reasoning into the protocol. Decide both deliberately rather than
leaving them as found — see [what ProPR stores](../reference/security.md#what-propr-stores).

## 5. Keep tenants apart, and keep your edge closed

An AI credential never crosses a tenant boundary — see
[tenant isolation of AI credentials](../reference/security.md#tenant-isolation-of-ai-credentials).

While you are here: block `/metrics` at your edge on any public deployment — see
[what to block at your edge](../reference/security.md#what-to-block-at-your-edge).

## Confirm it worked

1. Try to save a connection on a host the policy does not permit. It is refused rather than saved, with a
   message naming the tenant's permitted endpoint list — see
   [common messages](../ai/credentials.md#common-messages).
2. Read the tenant audit log: the restriction and the person who set it are both evidence — see
   [auditing](../reference/security.md#auditing).
3. Run one review and read your own network egress for it. The only hosts ProPR should have reached are
   your SCM host, the model endpoints on the permitted list, and any collector you configured yourself.

If something is refused that you expected to work, [troubleshooting](../operate/troubleshooting.md) routes
the symptom.
