#!/usr/bin/env python3
"""Convert path file keys from tuple format "(0, 1)" to underscore format "0_1".

Usage:
  python3 parsers/convert_path_keys.py input.json output.json
  python3 parsers/convert_path_keys.py input.json output.json --in-place
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any, Dict


def convert_keys(data: Any) -> Any:
    """Recursively convert all tuple-style keys to underscore format."""
    if isinstance(data, dict):
        result: Dict[str, Any] = {}
        for key, value in data.items():
            # Convert tuple keys like "(0, 1)" to "0_1"
            new_key = _convert_key(str(key))
            result[new_key] = convert_keys(value)
        return result
    elif isinstance(data, list):
        return [convert_keys(item) for item in data]
    else:
        return data


def _convert_key(key: str) -> str:
    """Convert a single key from tuple format to underscore format."""
    # Match tuple pattern like "(0, 1)" or "( 0 , 1 )"
    match = re.match(r"^\(\s*([^,]+)\s*,\s*([^\)]+)\s*\)$", key)
    if match:
        left = match.group(1).strip().strip("'\"")
        right = match.group(2).strip().strip("'\"")
        return f"{left}_{right}"
    # If not a tuple, return as-is
    return key


def main() -> None:
    parser = argparse.ArgumentParser(description="Convert path file keys from tuple to underscore format")
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
        default=4,
        help="JSON indentation level (default: 4)",
    )

    args = parser.parse_args()

    if not args.input_file.exists():
        raise FileNotFoundError(f"Input file not found: {args.input_file}")

    # Load input
    with args.input_file.open("r", encoding="utf-8") as f:
        data = json.load(f)

    # Convert keys
    converted = convert_keys(data)

    # Determine output path
    output_path = args.input_file if args.in_place else args.output_file
    if output_path is None:
        raise ValueError("Either specify output_file or use --in-place")

    # Write output
    with output_path.open("w", encoding="utf-8") as f:
        json.dump(converted, f, indent=args.indent)

    print(f"Converted keys and wrote to: {output_path}")


if __name__ == "__main__":
    main()
