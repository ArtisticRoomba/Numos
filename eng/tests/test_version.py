from pathlib import Path
import importlib.util
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "version.py"
SPEC = importlib.util.spec_from_file_location("numos_version", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
version_tool = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = version_tool
SPEC.loader.exec_module(version_tool)


class SemanticVersionTests(unittest.TestCase):
    def test_accepts_semver_2_versions(self) -> None:
        values = (
            "0.1.0",
            "1.2.3-alpha",
            "1.2.3-alpha.1",
            "1.2.3-0.3.7",
            "1.2.3+build.007",
            "1.2.3-rc.1+sha.abcdef",
        )
        for value in values:
            with self.subTest(value=value):
                self.assertEqual(value, str(version_tool.SemanticVersion.parse(value)))

    def test_rejects_invalid_semver(self) -> None:
        values = (
            "1",
            "1.2",
            "01.2.3",
            "1.02.3",
            "1.2.03",
            "1.2.3-alpha.01",
            "1.2.3-",
            "1.2.3+",
            "1.2.3_alpha",
        )
        for value in values:
            with self.subTest(value=value):
                with self.assertRaises(version_tool.VersionError):
                    version_tool.SemanticVersion.parse(value)

    def test_bump_resets_lower_components_and_metadata(self) -> None:
        current = version_tool.SemanticVersion.parse("1.2.3-beta.2+build.9")
        self.assertEqual("2.0.0", str(current.bump("major")))
        self.assertEqual("1.3.0-rc.1", str(current.bump("minor", "rc.1")))
        self.assertEqual("1.2.4", str(current.bump("patch")))

    def test_promote_removes_prerelease_and_build_metadata(self) -> None:
        current = version_tool.SemanticVersion.parse("1.2.3-rc.1+build.9")
        self.assertEqual("1.2.3", str(current.promote()))


class VersionFileTests(unittest.TestCase):
    def test_write_updates_dotnet_versions_and_preserves_layout(self) -> None:
        contents = """<Project>\n    <PropertyGroup>\n        <Version>0.1.0-alpha.1</Version>\n        <PackageVersion>$(Version)</PackageVersion>\n        <AssemblyVersion>0.0.0.0</AssemblyVersion>\n        <FileVersion>0.1.0.0</FileVersion>\n    </PropertyGroup>\n</Project>\n"""
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "Version.props"
            path.write_text(contents, encoding="utf-8")
            requested = version_tool.SemanticVersion.parse("2.4.6-rc.1+sha.123")
            version_tool.write_version(path, requested)
            updated = path.read_text(encoding="utf-8")

        self.assertIn("<Version>2.4.6-rc.1+sha.123</Version>", updated)
        self.assertIn("<PackageVersion>$(Version)</PackageVersion>", updated)
        self.assertIn("<AssemblyVersion>2.0.0.0</AssemblyVersion>", updated)
        self.assertIn("<FileVersion>2.4.6.0</FileVersion>", updated)
        self.assertEqual(contents.count("\n"), updated.count("\n"))


class GitReleaseTests(unittest.TestCase):
    VERSION_FILE = """<Project>\n    <PropertyGroup>\n        <Version>0.1.0-alpha.1</Version>\n        <PackageVersion>$(Version)</PackageVersion>\n        <AssemblyVersion>0.0.0.0</AssemblyVersion>\n        <FileVersion>0.1.0.0</FileVersion>\n    </PropertyGroup>\n</Project>\n"""

    def create_repository(self, directory: str) -> Path:
        root = Path(directory)
        for relative_path in version_tool.COMPONENT_FILES.values():
            path = root / relative_path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(self.VERSION_FILE, encoding="utf-8")
        subprocess.run(["git", "init", "--initial-branch=master"], cwd=root, check=True, capture_output=True)
        subprocess.run(["git", "config", "user.name", "Numos Tests"], cwd=root, check=True)
        subprocess.run(["git", "config", "user.email", "tests@example.invalid"], cwd=root, check=True)
        subprocess.run(["git", "add", "."], cwd=root, check=True)
        subprocess.run(["git", "commit", "--message", "Initial versions"], cwd=root, check=True, capture_output=True)
        return root

    def test_tag_creates_annotated_component_tag(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_repository(directory)
            tag = version_tool.create_tag(root, "coresim")
            tag_type = version_tool.run_git(root, "cat-file", "-t", tag).stdout.strip()

        self.assertEqual("coresim/v0.1.0-alpha.1", tag)
        self.assertEqual("tag", tag_type)

    def test_verify_rejects_dirty_repository(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = self.create_repository(directory)
            (root / "uncommitted.txt").write_text("dirty", encoding="utf-8")
            with self.assertRaisesRegex(version_tool.VersionError, "clean Git working tree"):
                version_tool.verify(root, "viewer")


if __name__ == "__main__":
    unittest.main()
