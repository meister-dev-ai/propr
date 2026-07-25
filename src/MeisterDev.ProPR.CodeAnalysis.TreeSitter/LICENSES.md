# Third-party native licence audit (FR-014)

This project consumes the native Tree-sitter core and per-language grammars through the pinned
`TreeSitter.DotNet` **1.3.0** NuGet package (research.md R1: **no fork**). Only the core library and the
six supported-language grammars are shipped in the worker image (the other ~22 grammars and all non-Linux
RIDs are pruned — see `build/TreeSitterPrune.targets` and the image-layer prune in the repo `Dockerfile`).

Product distribution licence: **Elastic License 2.0**. Every component below is **MIT** or **Apache-2.0**,
both permissive and compatible with redistribution under the Elastic License 2.0.

## Binding

| Component | Version | Licence | Source |
|-----------|---------|---------|--------|
| `TreeSitter.DotNet` (managed binding) | 1.3.0 | MIT | <https://www.nuget.org/packages/TreeSitter.DotNet/1.3.0> |

## Tree-sitter core

| Component | Licence | Source |
|-----------|---------|--------|
| `tree-sitter` (runtime) | MIT | <https://github.com/tree-sitter/tree-sitter> |

## Kept grammars

All six grammars are upstream MIT, except `tree-sitter-java` (Apache-2.0). Both are compatible.

| Grammar | Languages | Licence | Upstream |
|---------|-----------|---------|----------|
| `tree-sitter-typescript` | TypeScript, TSX | MIT | <https://github.com/tree-sitter/tree-sitter-typescript> |
| `tree-sitter-javascript` | JavaScript | MIT | <https://github.com/tree-sitter/tree-sitter-javascript> |
| `tree-sitter-python` | Python | MIT | <https://github.com/tree-sitter/tree-sitter-python> |
| `tree-sitter-go` | Go | MIT | <https://github.com/tree-sitter/tree-sitter-go> |
| `tree-sitter-java` | Java | Apache-2.0 | <https://github.com/tree-sitter/tree-sitter-java> |
| `tree-sitter-ruby` | Ruby | MIT | <https://github.com/tree-sitter/tree-sitter-ruby> |

## Shipped native binary digests (`TreeSitter.DotNet` 1.3.0)

SHA-256 of each kept `.so`, recorded against the pinned package so a future package bump is auditable.

### `linux-x64`

| File | SHA-256 |
|------|---------|
| `libtree-sitter.so` | `5a943678c9e0dee06fe32eb362dc4acb6bfbe9f357d349d5f3ad3a155665f867` |
| `libtree-sitter-typescript.so` | `70ee2a64069ef2712090cb97f096e4ba0eec10e4f8208728a33e874ffd58b5ab` |
| `libtree-sitter-tsx.so` | `f5877b3eaed5fb3ea86f382ca9bf825a9e14f1916689a8b2048da573d6fc0905` |
| `libtree-sitter-javascript.so` | `8970df0ec00b358d8cf6f46ed52f5ef521a59baf9730fb3c46f5958bf6b6b8bf` |
| `libtree-sitter-python.so` | `0e2b0f38015f0afa1c81f20b73e83899c27c922ad7c2003e18efcae71046d933` |
| `libtree-sitter-go.so` | `cab4ce6f27a50595271fa8f5c1e1c82a077946d96dc78566383eb48ce1dfc1fb` |
| `libtree-sitter-java.so` | `e58fd0a537b52125ac8195592294132e02b3727903d314bdb3a37f4c1cbb0e97` |
| `libtree-sitter-ruby.so` | `9a6f40e1501f3529ddf40775df51a8dfdfa862f6d470ae93c67097c4f3b98c5a` |

### `linux-arm64`

| File | SHA-256 |
|------|---------|
| `libtree-sitter.so` | `b6735df2eac6a2764d1cde40656b46afc0c6ce61f100b31c40031cc7158e0d8d` |
| `libtree-sitter-typescript.so` | `9b62c7b53d5a9fb023568e20475a494e08821c9abe630fb33db27dc42acd5860` |
| `libtree-sitter-tsx.so` | `d1645ca1445dc575ec651e7292f8853573bfc8e394b33185d67fe1707a1f5f27` |
| `libtree-sitter-javascript.so` | `d1b0585549b018add0b9ee2fc1b8a1c1344c60fac815a1f3a69dcf6c88eabe84` |
| `libtree-sitter-python.so` | `4cc331f46645b982fa8f3fa83368dd75fcfb1ad8e7659257a20c0cb33b844033` |
| `libtree-sitter-go.so` | `511bc3c7a07b0f97e54a1805bebffee91acc47ac88337eeb77d149c04fdbdccb` |
| `libtree-sitter-java.so` | `e765cfa53fbb9e849be38f905a476c1f58f42242d46ca567e81d2b5de9a439ef` |
| `libtree-sitter-ruby.so` | `4acc219c1781f1dd030000a6b322b308986bf5d12fa55f4e5a262adbc7bc6c44` |

> Regenerate after a package bump:
> `for g in "" -typescript -tsx -javascript -python -go -java -ruby; do sha256sum ~/.nuget/packages/treesitter.dotnet/<ver>/runtimes/<rid>/native/libtree-sitter$g.so; done`
