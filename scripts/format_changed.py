#!/usr/bin/env python3
"""Reformat changed source files with the CI-pinned JetBrains formatter."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


FORMAT_EXTENSIONS = {".cs", ".props", ".targets", ".resx", ".xaml", ".xml", ".config"}
EXPECTED_VERSION = "2026.2"
FORMAT_BATCH_SIZE = 400
MAX_FORMATTERS = 4


def is_format_target(path: str) -> bool:
    if path == "Directory.Build.props":
        return True
    # Solution cleanup does not select the root targets file, generated migrations, or standalone scripts.
    return (
        path.startswith(("src/", "tests/"))
        and "/Migrations/" not in path
        and Path(path).suffix.lower() in FORMAT_EXTENSIONS
    )


def git_output(*args: str) -> bytes:
    return subprocess.check_output(["git", *args])


def tracked_source_paths() -> list[str]:
    tracked = git_output("ls-files", "--cached", "-z")
    return [path for raw in tracked.split(b"\0") if raw if is_format_target(path := os.fsdecode(raw))]


def staged_paths() -> tuple[list[str], bool]:
    output = git_output("diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z")
    paths = [os.fsdecode(path) for path in output.split(b"\0") if path]
    # A change to the formatting rules can change the expected layout of any source file, so it checks all of them.
    if any(Path(path).name == ".editorconfig" or path.endswith(".DotSettings") for path in paths):
        return tracked_source_paths(), True
    return [path for path in paths if is_format_target(path)], False


def has_worktree_changes(paths: list[str]) -> bool:
    return subprocess.run(["git", "diff", "--quiet", "--", *paths], check=False).returncode != 0


def format_paths(paths: list[str], full_scan: bool) -> int:
    command = [
        "jb",
        "cleanupcode",
        "--profile=Built-in: Reformat Code",
        "--disable-settings-layers=GlobalAll;GlobalPerProduct;SolutionPersonal;ProjectPersonal",
        "--no-updates",
    ]
    if not full_scan or len(paths) <= FORMAT_BATCH_SIZE:
        return subprocess.run([*command, *paths], check=False).returncode

    shard_count = min(MAX_FORMATTERS, (len(paths) + FORMAT_BATCH_SIZE - 1) // FORMAT_BATCH_SIZE)
    shards = [paths[index::shard_count] for index in range(shard_count)]
    print(f"format: checking all source files in {shard_count} parallel batches", flush=True)
    with tempfile.TemporaryDirectory(prefix="propr-format-") as cache_root:
        def run_shard(index: int) -> subprocess.CompletedProcess[str]:
            return subprocess.run(
                [*command, f"--caches-home={cache_root}/shard-{index}", *shards[index]],
                capture_output=True,
                text=True,
                check=False,
            )

        with ThreadPoolExecutor(max_workers=shard_count) as executor:
            results = list(executor.map(run_shard, range(shard_count)))
    for result in results:
        if result.returncode != 0:
            print(result.stdout, file=sys.stderr)
            print(result.stderr, file=sys.stderr)
            return result.returncode
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--staged", action="store_true", help="Format files staged for the next commit")
    source.add_argument("--all", action="store_true", help="Check all tracked source files")
    args = parser.parse_args()

    paths, full_scan = (tracked_source_paths(), True) if args.all else staged_paths()
    if not paths:
        print("format: no source files selected")
        return 0

    if has_worktree_changes(paths):
        print("format: changed source files have unstaged edits; stage or restore them first", file=sys.stderr)
        return 1

    if shutil.which("jb") is None:
        print(f"format: JetBrains cleanupcode {EXPECTED_VERSION} is required", file=sys.stderr)
        return 1

    version = subprocess.run(["jb", "cleanupcode", "--version"], capture_output=True, text=True, check=False)
    if version.returncode != 0 or not re.search(rf"JetBrains Cleanup Code {re.escape(EXPECTED_VERSION)}(?:\.0)?\b", version.stdout):
        print(f"format: JetBrains cleanupcode {EXPECTED_VERSION} is required", file=sys.stderr)
        return 1

    print(f"format: checking {len(paths)} source file(s)", flush=True)
    result = format_paths(paths, full_scan)
    if result != 0:
        print("format: JetBrains cleanupcode failed", file=sys.stderr)
        return result

    if has_worktree_changes(paths):
        action = "stage the formatted files and retry" if args.staged else "commit the formatted files"
        print(f"format: source style changed; {action}", file=sys.stderr)
        return 1

    print("format: source style passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
