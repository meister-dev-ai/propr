// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


namespace MeisterDev.ProPR.Application.Features.Clients.Models;

/// <summary>Contains native local matching decisions for the existing operational connection query.</summary>
public sealed record ScmOperationalConnectionSelection(string? ExactHost, Func<string, bool> MatchesStoredHost, bool PreferLongestMatch);
