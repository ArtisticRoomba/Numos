from pathlib import Path
import sys
import unittest


ENG = Path(__file__).resolve().parents[1]
ROOT = ENG.parent
sys.path.insert(0, str(ENG))

import package_manifest  # noqa: E402


class PackageManifestTests(unittest.TestCase):
    def test_families_partition_all_published_packages(self) -> None:
        family_packages = tuple(
            package_id
            for family in package_manifest.PACKAGE_FAMILIES.values()
            for package_id in family
        )
        self.assertCountEqual(package_manifest.PACKAGE_DEPENDENCIES, family_packages)
        self.assertEqual(len(family_packages), len(set(family_packages)))

    def test_internal_dependencies_precede_dependents(self) -> None:
        for family, package_ids in package_manifest.PACKAGE_FAMILIES.items():
            positions = {package_id: index for index, package_id in enumerate(package_ids)}
            for package_id in package_ids:
                for dependency in package_manifest.PACKAGE_DEPENDENCIES[package_id]:
                    if dependency in positions:
                        with self.subTest(family=family, package=package_id, dependency=dependency):
                            self.assertLess(positions[dependency], positions[package_id])

    def test_projects_and_version_files_exist(self) -> None:
        for package_id in package_manifest.PACKAGE_DEPENDENCIES:
            with self.subTest(package=package_id):
                self.assertTrue((ROOT / package_manifest.project_file(package_id)).is_file())
                self.assertTrue(
                    (ROOT / package_manifest.PACKAGE_VERSION_FILES[package_id]).is_file()
                )


if __name__ == "__main__":
    unittest.main()
