from pathlib import Path
import sys
import tempfile
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


class ViewerAssetTests(unittest.TestCase):
    CONTENT = "contentFiles/any/any"
    BASE = {
        f"{CONTENT}/assets/icon.png",
        f"{CONTENT}/assets/imgui-default.ini",
        f"{CONTENT}/assets/ProggyClean.ttf",
        f"{CONTENT}/assets/NumosFixedCyrillic-6x13.ttf",
        f"{CONTENT}/locale/en/common.ftl",
    }

    def check(self, names: set[str], cultures: dict[str, list[str]] | None = None) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for culture, files in (cultures or {}).items():
                (root / culture).mkdir()
                for file in files:
                    (root / culture / file).write_text("")
            verify_packages.check_viewer_assets("Numos.Viewer.nupkg", names, root)

    def test_accepts_complete_package(self) -> None:
        names = self.BASE | {f"{self.CONTENT}/locale/ru/common.ftl"}
        self.check(names, {"en": ["common.ftl"], "ru": ["common.ftl"]})

    def test_rejects_missing_font(self) -> None:
        for font in ("ProggyClean.ttf", "NumosFixedCyrillic-6x13.ttf"):
            with self.subTest(font=font), self.assertRaisesRegex(ValueError, font):
                self.check(self.BASE - {f"{self.CONTENT}/assets/{font}"})

    def test_rejects_missing_english_locale(self) -> None:
        with self.assertRaisesRegex(ValueError, "English locale"):
            self.check(self.BASE - {f"{self.CONTENT}/locale/en/common.ftl"})

    def test_rejects_missing_culture_present_in_source(self) -> None:
        with self.assertRaisesRegex(ValueError, "locale/ru/common.ftl"):
            self.check(self.BASE, {"en": ["common.ftl"], "ru": ["common.ftl"]})


if __name__ == "__main__":
    unittest.main()
