// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;

// What a platform administrator sees before any of this add-in runs. It has to agree with the driver's own
// declaration; activation compares the two and refuses them when they disagree.
[assembly: ProviderAddIn(
    Key = "example/provider",
    Label = "Example provider",
    Version = "1.0",
    ContractVersion = ProviderContract.Version,
    ReachedHosts = ["api.example.com", ".example.com"],
    RequiredCapability = "example-connections")]
