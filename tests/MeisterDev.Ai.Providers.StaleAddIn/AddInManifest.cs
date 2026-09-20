// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;

// The contract version is stated as a literal, because an attribute argument is metadata and cannot read an
// environment variable the way the driver's declaration does. This is the version an administrator would be
// shown, and the discovery pass refuses the add-in on it before anything is loaded.
[assembly: ProviderAddIn(
    Key = "example/stale",
    Label = "Stale example provider",
    Version = "1.0",
    ContractVersion = "0.9",
    ReachedHosts = ["api.stale.example.com"])]
