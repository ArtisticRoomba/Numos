#!/usr/bin/env python3
"""Manage Numos package-family versions and annotated release tags."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path
import re
import subprocess
import sys


COMPONENT_FILES = {
    "coresim": Path("src/Numos.CoreSim/Version.props"),
    "viewer": Path("src/Numos.Viewer/Version.props"),
}

PRERELEASE_IDENTIFIER = r"(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
BUILD_IDENTIFIER = r"[0-9A-Za-z-]+"
SEMVER_PATTERN = re.compile(
    rf"^(?P<major>0|[1-9][0-9]*)\."
    rf"(?P<minor>0|[1-9][0-9]*)\."
    rf"(?P<patch>0|[1-9][0-9]*)"
    rf"(?:-(?P<prerelease>{PRERELEASE_IDENTIFIER}(?:\.{PRERELEASE_IDENTIFIER})*))?"
    rf"(?:\+(?P<build>{BUILD_IDENTIFIER}(?:\.{BUILD_IDENTIFIER})*))?$"
)
XML_VALUES = {
    "Version": re.compile(r"(<Version>)(?P<value>[^<]*)(</Version>)"),
    "AssemblyVersion": re.compile(r"(<AssemblyVersion>)(?P<value>[^<]*)(</AssemblyVersion>)"),
    "FileVersion": re.compile(r"(<FileVersion>)(?P<value>[^<]*)(</FileVersion>)"),
}
MAX_ASSEMBLY_COMPONENT = 65534


class VersionError(RuntimeError):
    """A user-facing version-management failure."""


@dataclass(frozen=True)
class SemanticVersion:
    major: int
    minor: int
    patch: int
    prerelease: str | None = None
    build: str | None = None

    @classmethod
    def parse(cls, value: str) -> "SemanticVersion":
        match = SEMVER_PATTERN.fullmatch(value)
        if match is None:
            raise VersionError(f"'{value}' is not a valid SemVer 2.0.0 version")

        version = cls(
            int(match["major"]),
            int(match["minor"]),
            int(match["patch"]),
            match["prerelease"],
            match["build"],
        )
        for name, component in (
            ("major", version.major),
            ("minor", version.minor),
            ("patch", version.patch),
        ):
            if component > MAX_ASSEMBLY_COMPONENT:
                raise VersionError(
                    f"{name} component {component} exceeds the .NET assembly-version limit "
                    f"of {MAX_ASSEMBLY_COMPONENT}"
                )
        return version

    def bump(self, kind: str, prerelease: str | None = None) -> "SemanticVersion":
        if prerelease is not None:
            SemanticVersion.parse(f"0.0.0-{prerelease}")
        if kind == "major":
            return SemanticVersion(self.major + 1, 0, 0, prerelease)
        if kind == "minor":
            return SemanticVersion(self.major, self.minor + 1, 0, prerelease)
        if kind == "patch":
            return SemanticVersion(self.major, self.minor, self.patch + 1, prerelease)
        raise VersionError("bump kind must be major, minor, or patch")

    def promote(self) -> "SemanticVersion":
        return SemanticVersion(self.major, self.minor, self.patch)

    def __str__(self) -> str:
        value = f"{self.major}.{self.minor}.{self.patch}"
        if self.prerelease:
            value += f"-{self.prerelease}"
        if self.build:
            value += f"+{self.build}"
        return value


def repository_root(explicit_root: str | None = None) -> Path:
    root = Path(explicit_root).resolve() if explicit_root else Path(__file__).resolve().parent.parent
    if all((root / path).is_file() for path in COMPONENT_FILES.values()):
        return root
    raise VersionError(f"'{root}' is not a Numos repository containing both version files")


def component_file(root: Path, component: str) -> Path:
    try:
        return root / COMPONENT_FILES[component]
    except KeyError as exception:
        raise VersionError(f"unknown component '{component}'; expected coresim or viewer") from exception


def read_xml_value(text: str, name: str, path: Path) -> str:
    matches = list(XML_VALUES[name].finditer(text))
    if len(matches) != 1:
        raise VersionError(f"expected exactly one <{name}> element in '{path}'")
    return matches[0]["value"].strip()


def read_version(path: Path) -> SemanticVersion:
    return SemanticVersion.parse(read_xml_value(path.read_text(encoding="utf-8-sig"), "Version", path))


def replace_xml_value(text: str, name: str, value: str, path: Path) -> str:
    matches = list(XML_VALUES[name].finditer(text))
    if len(matches) != 1:
        raise VersionError(f"expected exactly one <{name}> element in '{path}'")
    match = matches[0]
    return text[: match.start("value")] + value + text[match.end("value") :]


def write_version(path: Path, version: SemanticVersion) -> None:
    raw = path.read_bytes()
    has_bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    text = replace_xml_value(text, "Version", str(version), path)
    text = replace_xml_value(text, "AssemblyVersion", f"{version.major}.0.0.0", path)
    text = replace_xml_value(
        text,
        "FileVersion",
        f"{version.major}.{version.minor}.{version.patch}.0",
        path,
    )
    encoding = "utf-8-sig" if has_bom else "utf-8"
    path.write_text(text, encoding=encoding, newline="")


def run_git(root: Path, *arguments: str, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", *arguments],
        cwd=root,
        check=check,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )


def verify(root: Path, component: str) -> SemanticVersion:
    path = component_file(root, component)
    version = read_version(path)
    text = path.read_text(encoding="utf-8-sig")
    expected_assembly = f"{version.major}.0.0.0"
    expected_file = f"{version.major}.{version.minor}.{version.patch}.0"
    if read_xml_value(text, "AssemblyVersion", path) != expected_assembly:
        raise VersionError(f"{path}: AssemblyVersion must be {expected_assembly}")
    if read_xml_value(text, "FileVersion", path) != expected_file:
        raise VersionError(f"{path}: FileVersion must be {expected_file}")

    status = run_git(root, "status", "--porcelain").stdout
    if status:
        raise VersionError("release verification requires a clean Git working tree and index")

    committed = run_git(root, "show", f"HEAD:{COMPONENT_FILES[component].as_posix()}").stdout
    if str(version) not in committed:
        raise VersionError(f"{component} version {version} is not committed at HEAD")
    return version


def create_tag(root: Path, component: str) -> str:
    version = verify(root, component)
    tag = f"{component}/v{version}"
    exists = run_git(root, "rev-parse", "--verify", "--quiet", f"refs/tags/{tag}", check=False)
    if exists.returncode == 0:
        raise VersionError(f"tag '{tag}' already exists")
    run_git(root, "tag", "--annotate", tag, "--message", f"Numos {component} {version}")
    return tag


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", help="Numos repository root (defaults to the script's parent)")
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("show", help="show both package-family versions")

    for command in ("get", "promote", "verify", "tag"):
        command_parser = commands.add_parser(command)
        command_parser.add_argument("component", choices=COMPONENT_FILES)

    set_parser = commands.add_parser("set")
    set_parser.add_argument("component", choices=COMPONENT_FILES)
    set_parser.add_argument("version")

    bump_parser = commands.add_parser("bump")
    bump_parser.add_argument("component", choices=COMPONENT_FILES)
    bump_parser.add_argument("kind", choices=("major", "minor", "patch"))
    bump_parser.add_argument("--prerelease")
    return parser


def main(arguments: list[str] | None = None) -> int:
    args = build_parser().parse_args(arguments)
    try:
        root = repository_root(args.repo)
        if args.command == "show":
            for component in COMPONENT_FILES:
                print(f"{component}: {read_version(component_file(root, component))}")
            return 0

        path = component_file(root, args.component)
        if args.command == "get":
            print(read_version(path))
        elif args.command == "set":
            version = SemanticVersion.parse(args.version)
            write_version(path, version)
            print(f"{args.component}: {version}")
        elif args.command == "bump":
            version = read_version(path).bump(args.kind, args.prerelease)
            write_version(path, version)
            print(f"{args.component}: {version}")
        elif args.command == "promote":
            version = read_version(path).promote()
            write_version(path, version)
            print(f"{args.component}: {version}")
        elif args.command == "verify":
            print(f"{args.component}: {verify(root, args.component)} verified")
        elif args.command == "tag":
            print(create_tag(root, args.component))
        return 0
    except (OSError, subprocess.SubprocessError, VersionError) as exception:
        print(f"error: {exception}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
