# Data Model: Resolve PR Comments

## New Entities & Value Objects

### `ReviewPrScan` (Domain Entity)
Tracks the last processed commit for a pull request by the automated reviewer, ensuring we do not process the same commits repeatedly.

- `Id`: `Guid`
- `ClientId`: `Guid` (FK)
- `RepositoryId`: `string`
- `PullRequestId`: `int`
- `LastProcessedCommitId`: `string` (The git commit SHA)
- `UpdatedAt`: `DateTimeOffset`

### `ReviewPrScanThread` (Domain Entity)
Tracks the per-thread state for reviewer-authored threads to determine if new replies have been added since the last check.

- `Id`: `Guid`
- `ReviewPrScanId`: `Guid` (FK)
- `ThreadId`: `int`
- `LastSeenReplyCount`: `int`
- `UpdatedAt`: `DateTimeOffset`
### `CommentResolutionBehavior` (Domain Enum)
Specifies how the system handles comment resolution.

- `Disabled` (0): The system does not attempt to automatically resolve comments.
- `Silent` (1): (Default) The system changes the thread status to "fixed" without posting a reply.
- `WithReply` (2): The system posts a reply explaining the resolution before changing the thread status.

## Modifications to Existing Entities

### `ClientRecord` (EF Core Entity)
Add a column to store the new behavior configuration.

- `CommentResolutionBehavior`: `int` (Mapped to the enum, defaults to `1` / `Silent`).

### `MeisterProPRDbContext`
- Add `DbSet<ReviewPrScan> ReviewPrScans`.
- Add `DbSet<ReviewPrScanThread> ReviewPrScanThreads`.

### DTOs
- `ClientDto`: Add `CommentResolutionBehavior CommentResolutionBehavior` property.
- `CreateClientRequest` / `UpdateClientRequest`: Add optional `CommentResolutionBehavior? CommentResolutionBehavior` to allow setting the value from the Admin API.
