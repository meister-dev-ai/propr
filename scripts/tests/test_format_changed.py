import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "format_changed.py"


class FormatChangedTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name)
        self.source = self.repo / "src" / "Sample.cs"
        self.source.parent.mkdir()
        self.source.write_text("class Sample { }\n")
        self.git("init", "-q")
        self.git("config", "user.name", "Format Test")
        self.git("config", "user.email", "format@example.invalid")
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "core.hooksPath", "/dev/null")
        self.git("add", ".")
        self.git("commit", "-qm", "Initial source")

        bin_dir = self.repo / "tools"
        bin_dir.mkdir()
        jb = bin_dir / "jb"
        jb.write_text(
            "#!/usr/bin/env python3\n"
            "import json, os, pathlib, sys\n"
            "if '--version' in sys.argv:\n"
            "    print('JetBrains Cleanup Code 2026.2')\n"
            "    sys.exit(0)\n"
            "if os.environ.get('JB_ARGS_DIR'):\n"
            "    pathlib.Path(os.environ['JB_ARGS_DIR'], str(os.getpid())).write_text(json.dumps(sys.argv[1:]))\n"
            "else:\n"
            "    pathlib.Path(os.environ['JB_ARGS']).write_text(json.dumps(sys.argv[1:]))\n"
            "if os.environ.get('JB_FORMAT') == '1':\n"
            "    path = pathlib.Path(sys.argv[-1])\n"
            "    path.write_text(path.read_text().replace('class  Sample', 'class Sample'))\n"
        )
        jb.chmod(0o755)
        self.args_file = self.repo / "jb-args.json"
        self.env = os.environ.copy()
        self.env["PATH"] = f"{bin_dir}:{self.env['PATH']}"
        self.env["JB_ARGS"] = str(self.args_file)

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.repo, text=True)

    def run_formatter(self, *args, env=None):
        return subprocess.run(
            [sys.executable, str(SCRIPT), *args],
            cwd=self.repo,
            env=env or self.env,
            text=True,
            capture_output=True,
        )

    def test_staged_formatting_requires_restage(self):
        self.source.write_text("class  Sample { }\n")
        self.git("add", "src/Sample.cs")
        self.env["JB_FORMAT"] = "1"

        result = self.run_formatter("--staged")

        self.assertNotEqual(0, result.returncode)
        self.assertEqual("class Sample { }\n", self.source.read_text())
        self.assertEqual("class  Sample { }\n", self.git("show", ":src/Sample.cs"))
        args = json.loads(self.args_file.read_text())
        self.assertIn("src/Sample.cs", args)
        self.assertIn(
            "--disable-settings-layers=GlobalAll;GlobalPerProduct;SolutionPersonal;ProjectPersonal",
            args,
        )

        self.git("add", "src/Sample.cs")
        self.assertEqual(0, self.run_formatter("--staged").returncode)

    def test_unstaged_edits_are_not_formatted_or_staged(self):
        self.source.write_text("class  Sample { }\n")
        self.git("add", "src/Sample.cs")
        self.source.write_text("class   Sample { }\n")

        result = self.run_formatter("--staged")

        self.assertNotEqual(0, result.returncode)
        self.assertEqual("class   Sample { }\n", self.source.read_text())
        self.assertFalse(self.args_file.exists())

    def test_full_scan_checks_unchanged_source(self):
        self.source.write_text("class  Sample { }\n")
        self.git("add", "src/Sample.cs")
        self.git("commit", "-qm", "Add source with inconsistent spacing")
        self.env["JB_FORMAT"] = "1"

        result = self.run_formatter("--all")

        self.assertNotEqual(0, result.returncode)
        self.assertEqual("class Sample { }\n", self.source.read_text())
        self.assertIn("src/Sample.cs", json.loads(self.args_file.read_text()))

    def test_missing_formatter_fails_for_source_changes(self):
        self.source.write_text("class  Sample { }\n")
        self.git("add", "src/Sample.cs")
        env = self.env.copy()
        env["PATH"] = "/usr/bin:/bin"

        result = self.run_formatter("--staged", env=env)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("JetBrains", result.stderr)

    def test_staged_settings_change_scans_unchanged_source_files(self):
        settings = self.repo / ".editorconfig"
        settings.write_text("root = true\n")
        self.git("add", ".editorconfig")

        result = self.run_formatter("--staged")

        self.assertEqual(0, result.returncode)
        self.assertIn("src/Sample.cs", json.loads(self.args_file.read_text()))

    def test_project_and_solution_files_are_not_file_mode_targets(self):
        project = self.repo / "src" / "Sample.csproj"
        project.write_text("<Project />\n")
        self.source.write_text("class  Sample { }\n")
        self.git("add", "src/Sample.cs", "src/Sample.csproj")

        result = self.run_formatter("--staged")

        self.assertEqual(0, result.returncode)
        args = json.loads(self.args_file.read_text())
        self.assertIn("src/Sample.cs", args)
        self.assertNotIn("src/Sample.csproj", args)

    def test_generated_migrations_and_out_of_solution_scripts_are_excluded(self):
        migration = self.repo / "src" / "Migrations" / "Generated.cs"
        migration.parent.mkdir()
        migration.write_text("class  Generated { }\n")
        script = self.repo / "scripts" / "Utility.cs"
        script.parent.mkdir()
        script.write_text("class  Utility { }\n")
        self.source.write_text("class  Sample { }\n")
        self.git("add", "src/Sample.cs", "src/Migrations/Generated.cs", "scripts/Utility.cs")

        result = self.run_formatter("--staged")

        self.assertEqual(0, result.returncode)
        args = json.loads(self.args_file.read_text())
        self.assertIn("src/Sample.cs", args)
        self.assertNotIn("src/Migrations/Generated.cs", args)
        self.assertNotIn("scripts/Utility.cs", args)

    def test_unreferenced_root_targets_file_is_not_formatted(self):
        targets = self.repo / "Directory.Build.targets"
        targets.write_text("<Project />\n")
        self.git("add", "Directory.Build.targets", "src/Sample.cs")

        result = self.run_formatter("--staged")

        self.assertEqual(0, result.returncode)
        self.assertFalse(self.args_file.exists())

    def test_project_change_does_not_scan_unchanged_source_files(self):
        project = self.repo / "src" / "Sample.csproj"
        project.write_text("<Project />\n")
        self.git("add", "src/Sample.csproj")
        self.git("commit", "-qm", "Add project")
        project.write_text("<Project><PropertyGroup /></Project>\n")
        self.git("add", "src/Sample.csproj")

        result = self.run_formatter("--staged")

        self.assertEqual(0, result.returncode)
        self.assertFalse(self.args_file.exists())

    def test_settings_change_checks_every_source_file_in_parallel_batches(self):
        settings = self.repo / ".editorconfig"
        settings.write_text("root = true\n")
        for index in range(401):
            (self.repo / "src" / f"Sample{index}.cs").write_text(f"class Sample{index} {{ }}\n")
        self.git("add", "src", ".editorconfig")
        self.git("commit", "-qm", "Add source and formatting settings")
        settings.write_text("root = true\nindent_size = 4\n")
        self.git("add", ".editorconfig")
        args_dir = self.repo / "jb-invocations"
        args_dir.mkdir()
        env = self.env.copy()
        env["JB_ARGS_DIR"] = str(args_dir)
        result = self.run_formatter("--staged", env=env)

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        invocations = [json.loads(path.read_text()) for path in args_dir.iterdir()]
        self.assertGreater(len(invocations), 1)
        formatted = [arg for invocation in invocations for arg in invocation if arg.endswith(".cs")]
        expected = {"src/Sample.cs", *(f"src/Sample{index}.cs" for index in range(401))}
        self.assertEqual(expected, set(formatted))
        self.assertEqual(len(expected), len(formatted))


if __name__ == "__main__":
    unittest.main()
