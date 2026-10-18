#!/usr/bin/env python3
"""Validate Numos NuGet package metadata and contents."""

from __future__ import annotations

import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
from zipfile import ZipFile

from package_manifest import (
    PACKAGE_ANALYZERS,
    PACKAGE_DEPENDENCIES,
    PACKAGE_FAMILIES,
    carries_analyzers,
    package_version,
    package_ids,
)

COPYRIGHT = (
    "Copyright © 2026 VeritableCalamity; Copyright © 2026 ArtisticRoomba; "
    "Copyright © 2026 Numos contributors"
)
REPOSITORY_URL = "https://github.com/ArtisticRoomba/Numos"


def find_package(directory: Path, package_id: str, version: str, suffix: str) -> Path:
    path = directory / f"{package_id}.{version}.{suffix}"
    if not path.is_file():
        raise ValueError(f"missing artifact {path.name}")
    return path


def check_analyzers(package_name: str, package_id: str, names: set[str]) -> None:
    for analyzer in PACKAGE_ANALYZERS.get(package_id, ()):
        path = f"analyzers/dotnet/cs/{analyzer}.dll"
        if path not in names:
            raise ValueError(f"{package_name}: missing analyzer {path}")


def flows_analyzers(dependency: dict[str, str]) -> bool:
    def assets(name: str) -> set[str] | None:
        value = dependency.get(name)
        if value is None:
            return None
        return {asset.strip().lower() for asset in value.split(",") if asset.strip()}

    include = assets("include")
    exclude = assets("exclude") or set()
    if include is not None and not include & {"all", "analyzers"}:
        return False
    return not exclude & {"all", "analyzers"}


def check_dependency_assets(package_name: str, dependencies: dict[str, dict[str, str]]) -> None:
    for dependency_id, dependency in dependencies.items():
        if dependency_id.startswith("Numos.") and dependency_id not in PACKAGE_DEPENDENCIES:
            raise ValueError(f"{package_name}: depends on unpublished package {dependency_id}")
        # NuGet excludes Build and Analyzers by default, which would stop the Numos.API generator at this package.
        if dependency_id in PACKAGE_DEPENDENCIES and carries_analyzers(dependency_id):
            if not flows_analyzers(dependency):
                raise ValueError(f"{package_name}: {dependency_id} dependency must not exclude analyzers")


def validate_package(root: Path, directory: Path, package_id: str) -> None:
    version = package_version(root, package_id)
    normalized_version = version.partition("+")[0]
    package = find_package(directory, package_id, normalized_version, "nupkg")
    symbols = find_package(directory, package_id, normalized_version, "snupkg")

    with ZipFile(package) as archive:
        names = set(archive.namelist())
        nuspec_name = next((name for name in names if name.endswith(".nuspec")), None)
        if nuspec_name is None:
            raise ValueError(f"{package.name}: missing nuspec")
        nuspec = ET.fromstring(archive.read(nuspec_name))

        namespace_uri = nuspec.tag.partition("}")[0].removeprefix("{")
        namespace = {"n": namespace_uri}
        metadata = nuspec.find("n:metadata", namespace)
        if metadata is None:
            raise ValueError(f"{package.name}: missing metadata")

        def required_text(name: str) -> str:
            element = metadata.find(f"n:{name}", namespace)
            if element is None or not element.text:
                raise ValueError(f"{package.name}: missing {name}")
            return element.text

        if required_text("id") != package_id:
            raise ValueError(f"{package.name}: package ID mismatch")
        if required_text("version") != version:
            raise ValueError(f"{package.name}: package version mismatch")
        if required_text("copyright") != COPYRIGHT:
            raise ValueError(f"{package.name}: copyright mismatch")
        if required_text("readme") != "README.md" or "README.md" not in names:
            raise ValueError(f"{package.name}: README is not embedded")
        if required_text("icon") != "icon.png" or "icon.png" not in names:
            raise ValueError(f"{package.name}: icon is not embedded")

        license_element = metadata.find("n:license", namespace)
        if license_element is None or license_element.text != "MIT":
            raise ValueError(f"{package.name}: expected MIT license expression")

        repository = metadata.find("n:repository", namespace)
        if repository is None:
            raise ValueError(f"{package.name}: missing repository metadata")
        if repository.get("url") != REPOSITORY_URL:
            raise ValueError(f"{package.name}: repository URL mismatch")
        if not repository.get("branch") or not repository.get("commit"):
            raise ValueError(f"{package.name}: repository branch/commit is missing")

        dependencies = {
            dependency.get("id", ""): dict(dependency.attrib)
            for dependency in metadata.findall("n:dependencies/n:group/n:dependency", namespace)
        }
        for dependency_id in PACKAGE_DEPENDENCIES[package_id]:
            if dependency_id not in dependencies:
                raise ValueError(f"{package.name}: missing dependency {dependency_id}")
            expected_dependency_version = package_version(root, dependency_id).partition("+")[0]
            if dependencies[dependency_id].get("version") != expected_dependency_version:
                raise ValueError(
                    f"{package.name}: {dependency_id} dependency must be {expected_dependency_version}"
                )
        check_dependency_assets(package.name, dependencies)
        check_analyzers(package.name, package_id, names)

        if package_id == "Numos.Viewer":
            for asset in (
                "contentFiles/any/any/assets/icon.png",
                "contentFiles/any/any/assets/imgui-default.ini",
            ):
                if asset not in names:
                    raise ValueError(f"{package.name}: missing runtime asset {asset}")

    with ZipFile(symbols) as archive:
        if not any(name.endswith(f"/{package_id}.pdb") for name in archive.namelist()):
            raise ValueError(f"{symbols.name}: missing portable PDB")

    print(f"verified {package.name}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package_directory", type=Path)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--family", choices=("all", *PACKAGE_FAMILIES), default="all")
    args = parser.parse_args()
    try:
        for package_id in package_ids(args.family):
            validate_package(args.repo.resolve(), args.package_directory.resolve(), package_id)
        return 0
    except (OSError, ValueError, ET.ParseError) as exception:
        print(f"error: {exception}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
