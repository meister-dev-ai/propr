# Research: MVP Backend — Local AI Code Review

**Branch**: `001-mvp-backend` | **Phase**: 0 — Research | **Date**: 2026-03-03

## Decision 1: AI SDK — Provider-Agnostic Architecture

### Decision

- **Infrastructure implementation** (`AgentAiReviewCore`) depends on
  **`Microsoft.Extensions.AI.IChatClient`** — the standard .NET AI abstraction.
- **Initial provider**: Azure OpenAI via **`Microsoft.Agents.AI.OpenAI`** (prerelease),
  which supplies the `AsAIAgent()` / `AsIChatClient()` extension on `AzureOpenAIClient`.
- **DI registration** (in `Program.cs`) wires the concrete `IChatClient` implementation.
  Future providers (Anthropic, Ollama, Azure AI Inference, etc.) swap only this
  registration — the Infrastructure implementation and all tests remain unchanged.

Source: [Microsoft Agent Framework — Your First Agent](https://learn.microsoft.com/en-us/agent-framework/get-started/your-first-agent?pivots=programming-language-csharp)

### Rationale

FR-005 mandates `Microsoft.Agents.AI`; the spec also requires the AI endpoint URL
to be supplied via env var, meaning provider flexibility is an architectural
requirement (not just a nice-to-have). `IChatClient` from `Microsoft.Extensions.AI`
is the standard .NET abstraction for chat-completion providers and has adapters for
Azure OpenAI, Azure AI Inference, OpenAI, Ollama, and others. Using it as the
internal interface in the Infrastructure layer satisfies FR-005 (Agent Framework
is used as the initial provider) while keeping the implementation non-prescriptive
about the underlying model service.

### Architecture

```
Domain:          IAiReviewCore         (PullRequest → ReviewResult; zero SDK refs)
Infrastructure:  AgentAiReviewCore : IAiReviewCore
                   depends on: IChatClient  (Microsoft.Extensions.AI)
                               ReviewPrompts (internal static class)
Api/Program.cs:  DI registration:
                   IChatClient ← AzureOpenAIClient
                                  .GetChatClient(deployment)
                                  .AsIChatClient()           (initial: Azure OpenAI)
                   // Future: swap this one line for Anthropic, Ollama, etc.
```

### Implementation Sketch

```csharp
// Infrastructure/AI/AgentAiReviewCore.cs
public sealed class AgentAiReviewCore(IChatClient chatClient) : IAiReviewCore
{
    public async Task<ReviewResult> ReviewAsync(
        PullRequest pr, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ReviewPrompts.SystemPrompt),
            new(ChatRole.User,   ReviewPrompts.BuildUserMessage(pr))
        };
        ChatCompletion response =
            await chatClient.CompleteAsync(messages, cancellationToken: ct);
        return ReviewResultParser.Parse(response.Message.Text ?? "");
    }
}

// Api/Program.cs (Azure OpenAI initial registration)
builder.Services.AddSingleton<IChatClient>(_ =>
    new AzureOpenAIClient(
        new Uri(config["AI_ENDPOINT"]!),
        new DefaultAzureCredential())
    .GetChatClient(config["AI_DEPLOYMENT"]!)
    .AsIChatClient());

builder.Services.AddTransient<IAiReviewCore, AgentAiReviewCore>();
```

### Environment Variables

| Variable        | Description                                     |
|-----------------|-------------------------------------------------|
| `AI_ENDPOINT`   | Azure OpenAI endpoint URL (required)            |
| `AI_DEPLOYMENT` | Model deployment name, e.g. `gpt-4o` (required) |

`DefaultAzureCredential` resolves auth transparently:

- **Local**: `AZURE_CLIENT_ID` + `AZURE_TENANT_ID` + `AZURE_CLIENT_SECRET` (service principal)
- **Production**: managed identity (no extra env vars needed)

### NuGet Packages

| Project        | Package                      | Version                      |
|----------------|------------------------------|------------------------------|
| Infrastructure | `Azure.AI.OpenAI`            | `2.2.0-beta.4` (prerelease)  |
| Infrastructure | `Microsoft.Agents.AI.OpenAI` | `0.2.0-preview` (prerelease) |
| Infrastructure | `Azure.Identity`             | `1.13.1`                     |
| Infrastructure | `Microsoft.Extensions.AI`    | `9.4.0`                      |

> **Action**: run `dotnet add package Microsoft.Agents.AI.OpenAI --prerelease` at
> implementation time to confirm and pin exact versions.

### Alternatives Considered

| Option                                                      | Why Rejected                                                                                                                  |
|-------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------|
| `AgentAiReviewCore` depends directly on `AzureOpenAIClient` | Ties Infrastructure to Azure OpenAI; violates the user's provider-agnostic requirement                                        |
| `Azure.AI.Agents.Persistent` (Foundry Persistent Agents)    | Requires pre-created agents, thread/run management, and agent IDs; overcomplicated for MVP; wrong package for Agent Framework |
| `Microsoft.SemanticKernel`                                  | Heavier dependency; not what FR-005 specifies                                                                                 |

---

## Decision 2: AI Review Prompt Design

### Decision

Single-turn structured-output prompt. The system prompt instructs the model to
respond with valid JSON only. The user message is constructed from PR metadata
and all changed files. `System.Text.Json` parses the response into `ReviewResult`.

### System Prompt (passed as `ChatRole.System` message)

```
You are an expert code reviewer specialising in .NET/C# and general
software engineering best practices. Review pull requests for bugs, security
vulnerabilities, code quality issues, performance problems, and maintainability.

Respond with valid JSON ONLY — no markdown fences, no preamble, no text
outside the JSON object. Schema:
{
  "summary": "<overall narrative>",
  "comments": [
    { "filePath": "<relative path or null>", "lineNumber": <int or null>,
      "severity": "info"|"warning"|"error"|"suggestion", "message": "<text>" }
  ]
}
```

### User Message Structure

```
Pull Request: {title}
{sourceBranch} → {targetBranch}
Description: {description}

Changed Files ({n}):

=== {file.Path} [{Add|Edit|Delete}] ===
--- FULL CONTENT ---
{file.FullContent}
--- DIFF ---
{file.UnifiedDiff}
```

For empty PRs: `"No files changed. Return summary stating no changes found; empty comments array."`

### Parsing

```csharp
record ReviewResultDto(string Summary, List<ReviewCommentDto> Comments);
record ReviewCommentDto(string? FilePath, int? LineNumber, string Severity, string Message);

ReviewResultDto dto = JsonSerializer.Deserialize<ReviewResultDto>(json,
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
```

---

## Decision 3: Azure DevOps .NET SDK

### Decision

**`Microsoft.TeamFoundationServer.Client` v19.245.1** and
**`Microsoft.VisualStudio.Services.Client` v19.245.1** — both target
`netstandard2.0`, fully compatible with .NET 10.

### Key API Calls Resolved

| Operation                               | Client           | Method                                                                |
|-----------------------------------------|------------------|-----------------------------------------------------------------------|
| Get PR iteration metadata (commit SHAs) | `GitHttpClient`  | `GetPullRequestIterationAsync`                                        |
| List changed files for iteration        | `GitHttpClient`  | `GetPullRequestIterationChangesAsync`                                 |
| Fetch file content by path + commit     | `GitHttpClient`  | `GetItemContentAsync` with `GitVersionDescriptor`                     |
| Fetch file content by blob SHA          | `GitHttpClient`  | `GetBlobContentAsync`                                                 |
| Post inline comment (file + line)       | `GitHttpClient`  | `CreateThreadAsync` with `ThreadContext.FilePath` + `RightFileStart`  |
| Post PR-level comment                   | `GitHttpClient`  | `CreateThreadAsync` with `ThreadContext = null`                       |
| Validate user ADO token (FR-015)        | Raw `HttpClient` | `GET app.vssps.visualstudio.com/_apis/connectionData?api-version=7.1` |

### `DefaultAzureCredential` → `VssConnection` Bridge

ADO resource App ID: `499b84ac-1321-427f-aa17-267ca6975798/.default`

```csharp
AccessToken token = await credential.GetTokenAsync(
    new TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]), ct);
var conn = new VssConnection(new Uri(orgUrl),
    new VssOAuthAccessTokenCredential(token.Token));
```

`VssConnectionFactory` (singleton) handles token refresh before expiry.

### ADO Token Validation (FR-015)

```
GET https://app.vssps.visualstudio.com/_apis/connectionData?api-version=7.1
Authorization: Bearer {X-Ado-Token}
```

Returns `200` (valid) or `401` (invalid/expired). Called via a named
`HttpClient` (`"AdoTokenValidator"`) — never via a `VssConnection`, ensuring
the user token is not available to the ADO SDK.

### NuGet Packages (Infrastructure)

```xml
<PackageReference Include="Microsoft.TeamFoundationServer.Client" Version="19.245.1" />
<PackageReference Include="Microsoft.VisualStudio.Services.Client" Version="19.245.1" />
```

---

## Decision 4: Unified Diff Generation — DiffPlex

### Decision

Generate unified diff text in-process using **`DiffPlex` v1.7.2**.
The ADO `GitHttpClient` does not return raw patch text for file diffs.

### Rationale

`GetCommitDiffsAsync` returns structured `GitChange` objects, not patch text.
`$format=patch` on the items endpoint gives a diff relative to the file's parent
commit (not the PR base) — incorrect for multi-commit PRs. `DiffPlex.InlineDiffBuilder`
generates `+`/`-` diff lines from two content strings.

**Edge cases**: deleted files → base content = file text, head content = `""`;
added files → base content = `""`, head content = file text. DiffPlex handles both.

### NuGet Packages (Infrastructure)

```xml
<PackageReference Include="DiffPlex" Version="1.7.2" />
```

---

## Decision 5: Background Worker — PeriodicTimer + Fire-and-Forget

### Decision

`BackgroundService` with `PeriodicTimer` (2-second interval). Each pending job
launches as an independent tracked task in `ConcurrentDictionary<Guid, Task>`.
Shutdown awaits `Task.WhenAll` to drain in-flight jobs.

### Key Points

- `PeriodicTimer.WaitForNextTickAsync` is cancellation-aware; no overlapping ticks.
- All exceptions in `ProcessJobSafeAsync` are caught: `OperationCanceledException`
  → revert to `Pending`; all other exceptions → `Failed` with message.
- `HostOptions.ShutdownTimeout = 3 minutes` to accommodate 120-second AI reviews.

---

## Decision 6: Idempotency Key (FR-012)

Composite: `(organizationUrl, projectId, repositoryId, pullRequestId, iterationId)`.
`IJobRepository.FindActiveJob(...)` checks for an existing non-`Failed` job before
creating a new one.

---

## Package Summary

### `MeisterProPR.Domain` — Zero external NuGet dependencies (by constitution)

_None_

### `MeisterProPR.Application` — No external SDK dependencies

_None_

### `MeisterProPR.Infrastructure`

```xml
<PackageReference Include="Microsoft.Extensions.AI"              Version="9.4.0" />
<PackageReference Include="Azure.AI.OpenAI"                      Version="2.2.0-beta.4" />
<PackageReference Include="Microsoft.Agents.AI.OpenAI"           Version="0.2.0-preview" />
<PackageReference Include="Azure.Identity"                       Version="1.13.1" />
<PackageReference Include="Microsoft.TeamFoundationServer.Client" Version="19.245.1" />
<PackageReference Include="Microsoft.VisualStudio.Services.Client" Version="19.245.1" />
<PackageReference Include="DiffPlex"                             Version="1.7.2" />
```

### `MeisterProPR.Api`

```xml
<PackageReference Include="Serilog.AspNetCore"                       Version="8.0.3" />
<PackageReference Include="Serilog.Sinks.Console"                    Version="6.0.0" />
<PackageReference Include="OpenTelemetry.Extensions.Hosting"         Version="1.10.0" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.10.0" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.10.0" />
<PackageReference Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" Version="1.10.0-rc.1" />
<PackageReference Include="Swashbuckle.AspNetCore"                   Version="7.3.1" />
```

> **Action**: verify and pin all prerelease versions at implementation time.
