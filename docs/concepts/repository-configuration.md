# Configuring ProPR from your repository

Two settings live in the repository rather than in ProPR, in a `.meister-propr/` folder. Both are read
from the **target branch** of the pull request. Files on the source branch are never read, so a
contributor cannot change how their own pull request is reviewed.

Everything else about a review is set in ProPR — see [what you can tune](reviews.md#what-you-can-tune).

## `.meister-propr/exclude`

Glob patterns for files ProPR should not review — one pattern per line. Blank lines and lines starting
with `#` are ignored. Matching is case-insensitive and relative to the repository root.

```
# generated code
**/Migrations/*.Designer.cs
**/Migrations/*ModelSnapshot.cs
**/*.g.cs
```

| Situation | What ProPR uses |
|---|---|
| No `exclude` file, or it cannot be read | The built-in defaults: `**/Migrations/*.Designer.cs` and `**/Migrations/*ModelSnapshot.cs` |
| `exclude` file with at least one pattern | Exactly those patterns — the built-in defaults no longer apply |
| `exclude` file with only comments and blank lines | No exclusions at all |

Because a present file replaces the defaults entirely, repeat any default you still want. This works
on Azure DevOps, GitHub, GitLab and Forgejo.

## `.meister-propr/instructions-*.md`

Repository-specific guidance handed to the reviewer — house conventions, what to be strict about, what
to leave alone. Any file in the folder whose name starts with `instructions-` is picked up; the folder
is read one level deep, not recursively.

Each file must open with a `"""` header block declaring `description:` and `when-to-use:`. A file
whose header is missing, unterminated, or missing either field is skipped without an error, so check
the header first if an instruction seems to have no effect.

```markdown
"""
description: How we write EF Core migrations
when-to-use: When the diff touches anything under Migrations/
"""

Migrations are append-only. Never edit a migration that has shipped …
```

Instruction files are read on Azure DevOps and GitLab. On GitHub and Forgejo they are ignored today;
exclusions still work everywhere.

If a file you expected to be skipped was reviewed anyway, or an instruction had no effect, start from
[troubleshooting](../operate/troubleshooting.md).
