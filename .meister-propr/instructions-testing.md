"""
description: Testing patterns — NSubstitute mocking, WebApplicationFactory integration tests, Testcontainers, and test project conventions.
when-to-use: When test files change — any file in tests/, files matching *.Tests.*, *.spec.ts, or *.test.ts.
"""

# Testing Patterns

## Test Frameworks

- **Unit tests**: xUnit with NSubstitute for mocking. `Substitute.For<IFoo>()` creates mocks. `Arg.Any<T>()`, `Arg.Is<T>(x => ...)` are argument matchers. `Received()`, `DidNotReceive()`, `DidNotReceiveWithAnyArgs()` are NSubstitute verification calls.
- **API integration tests**: `WebApplicationFactory<Program>` with a real in-memory EF Core database (`UseInMemoryDatabase`). Tests call actual HTTP endpoints via `factory.CreateClient()`.
	Integration tests MUST register the protection codec (or a test-friendly equivalent) and assert that secrets are never returned in API responses; where relevant, tests should assert database rows contain protected (non-plaintext) values.
- **Infrastructure integration tests**: Testcontainers (`Testcontainers.PostgreSql`) spins up a real PostgreSQL container. These tests verify EF Core behaviour against a real database engine.

## In-Memory Database Scope

`InMemoryDatabaseRoot` is shared across all requests within a `WebApplicationFactory` fixture instance. This means state can persist between requests within one test — this is intentional for multi-step test flows. State does NOT persist between test classes (each test class gets its own factory).

## NSubstitute Conventions

`DidNotReceiveWithAnyArgs()` followed by a method call with explicit `null!` arguments is a NSubstitute idiom for asserting a method was never called. The `null!` arguments are placeholders — NSubstitute ignores them when using `WithAnyArgs`. This is correct usage, not a bug.

`Returns(...)` and `ReturnsForAnyArgs(...)` set up return values. When both are set up for the same method, `Returns(...)` matches first on explicit argument equality; `ReturnsForAnyArgs(...)` is the fallback.

## `MakePullRequest` Helper Pattern

Many test files contain a local `MakePullRequest(...)` helper that constructs a `PullRequest` value object with placeholder values. It is common and correct to pass the same string (e.g., `repoId`) for both `RepositoryId` and `RepositoryName` — in tests these are equivalent placeholders. Do not flag this as a bug or duplicate argument.

## Controller Tests — Auth Bypass

`WebApplicationFactory` subclasses in this project replace the middleware that normally sets `HttpContext.Items["IsAdmin"]`, `HttpContext.Items["ClientKey"]`, etc. Tests bypass real auth by injecting these items directly. This is the intended pattern for integration testing behind auth middleware.

## `flushPromises()` in Vue Tests

In `admin-ui/tests/`, `flushPromises()` from `@vue/test-utils` resolves all pending promises. It does NOT advance `setInterval`/`setTimeout`. If a test is validating timer-based polling, it must also use `vi.useFakeTimers()` and `vi.advanceTimersByTimeAsync()`.
