// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;

// What this family states about itself in a form a host can read without running it. The deployment image
// carries this add-in in its own directory, which needs no activation, so nothing here gates it. It is declared
// anyway: these families are the worked examples an add-in author copies, and one that leaves out what the
// contract asks for teaches the wrong thing. A test asserts it against the driver's own declaration, so the two
// cannot drift.
[assembly: ProviderAddIn(
    Key = "meisterdev/azureOpenAi",
    Label = "Azure OpenAI / AI Foundry",
    Version = "1.0",
    ContractVersion = ProviderContract.Version,
    ReachedHosts = [".openai.azure.com", ".services.ai.azure.com", ".cognitiveservices.azure.com"])]
