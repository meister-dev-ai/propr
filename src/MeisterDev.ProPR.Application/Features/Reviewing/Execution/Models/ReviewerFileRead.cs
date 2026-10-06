// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     A range of a file that a review pass read through its file-content tool and that returned source. The evidence
///     judge re-reads these ranges from the source branch, so it can weigh a finding against the code the reviewer saw.
/// </summary>
/// <param name="Path">Repository-relative path of the file, as the review tools accept it.</param>
/// <param name="StartLine">First line of the read, 1-based.</param>
/// <param name="EndLine">Last line of the read that returned content.</param>
public sealed record ReviewerFileRead(string Path, int StartLine, int EndLine);
