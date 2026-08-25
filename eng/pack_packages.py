#!/usr/bin/env python3
"""Build a Numos package family with consistent release provenance."""

from __future__ import annotations

import argparse
from pathlib import Path
import subprocess
import sys

from package_manifest import PACKAGE_FAMILIES, package_file_name, package_ids, project_file


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("family", choices=("all", *PACKAGE_FAMILIES))
    parser.add_argument("output", type=Path)
    parser.add_argument("--repository-commit", required=True)
    parser.add_argument("--repository-branch", required=True)
    parser.add_argument(
        "--publish-manifest",
        type=Path,
        help="write primary package file names in dependency-safe publication order",
    )
    args = parser.parse_args()

    root = Path(__file__).resolve().parent.parent
    output = args.output.resolve()
    try:
        for package_id in package_ids(args.family):
            subprocess.run(
                [
                    "dotnet",
                    "pack",
                    str(root / project_file(package_id)),
                    "-c",
                    "Release",
                    "-o",
                    str(output),
                    "-warnaserror",
                    "-p:ContinuousIntegrationBuild=true",
                    f"-p:SourceRevisionId={args.repository_commit}",
                    f"-p:RepositoryCommit={args.repository_commit}",
                    f"-p:SourceBranchName={args.repository_branch}",
                    f"-p:RepositoryBranch={args.repository_branch}",
                ],
                cwd=root,
                check=True,
            )
        if args.publish_manifest:
            manifest = args.publish_manifest.resolve()
            manifest.parent.mkdir(parents=True, exist_ok=True)
            files = [package_file_name(root, package_id) for package_id in package_ids(args.family)]
            manifest.write_text("".join(f"{file_name}\n" for file_name in files), encoding="utf-8")
        return 0
    except (OSError, subprocess.CalledProcessError) as exception:
        print(f"error: {exception}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
