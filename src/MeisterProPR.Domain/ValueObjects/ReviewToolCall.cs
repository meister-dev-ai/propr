// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Records a single tool invocation made during an agentic review pass.
/// </summary>
/// <param name="ToolName">Name of the tool that was called.</param>
/// <param name="Arguments">Serialised arguments passed to the tool.</param>
/// <param name="Result">Serialised result returned by the tool.</param>
/// <param name="InvokedAt">UTC timestamp at which the tool was invoked.</param>
public sealed record ReviewToolCall(string ToolName, string Arguments, string Result, DateTimeOffset InvokedAt);
