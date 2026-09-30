import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


HOOK = Path(__file__).resolve().parents[2] / ".githooks" / "pre-commit"


class PreCommitTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.git("init", "-q")

        hook = self.repo / "pre-commit"
        shutil.copyfile(HOOK, hook)
        hook.chmod(0o755)

        self.tools = self.repo / "tools"
        self.tools.mkdir()
        for name in ("python3", "dotnet", "npm"):
            self.stub(name, "")

        self.log = self.repo / "commands.log"
        self.env = os.environ.copy()
        self.env["PATH"] = f"{self.tools}:{self.env['PATH']}"
        self.env["HOOK_LOG"] = str(self.log)

    def stub(self, name, body):
        executable = self.tools / name
        executable.write_text(
            "#!/bin/sh\n"
            f'printf "{name} %s\\n" "$*" >> "$HOOK_LOG"\n'
            f"{body}\n"
        )
        executable.chmod(0o755)

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.repo, check=True, capture_output=True, text=True)

    def stage(self, *paths):
        for path in paths:
            file = self.repo / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text("content\n")
        self.git("add", *paths)

    def run_hook(self):
        return subprocess.run(
            ["sh", str(self.repo / "pre-commit")],
            cwd=self.repo,
            env=self.env,
            capture_output=True,
            text=True,
        )

    def commands(self):
        return self.log.read_text().splitlines() if self.log.exists() else []

    def test_backend_and_frontend_changes_run_every_check(self):
        self.stage("src/Feature.cs", "frontend/src/feature.ts")

        result = self.run_hook()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        commands = self.commands()
        self.assertIn("python3 scripts/format_changed.py --staged", commands)
        self.assertTrue(any(command.startswith("dotnet build ") for command in commands))
        self.assertTrue(any(command.startswith("dotnet test ") for command in commands))
        self.assertIn("npm run type-check", commands)
        self.assertIn("npm run test", commands)

    def test_documentation_change_runs_no_check(self):
        self.stage("docs/page.md")

        result = self.run_hook()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], self.commands())

    def test_frontend_change_runs_no_backend_check(self):
        self.stage("frontend/src/feature.ts")

        result = self.run_hook()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        commands = self.commands()
        self.assertFalse(any(command.startswith(("dotnet ", "python3 ")) for command in commands))
        self.assertIn("npm run test", commands)

    def test_settings_change_checks_formatting_without_backend_build(self):
        self.stage(".editorconfig")

        result = self.run_hook()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        commands = self.commands()
        self.assertIn("python3 scripts/format_changed.py --staged", commands)
        self.assertFalse(any(command.startswith("dotnet ") for command in commands))

    def test_build_failure_rejects_commit_without_running_tests(self):
        self.stage("src/Feature.cs")
        self.stub("dotnet", '[ "$1" = build ] && exit 1\nexit 0')

        result = self.run_hook()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("build errors detected", result.stdout)
        self.assertFalse(any(command.startswith("dotnet test ") for command in self.commands()))

    def test_backend_test_failure_rejects_commit(self):
        self.stage("src/Feature.cs")
        self.stub("dotnet", '[ "$1" = test ] && exit 1\nexit 0')

        result = self.run_hook()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("backend tests failed", result.stdout)

    def test_frontend_test_failure_rejects_commit(self):
        self.stage("frontend/src/feature.ts")
        self.stub("npm", '[ "$2" = test ] && exit 1\nexit 0')

        result = self.run_hook()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("frontend tests failed", result.stdout)

    def test_formatter_failure_rejects_commit(self):
        self.stage("src/Feature.cs")
        self.stub("python3", "exit 9")

        result = self.run_hook()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("FAILED -- format check", result.stdout)

    def test_checks_run_at_the_same_time(self):
        self.stage("src/Feature.cs", "frontend/src/feature.ts")
        # The backend check waits until the frontend check has started and the other way round, so both fail
        # when the hook runs them one after another.
        started = self.repo / "started"
        started.mkdir()
        self.stub("dotnet", self.wait_script(started, own="dotnet", other="npm"))
        self.stub("npm", self.wait_script(started, own="npm", other="dotnet"))

        result = self.run_hook()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    @staticmethod
    def wait_script(started, own, other):
        return (
            f'touch "{started}/{own}"\n'
            "attempt=0\n"
            f'while [ ! -f "{started}/{other}" ] && [ "$attempt" -lt 40 ]; do\n'
            "    sleep 0.05\n"
            "    attempt=$((attempt + 1))\n"
            "done\n"
            f'test -f "{started}/{other}"'
        )


if __name__ == "__main__":
    unittest.main()
