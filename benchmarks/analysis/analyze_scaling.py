#!/usr/bin/env python3
"""Turn BenchmarkDotNet scaling CSV reports into complexity graphs."""

from __future__ import annotations

import argparse
import csv
import math
import re
from collections import defaultdict
from pathlib import Path

DIMENSIONS = (
    "RegisteredChunks", "AwakeChunks", "VoxelCount", "ActiveVoxelCount",
    "GasCount", "ReactionCount", "CondensingGasCount", "BoundaryEventCount",
    "ThermalBoundaryEdgeCount", "Workers", "Chunks", "Edge", "Gases",
    "Reactions", "CondensingGases", "Registered", "Awake", "Occupancy",
    "Supersaturation",
)

VARIANT_COLUMNS = (
    "WorkloadMode", "Activity", "ReactionState", "Topology", "BoundaryTopology",
    "PressureGradient", "TemperatureGradient", "Supersaturation",
    "SupersaturatedVoxelFraction", "ChunkCount", "ActiveVoxelsPerChunk",
    "Chunks", "Edge", "Gases", "Reactions", "CondensingGases", "Registered",
    "Awake", "Occupancy", "Job",
)

AXIS_ALIASES = {
    "RegisteredChunks": {"Registered", "Chunks"},
    "AwakeChunks": {"Awake", "Chunks"},
    "VoxelCount": {"Edge"},
    "ActiveVoxelCount": {"Occupancy", "ActiveVoxelsPerChunk"},
    "GasCount": {"Gases"},
    "ReactionCount": {"Reactions"},
    "CondensingGasCount": {"CondensingGases"},
    "Workers": {"Job"},
}


def number(value: str) -> float:
    match = re.search(r"[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?", value.replace(",", ""))
    if not match:
        raise ValueError(f"No number in {value!r}")
    return float(match.group())


def nanoseconds(value: str) -> float:
    amount = number(value)
    unit = value.strip().split()[-1] if " " in value.strip() else "ns"
    factors = {"ns": 1.0, "us": 1e3, "μs": 1e3, "ms": 1e6, "s": 1e9}
    return amount * factors.get(unit, 1.0)


def varying_dimension(rows: list[dict[str, str]]) -> str | None:
    for dimension in DIMENSIONS:
        values = {row.get(dimension, "") for row in rows} - {"", "-"}
        if len(values) > 1:
            return dimension
    return None


def time_scale(values: list[float]) -> tuple[float, str]:
    largest = max(values)
    if largest >= 1e9:
        return 1e9, "s"
    if largest >= 1e6:
        return 1e6, "ms"
    if largest >= 1e3:
        return 1e3, "μs"
    return 1.0, "ns"


def safe_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", value).strip("_")


def save_plot(figure, output: Path, stem: str, suffix: str, generated: list[Path]) -> None:
    path = output / f"{safe_name(stem)}.{suffix}.png"
    figure.tight_layout()
    figure.savefig(path, dpi=160)
    generated.append(path)


def plot_series(
        plt, output: Path, title: str, dimension: str,
        x: list[float], elapsed_ns: list[float], generated: list[Path],
) -> None:
    ordered = sorted(zip(x, elapsed_ns), key=lambda point: point[0])
    x, elapsed_ns = map(list, zip(*ordered))
    scale, unit = time_scale(elapsed_ns)
    elapsed = [value / scale for value in elapsed_ns]

    figure, axis = plt.subplots()
    axis.plot(x, elapsed, "o-", label="Dots: BenchmarkDotNet mean; line: visual guide")
    axis.set_xlabel(dimension)
    axis.set_ylabel(f"Mean time ({unit})")
    axis.set_title(title)
    axis.grid(alpha=0.25)
    axis.legend()
    save_plot(figure, output, title, "linear", generated)
    plt.close(figure)

    positive = [(a, b) for a, b in zip(x, elapsed) if a > 0 and b > 0]
    if len(positive) >= 2:
        log_x, log_y = map(list, zip(*positive))
        figure, axis = plt.subplots()
        axis.loglog(log_x, log_y, "o-", label="Dots: BenchmarkDotNet mean; line: visual guide")
        axis.set_xlabel(dimension)
        axis.set_ylabel(f"Mean time ({unit})")
        axis.set_title(f"{title} — log-log")
        axis.grid(which="both", alpha=0.25)
        axis.legend()
        save_plot(figure, output, title, "log-log", generated)
        plt.close(figure)

        if len(positive) >= 3:
            midpoints = [math.sqrt(log_x[index - 1] * log_x[index]) for index in range(1, len(log_x))]
            slopes = [
                math.log(log_y[index] / log_y[index - 1]) /
                math.log(log_x[index] / log_x[index - 1])
                for index in range(1, len(log_x))
                if log_x[index] != log_x[index - 1]
            ]
            if len(midpoints) == len(slopes):
                figure, axis = plt.subplots()
                axis.plot(midpoints, slopes, "o-", label="Slope between adjacent means")
                axis.axhline(1.0, color="grey", linestyle="--", linewidth=1, label="Linear reference: slope 1")
                axis.axhline(2.0, color="grey", linestyle=":", linewidth=1, label="Quadratic reference: slope 2")
                axis.set_xscale("log")
                axis.set_xlabel(dimension)
                axis.set_ylabel("Adjacent log-log slope")
                axis.set_title(f"{title} — local slope")
                axis.grid(alpha=0.25)
                axis.legend()
                save_plot(figure, output, title, "local-slope", generated)
                plt.close(figure)

    nonzero = [(a, b) for a, b in zip(x, elapsed_ns) if a != 0]
    if nonzero:
        normalized_x = [point[0] for point in nonzero]
        normalized = [point[1] / point[0] for point in nonzero]
        normalized_scale, normalized_unit = time_scale(normalized)
        figure, axis = plt.subplots()
        axis.plot(
            normalized_x,
            [value / normalized_scale for value in normalized],
            "o-",
            label=f"Mean time / {dimension}",
        )
        axis.set_xlabel(dimension)
        axis.set_ylabel(f"Mean time / {dimension} ({normalized_unit})")
        axis.set_title(f"{title} — normalized")
        axis.grid(alpha=0.25)
        axis.legend()
        save_plot(figure, output, title, "normalized", generated)
        plt.close(figure)

    if dimension == "Workers" and elapsed_ns:
        baseline = elapsed_ns[0]
        speedup = [baseline / value for value in elapsed_ns]
        figure, axis = plt.subplots()
        axis.plot(x, speedup, "o-", label="speedup")
        axis.plot(x, x, "--", color="grey", label="ideal")
        axis.set_xlabel("Workers")
        axis.set_ylabel("Speedup over smallest worker count")
        axis.set_title(f"{title} — parallel speedup")
        axis.legend()
        axis.grid(alpha=0.25)
        save_plot(figure, output, title, "speedup", generated)
        plt.close(figure)

        figure, axis = plt.subplots()
        axis.plot(x, [value / workers for value, workers in zip(speedup, x)], "o-")
        axis.set_xlabel("Workers")
        axis.set_ylabel("Parallel efficiency")
        axis.set_title(f"{title} — parallel efficiency")
        axis.grid(alpha=0.25)
        save_plot(figure, output, title, "parallel-efficiency", generated)
        plt.close(figure)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv", type=Path, nargs="+", help="BenchmarkDotNet CSV report(s)")
    parser.add_argument("--output", type=Path, default=Path("scaling-graphs"))
    args = parser.parse_args()

    try:
        import matplotlib.pyplot as plt
    except ImportError as error:
        parser.error(f"Matplotlib is required to generate graphs: {error}")

    args.output.mkdir(parents=True, exist_ok=True)
    by_benchmark: dict[str, list[dict[str, str]]] = defaultdict(list)
    for source in args.csv:
        with source.open(newline="", encoding="utf-8-sig") as stream:
            for row in csv.DictReader(stream):
                if "Workers" not in row and "Workers=" in row.get("Job", ""):
                    row["Workers"] = row["Job"].split("Workers=", 1)[1].split()[0].rstrip(",)")
                benchmark = row.get("Method") or row.get("Benchmark") or "Unknown"
                if row.get("Mean", "") not in ("", "NA"):
                    by_benchmark[benchmark].append(row)

    generated: list[Path] = []
    for benchmark, rows in sorted(by_benchmark.items()):
        dimension = varying_dimension(rows)
        if not dimension:
            continue
        split_columns = [
            column for column in VARIANT_COLUMNS
            if column != dimension and column not in AXIS_ALIASES.get(dimension, set())
        ]
        variants: dict[tuple[str, ...], list[dict[str, str]]] = defaultdict(list)
        for row in rows:
            variants[tuple(row.get(column, "") for column in split_columns)].append(row)

        for variant, series in variants.items():
            if len(series) < 2:
                continue
            labels = ", ".join(
                f"{column}={value}" for column, value in zip(split_columns, variant)
                if value not in ("", "-", "Default")
            )
            title = f"{benchmark} ({labels})" if labels else benchmark
            x = [number(row[dimension]) for row in series]
            elapsed = [nanoseconds(row["Mean"]) for row in series]
            plot_series(plt, args.output, title, dimension, x, elapsed, generated)

    if not generated:
        parser.error("No CSV series contained at least two values for a recognized scaling dimension")
    for path in generated:
        print(path)


if __name__ == "__main__":
    main()
