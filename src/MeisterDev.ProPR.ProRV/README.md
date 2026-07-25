# ProRV

ProRV is the bounded review-knowledge library used by ProPR review execution.

It stores embedded derivative assets plus modular prompt and ranking components for:

- fast per-file check applicability screening
- focused refinement of selected checks
- focused review guidance generation for file-based review strategies

The generated asset set is derived from CodeQL knowledge, but the runtime approach, prompt design,
and integration model are ProRV-specific.

## Project Structure

- `Abstractions/`: public service contracts
- `Models/`: public request/result models
- `DependencyInjection/`: DI entrypoints
- `Core/`: orchestration and ranking logic
- `Knowledge/`: embedded asset catalog loading
- `Prompting/`: prompt construction helpers
- `Assets/`: generated derivative knowledge assets

## Attribution
Data for ProRV is derived from the CodeQL knowledge base, but the runtime approach is fully ProRV-specific.
