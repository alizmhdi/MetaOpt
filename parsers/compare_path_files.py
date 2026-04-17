#!/usr/bin/env python3
"""Compare two MetaOptimize path JSON files.

Supports files shaped as either:
1) {"2": {"src_dst": [[...], [...]], ...}}  (single nested level)
2) {"src_dst": [[...], [...]], ...}            (direct pair map)

Usage:
  python3 parsers/compare_path_files.py file_a.json file_b.json
  python3 parsers/compare_path_files.py file_a.json file_b.json --level-a 2 --level-b 3
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Dict, Iterable, List, Sequence, Tuple

PairMap = Dict[str, List[List[str]]]
PathTuple = Tuple[str, ...]


def _is_pair_map(obj: object) -> bool:
    if not isinstance(obj, dict):
        return False
    if not obj:
        return True

    for key, value in obj.items():
        if not isinstance(key, str):
            return False
        if "_" not in key and not re.match(r"^\(\s*[^,]+\s*,\s*[^\)]+\s*\)$", key):
            return False
        if not isinstance(value, list):
            return False
        for p in value:
            if not isinstance(p, list):
                return False
            if not all(isinstance(node, (str, int)) for node in p):
                return False
    return True


def _parse_pair_key(key: str) -> Tuple[str, str]:
    if "_" in key:
        left, right = key.split("_", 1)
        return left.strip(), right.strip()

    m = re.match(r"^\(\s*([^,]+)\s*,\s*([^\)]+)\s*\)$", key)
    if m:
        left = m.group(1).strip().strip("'\"")
        right = m.group(2).strip().strip("'\"")
        return left, right

    raise ValueError(f"Invalid pair key format: {key}")


def _normalize_pair_map(raw_map: dict) -> PairMap:
    normalized: PairMap = {}
    for raw_key, raw_paths in raw_map.items():
        src, dst = _parse_pair_key(str(raw_key))
        pair_key = f"{src}_{dst}"
        clean_paths: List[List[str]] = []
        for path in raw_paths:
            clean_paths.append([str(node) for node in path])
        normalized[pair_key] = clean_paths
    return normalized


def _load_json(path: Path) -> object:
    text = path.read_text(encoding="utf-8")
    if text.strip() == "":
        return {}
    return json.loads(text)


def _extract_pair_map(data: object, level: str | None, file_label: str) -> Tuple[PairMap, str]:
    if isinstance(data, dict) and not data:
        return {}, "<empty>"

    if _is_pair_map(data):
        return _normalize_pair_map(data), "<root>"

    if not isinstance(data, dict):
        raise ValueError(f"{file_label}: expected object at root")

    if level is not None:
        if level not in data:
            raise ValueError(
                f"{file_label}: level '{level}' not found. Available levels: {sorted(map(str, data.keys()))}"
            )
        level_obj = data[level]
        if not _is_pair_map(level_obj):
            raise ValueError(f"{file_label}: level '{level}' is not a pair map")
        return _normalize_pair_map(level_obj), level

    nested_levels = [(k, v) for k, v in data.items() if isinstance(v, dict) and _is_pair_map(v)]
    if len(nested_levels) == 1:
        k, v = nested_levels[0]
        return _normalize_pair_map(v), str(k)

    if len(nested_levels) > 1:
        keys = [str(k) for k, _ in nested_levels]
        raise ValueError(
            f"{file_label}: multiple levels found {keys}. Please pass --level-a/--level-b explicitly."
        )

    raise ValueError(f"{file_label}: could not detect a valid pair map")


def _normalize_path(path: Sequence[str]) -> PathTuple:
    return tuple(path)


def _path_to_str(path: Iterable[str]) -> str:
    return " -> ".join(path)


def compare_pair_maps(map_a: PairMap, map_b: PairMap, max_examples: int) -> str:
    pairs_a = set(map_a.keys())
    pairs_b = set(map_b.keys())

    only_a = sorted(pairs_a - pairs_b)
    only_b = sorted(pairs_b - pairs_a)
    common = sorted(pairs_a & pairs_b)

    identical_sets = []
    same_set_diff_order = []
    different_sets = []

    for pair in common:
        paths_a = map_a[pair]
        paths_b = map_b[pair]
        set_a = {_normalize_path(p) for p in paths_a}
        set_b = {_normalize_path(p) for p in paths_b}

        if set_a == set_b:
            if paths_a == paths_b:
                identical_sets.append(pair)
            else:
                same_set_diff_order.append(pair)
        else:
            different_sets.append(pair)

    total_paths_a = sum(len(v) for v in map_a.values())
    total_paths_b = sum(len(v) for v in map_b.values())

    lines: List[str] = []
    lines.append("=== Summary ===")
    lines.append(f"File A pairs: {len(pairs_a)}, total paths: {total_paths_a}")
    lines.append(f"File B pairs: {len(pairs_b)}, total paths: {total_paths_b}")
    lines.append(f"Common pairs: {len(common)}")
    lines.append(f"Pairs only in A: {len(only_a)}")
    lines.append(f"Pairs only in B: {len(only_b)}")
    lines.append(f"Common pairs with identical path list (same order): {len(identical_sets)}")
    lines.append(f"Common pairs with same path set but different order: {len(same_set_diff_order)}")
    lines.append(f"Common pairs with different path sets: {len(different_sets)}")

    def _add_examples(title: str, items: List[str]) -> None:
        lines.append("")
        lines.append(title)
        if not items:
            lines.append("  (none)")
            return
        for pair in items[:max_examples]:
            lines.append(f"  - {pair}")
        if len(items) > max_examples:
            lines.append(f"  ... ({len(items) - max_examples} more)")

    _add_examples("Pairs only in A:", only_a)
    _add_examples("Pairs only in B:", only_b)

    lines.append("")
    lines.append("Different path set examples:")
    if not different_sets:
        lines.append("  (none)")
    else:
        for pair in different_sets[:max_examples]:
            set_a = {_normalize_path(p) for p in map_a[pair]}
            set_b = {_normalize_path(p) for p in map_b[pair]}
            a_not_b = sorted(set_a - set_b)
            b_not_a = sorted(set_b - set_a)

            lines.append(f"  - Pair {pair}")
            if a_not_b:
                lines.append("    only in A:")
                for p in a_not_b[:max_examples]:
                    lines.append(f"      * {_path_to_str(p)}")
                if len(a_not_b) > max_examples:
                    lines.append(f"      ... ({len(a_not_b) - max_examples} more)")
            if b_not_a:
                lines.append("    only in B:")
                for p in b_not_a[:max_examples]:
                    lines.append(f"      * {_path_to_str(p)}")
                if len(b_not_a) > max_examples:
                    lines.append(f"      ... ({len(b_not_a) - max_examples} more)")
        if len(different_sets) > max_examples:
            lines.append(f"  ... ({len(different_sets) - max_examples} more pairs)")

    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description="Compare two MetaOptimize path JSON files")
    parser.add_argument("file_a", type=Path, help="First path JSON file")
    parser.add_argument("file_b", type=Path, help="Second path JSON file")
    parser.add_argument("--level-a", type=str, default=None, help="Top-level key to use in file A")
    parser.add_argument("--level-b", type=str, default=None, help="Top-level key to use in file B")
    parser.add_argument(
        "--max-examples",
        type=int,
        default=10,
        help="Maximum number of example entries to print per section",
    )

    args = parser.parse_args()

    data_a = _load_json(args.file_a)
    data_b = _load_json(args.file_b)

    pair_map_a, picked_level_a = _extract_pair_map(data_a, args.level_a, str(args.file_a))
    pair_map_b, picked_level_b = _extract_pair_map(data_b, args.level_b, str(args.file_b))

    print(f"Using level in A: {picked_level_a}")
    print(f"Using level in B: {picked_level_b}")
    print(compare_pair_maps(pair_map_a, pair_map_b, args.max_examples))


if __name__ == "__main__":
    main()
