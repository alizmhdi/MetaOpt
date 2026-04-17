#!/usr/bin/env python3
"""Convert path file keys from underscore format "0_1" to tuple format "(0,1)".

Usage:
  python3 parsers/convert_path_keys_to_tuple.py input.json output.json
  python3 parsers/convert_path_keys_to_tuple.py input.json --in-place
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any, Dict


PAIR_RE = re.compile(r"^([^_]+)_([^_]+)$")


def convert_keys(data: Any) -> Any:
    """Recursively convert all underscore pair keys to tuple format."""
    if isinstance(data, dict):
        result: Dict[str, Any] = {}
        for key, value in data.items():
            new_key = _convert_key(str(key))
            result[new_key] = convert_keys(value)
        return result
    if isinstance(data, list):
        return [convert_keys(item) for item in data]
    return data


def _convert_key(key: str) -> str:
    """Convert a single key from underscore format to tuple format."""
    m = PAIR_RE.match(key)
    if m:
        left = m.group(1).strip().strip("'\"")
        right = m.group(2).strip().strip("'\"")
        return f"({left},{right})"
    return key


def main() -> None:
    parser = argparse.ArgumentParser(description="Convert path file keys from underscore to tuple format")
    parser.add_argument("input_file", type=Path, help="Input JSON file")
    parser.add_argument("output_file", nargs="?", type=Path, help="Output JSON file (optional if --in-place)")
    parser.add_argument(
        "--in-place",
        action="store_true",
        help="Overwrite input file instead of writing to output file",
    )
    parser.add_argument(
        "--indent",
        type=int,
        default=2,
        help="JSON indentation level (default: 2)",
    )

    args = parser.parse_args()

    if not args.input_file.exists():
        raise FileNotFoundError(f"Input file not found: {args.input_file}")

    output_path = args.input_file if args.in_place else args.output_file
    if output_path is None:
        raise ValueError("Either specify output_file or use --in-place")

    with args.input_file.open("r", encoding="utf-8") as f:
        data = json.load(f)

    converted = convert_keys(data)

    with output_path.open("w", encoding="utf-8") as f:
        json.dump(converted, f, indent=args.indent)

    print(f"Converted keys and wrote to: {output_path}")


if __name__ == "__main__":
    main()
