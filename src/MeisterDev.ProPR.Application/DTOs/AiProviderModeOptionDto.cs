// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     One protocol mode a provider family speaks, with what an operator sees where it is offered.
/// </summary>
/// <remarks>
///     The label travels with the value so a console can render a shape it has never heard of. It comes from the
///     family where the family states one and from the mode name otherwise, which is decided here rather than in
///     the console: a console holding its own table shows a key instead of a name for every family installed
///     after it shipped.
/// </remarks>
/// <param name="Value">The shape, as it is submitted and stored.</param>
/// <param name="Label">What an operator sees where the shape is offered.</param>
public sealed record AiProtocolModeOptionDto(string Value, string Label);

/// <summary>
///     One authentication mode a provider family authenticates with, with what an operator sees where it is offered.
/// </summary>
/// <remarks>The label is resolved the same way as on <see cref="AiProtocolModeOptionDto" />.</remarks>
/// <param name="Value">The shape, as it is submitted and stored.</param>
/// <param name="Label">What an operator sees where the shape is offered.</param>
/// <param name="IsSuperseded">
///     Whether the family still reads this shape but no longer offers it for a new connection. Every shape the
///     family declares is reported, flagged, rather than the superseded ones being dropped here: a console
///     showing a profile saved under one needs its name, and the server accepts it on save so that profile can
///     be edited without its credential being re-entered in another shape.
/// </param>
public sealed record AiAuthModeOptionDto(string Value, string Label, bool IsSuperseded);
