// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Dtos;

/// <summary>
///     The payload the next ping would carry.
///     <para>
///         The payload is carried as serialized text rather than as an object. An object would be serialized a
///         second time by the API's own serializer, so the preview would show that re-serialization rather than
///         the bytes the sender produces.
///     </para>
/// </summary>
/// <param name="Endpoint">The address this payload would be posted to.</param>
/// <param name="ContentType">The media type it would be posted as.</param>
/// <param name="Payload">The request body as it would be sent.</param>
/// <param name="PayloadDocumentationUrl">Where the payload fields are documented.</param>
public sealed record UsageStatisticsPreviewDto(
    string Endpoint,
    string ContentType,
    string Payload,
    string PayloadDocumentationUrl);
