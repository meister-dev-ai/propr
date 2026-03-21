# Feature Specification: Resolve PR Comments

**Feature Branch**: `011-resolve-pr-comments`  
**Created**: 21. März 2026  
**Status**: Approved

## Clarifications

### Session 2026-03-21
- Q: If the AI resolves a comment, and a user manually marks it as active (unresolved) again, should the AI re-evaluate it on the next commit? → A: Yes, treat it as an active unresolved comment and re-evaluate it on the next commit.
- Q: Should the system re-evaluate unresolved comments on every new commit pushed to the PR, or should it wait for a specific trigger? → A: On every check cycle where new commits are detected (continuous evaluation).
- Q: What should the system do if the AI is unsure whether the commit fully resolves the comment? → A: Leave the comment unresolved.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Automatic Resolution of Fixed Issues (Priority: P1)

As a developer submitting a pull request, I want the automated reviewer to automatically resolve its own comments when I push a fix, so that I don't have to manually resolve them and can easily see what issues remain.

**Why this priority**: Core functionality of the feature. Automates the resolution process to save developer time and provides immediate feedback that the fix was acceptable.

**Independent Test**: Can be fully tested by creating a pull request, letting the automated reviewer comment, pushing a fix for the comment, and verifying the system changes the comment status to resolved.

**Acceptance Scenarios**:

1. **Given** an open pull request with an active (unresolved) comment made by the automated reviewer
   **When** the author pushes a new commit that addresses the issue raised in the comment
   **And** the system processes the new commit
   **Then** the reviewer changes the status of its comment to a resolved (or closed) state.
2. **Given** an open pull request with an active comment made by the automated reviewer
   **When** the author pushes a new commit that does *not* address the issue raised
   **Then** the comment remains in an unresolved state.

---

### User Story 2 - Efficient Re-evaluation on New Commits Only (Priority: P2)

As a system administrator, I want the system to only re-evaluate unresolved comments when new, unseen commits are added to the pull request, so that processing resources are not wasted on unchanged code.

**Why this priority**: Essential for cost control and performance, preventing redundant processing on every check cycle.

**Independent Test**: Can be tested by observing the system's processing logs when checking a pull request with unresolved comments but no new commits (should be zero re-evaluation processing).

**Acceptance Scenarios**:

1. **Given** a pull request with unresolved comments
   **When** the system checks the pull request but no new commits have been pushed since the last check
   **Then** the system does not re-evaluate the unresolved comments.
2. **Given** a pull request with unresolved comments
   **When** the system checks the pull request and detects new commits since its last evaluation
   **Then** it only re-evaluates the currently unresolved comments against the changes in the new commits.

### Edge Cases

- What happens when a user manually resolves an automated comment before the system re-evaluates it? (Assumption: The system ignores already resolved comments).
  - > The agent takes no action on comments that are already resolved, regardless of whether they were resolved by the system or manually by a user.
- What happens when a user manually re-opens (unresolves) an automated comment that the AI previously resolved?
  - > The system will treat it as an active unresolved comment and re-evaluate it on the next commit.
- What happens when a user force-pushes, removing a commit that the system previously reviewed?
  - > The system will detect the new commit history and re-evaluate the unresolved comments against the new state of the code. If the issue is still present, the comment remains unresolved; if it is resolved, the comment status is updated accordingly.
- What happens if the author replies to the comment without pushing code?
  - > The system detects that new replies have been added to the thread since its last check and re-evaluates that thread. This allows it to respond to follow-up questions or discussion directed at the reviewer. The trigger is "new replies in the thread since last processed" — not "new commits in the PR."
- What happens if the AI is unsure whether the commit fully resolves the comment (e.g., partial fix)?
  - > The system will leave the comment unresolved, requiring human intervention.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST track the latest commit identifier that it has processed for each active pull request.
- **FR-002**: The system MUST identify all currently unresolved comment threads that were created by the automated reviewer on a given pull request, by comparing the thread author identity against the reviewer's own identity.
- **FR-003**: On every check cycle where new commits are detected (continuous evaluation), the system MUST re-evaluate only the unresolved automated comments against the new changes.
- **FR-004**: If the system determines that the new changes address the issue in an unresolved comment, the system MUST update the comment thread status to `fixed` in the source control system.
- **FR-005**: The system MUST NOT re-evaluate code changes if no new commits have been added since the last check. However, if any human reply has been added to a reviewer-authored thread since the last time that thread was processed, the system MUST re-evaluate that thread regardless of whether new commits exist, so that it can respond to follow-up questions or discussion.
- **FR-006**: The system MUST support configurable behavior on a per-client basis for comment resolution, defaulting to silently changing the thread status without adding a reply.

### Key Entities

- **Review State**: Needs to include the last processed commit identifier to detect changes.
- **Comment Thread**: Represents the discussion on the pull request. The system needs to check its status (active/resolved) and author.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of re-evaluations are skipped when a pull request has no new commits, reducing redundant processing requests to zero for unchanged pull requests.
- **SC-002**: When a fix is pushed, the system resolves its corresponding comment within the next processing cycle.
- **SC-003**: System identifies and processes only unresolved comments during the re-evaluation phase, ignoring already resolved threads.

## Assumptions
- The source control system allows updating comment thread statuses via API.
- The automated reviewer is capable of understanding a previous comment and a code diff to determine if the issue is fixed.
- Comments manually resolved by humans should be ignored by the system's re-evaluation process.
