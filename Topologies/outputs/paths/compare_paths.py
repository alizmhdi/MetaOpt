import argparse
import json
from pathlib import Path
from typing import Dict, List, Tuple


PathList = List[List[str]]
TierMap = Dict[str, PathList]
AllTiers = Dict[str, TierMap]


def load_paths(file_path: Path) -> AllTiers:
    with file_path.open("r", encoding="utf-8") as f:
        data = json.load(f)

    # Normalize all keys/nodes to strings for stable comparison.
    normalized: AllTiers = {}
    for tier, tier_data in data.items():
        tier_key = str(tier)
        normalized[tier_key] = {}
        for pair, paths in tier_data.items():
            pair_key = str(pair)
            normalized_paths = [[str(node) for node in path] for path in paths]
            normalized[tier_key][pair_key] = normalized_paths
    return normalized


def summarize_differences(left: AllTiers, right: AllTiers) -> Tuple[int, List[str]]:
    diffs: List[str] = []

    left_tiers = set(left.keys())
    right_tiers = set(right.keys())

    missing_in_right = sorted(left_tiers - right_tiers)
    missing_in_left = sorted(right_tiers - left_tiers)

    if missing_in_right:
        diffs.append(f"Tiers only in left: {missing_in_right}")
    if missing_in_left:
        diffs.append(f"Tiers only in right: {missing_in_left}")

    common_tiers = sorted(left_tiers & right_tiers)

    for tier in common_tiers:
        left_pairs = left[tier]
        right_pairs = right[tier]

        left_pair_keys = set(left_pairs.keys())
        right_pair_keys = set(right_pairs.keys())

        pairs_missing_in_right = sorted(left_pair_keys - right_pair_keys)
        pairs_missing_in_left = sorted(right_pair_keys - left_pair_keys)

        if pairs_missing_in_right:
            diffs.append(
                f"Tier {tier}: pairs only in left ({len(pairs_missing_in_right)}): {pairs_missing_in_right[:10]}"
            )
        if pairs_missing_in_left:
            diffs.append(
                f"Tier {tier}: pairs only in right ({len(pairs_missing_in_left)}): {pairs_missing_in_left[:10]}"
            )

        for pair in sorted(left_pair_keys & right_pair_keys):
            left_paths = left_pairs[pair]
            right_paths = right_pairs[pair]

            left_set = {tuple(p) for p in left_paths}
            right_set = {tuple(p) for p in right_paths}

            if left_set != right_set:
                only_left = sorted(left_set - right_set)
                only_right = sorted(right_set - left_set)
                diffs.append(
                    f"Tier {tier}, pair {pair}: left_paths={len(left_paths)}, right_paths={len(right_paths)}, "
                    f"only_left={len(only_left)}, only_right={len(only_right)}"
                )
                if only_left:
                    diffs.append(f"  Example only in left: {list(only_left[0])}")
                if only_right:
                    diffs.append(f"  Example only in right: {list(only_right[0])}")

    return len(diffs), diffs


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Compare two topology path JSON files (tier -> src_dst -> list of paths)."
    )
    parser.add_argument(
        "left",
        nargs="?",
        default="Topologies/outputs/paths/b4-teavar_paths.json",
        help="Left JSON file (default: b4-teavar_paths.json)",
    )
    parser.add_argument(
        "right",
        nargs="?",
        default="Topologies/outputs/paths/b4_paths_2.json",
        help="Right JSON file (default: b4_paths_2.json)",
    )

    args = parser.parse_args()
    left_file = Path(args.left)
    right_file = Path(args.right)

    if not left_file.exists():
        print(f"Error: left file not found: {left_file}")
        return 2
    if not right_file.exists():
        print(f"Error: right file not found: {right_file}")
        return 2

    left = load_paths(left_file)
    right = load_paths(right_file)

    diff_count, diffs = summarize_differences(left, right)

    print(f"Compared:\n  left:  {left_file}\n  right: {right_file}")
    if diff_count == 0:
        print("Result: files are equivalent by tiers, pairs, and path content.")
        return 0

    print(f"Result: found {diff_count} difference entries.")
    for d in diffs:
        print(d)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
