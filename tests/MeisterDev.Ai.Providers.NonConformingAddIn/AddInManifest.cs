// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;

[assembly: ProviderAddIn(
    Key = "example/nonconforming",
    Label = "Non-conforming example provider",
    Version = "1.0",
    ContractVersion = ProviderContract.Version,
    ReachedHosts = ["api.nonconforming.example.com"])]
