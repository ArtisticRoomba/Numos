from pathlib import Path
import sys
import unittest


ENG = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENG))

import verify_packages  # noqa: E402


class AnalyzerTests(unittest.TestCase):
    def test_accepts_packed_generator(self) -> None:
        names = {"lib/net10.0/Numos.API.dll", "analyzers/dotnet/cs/Numos.API.Generators.dll"}
        verify_packages.check_analyzers("Numos.API.nupkg", "Numos.API", names)

    def test_rejects_missing_generator(self) -> None:
        with self.assertRaisesRegex(ValueError, "missing analyzer analyzers/dotnet/cs/Numos.API.Generators.dll"):
            verify_packages.check_analyzers("Numos.API.nupkg", "Numos.API", {"lib/net10.0/Numos.API.dll"})

    def test_rejects_generator_under_a_target_framework_folder(self) -> None:
        names = {"analyzers/dotnet/cs/net10.0/Numos.API.Generators.dll"}
        with self.assertRaises(ValueError):
            verify_packages.check_analyzers("Numos.API.nupkg", "Numos.API", names)

    def test_packages_without_analyzers_need_none(self) -> None:
        verify_packages.check_analyzers("Numos.CoreSim.nupkg", "Numos.CoreSim", set())


class DependencyAssetTests(unittest.TestCase):
    def check(self, dependencies: dict[str, dict[str, str]]) -> None:
        verify_packages.check_dependency_assets("Numos.Test.nupkg", dependencies)

    def test_accepts_analyzers_flowing_from_numos_api(self) -> None:
        for attributes in ({}, {"include": "All"}, {"exclude": "Build"}, {"include": "Compile,Analyzers"}):
            with self.subTest(attributes=attributes):
                self.check({"Numos.API": attributes})

    def test_rejects_numos_api_without_analyzers(self) -> None:
        for attributes in (
            {"exclude": "Build,Analyzers"},
            {"exclude": "all"},
            {"include": "Compile,Runtime"},
        ):
            with self.subTest(attributes=attributes), self.assertRaisesRegex(ValueError, "must not exclude analyzers"):
                self.check({"Numos.API": attributes})

    def test_rejects_excluded_analyzers_on_packages_that_depend_on_numos_api(self) -> None:
        with self.assertRaisesRegex(ValueError, "Numos.Serialization dependency"):
            self.check({"Numos.Serialization": {"exclude": "Build,Analyzers"}})

    def test_allows_default_excludes_on_packages_without_analyzers(self) -> None:
        self.check(
            {
                "Numos.CoreSim": {"exclude": "Build,Analyzers"},
                "Numos.SimDrawer": {"exclude": "Build,Analyzers"},
                "Raylib-cs": {"exclude": "Build,Analyzers"},
            }
        )

    def test_rejects_unpublished_numos_packages(self) -> None:
        for dependency_id in ("Numos.API.Generators", "Numos.Replay.Generators"):
            with self.subTest(dependency=dependency_id), self.assertRaisesRegex(ValueError, "unpublished"):
                self.check({dependency_id: {}})


if __name__ == "__main__":
    unittest.main()
