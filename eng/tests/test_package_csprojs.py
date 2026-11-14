import sys
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path, PureWindowsPath

ENG = Path(__file__).resolve().parents[1]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))

import package_manifest  # noqa: E402


def _load(package_id: str) -> ET.Element:
    return ET.parse(ROOT / package_manifest.project_file(package_id)).getroot()


def _imported_version_file(package_id: str) -> Path:
    project_dir = (ROOT / package_manifest.project_file(package_id)).parent
    imports = [
        PureWindowsPath(node.attrib["Project"]).as_posix()
        for node in _load(package_id).iter("Import")
        if node.attrib.get("Project", "").endswith("Version.props")
    ]
    assert len(imports) == 1, f"{package_id} must import exactly one Version.props"
    return (project_dir / imports[0]).resolve().relative_to(ROOT)


def _referenced_packages(package_id: str) -> set[str]:
    """Return referenced manifest packages. Analyzer references (ReferenceOutputAssembly=false) are skipped."""
    referenced = set()
    for node in _load(package_id).iter("ProjectReference"):
        if node.attrib.get("ReferenceOutputAssembly", "true").lower() == "false":
            continue
        name = PureWindowsPath(node.attrib["Include"]).stem
        if name in package_manifest.PACKAGE_DEPENDENCIES:
            referenced.add(name)
    return referenced


class PackageCsprojTests(unittest.TestCase):
    def test_family_matches_imported_version_props(self) -> None:
        for family, package_ids in package_manifest.PACKAGE_FAMILIES.items():
            for package_id in package_ids:
                with self.subTest(package=package_id):
                    self.assertEqual(
                        _imported_version_file(package_id),
                        package_manifest.PACKAGE_VERSION_FILES[package_id],
                    )
        coresim = {
            package_manifest.PACKAGE_VERSION_FILES[package_id]
            for package_id in package_manifest.PACKAGE_FAMILIES["coresim"]
        }
        viewer = {
            package_manifest.PACKAGE_VERSION_FILES[package_id]
            for package_id in package_manifest.PACKAGE_FAMILIES["viewer"]
        }
        self.assertEqual(len(coresim), 1)
        self.assertEqual(len(viewer), 1)
        self.assertNotEqual(coresim, viewer)

    def test_dependencies_match_project_references(self) -> None:
        for package_id, dependencies in package_manifest.PACKAGE_DEPENDENCIES.items():
            with self.subTest(package=package_id):
                self.assertEqual(set(dependencies), _referenced_packages(package_id))

    def test_analyzer_references_match_manifest(self) -> None:
        for package_id in package_manifest.PACKAGE_DEPENDENCIES:
            analyzers = {
                PureWindowsPath(node.attrib["Include"]).stem
                for node in _load(package_id).iter("ProjectReference")
                if node.attrib.get("ReferenceOutputAssembly", "true").lower() == "false"
            }
            expected = set(package_manifest.PACKAGE_ANALYZERS.get(package_id, ()))
            with self.subTest(package=package_id):
                self.assertTrue(expected <= analyzers)


if __name__ == "__main__":
    unittest.main()
