"""Inspect MOSAIC numeric CSV without third-party packages; never infer acquisition time."""
import argparse
import csv
import math
from pathlib import Path

def read(path, layout, channels, rows_per_matrix=None):
    records = []
    groups = []
    group = []
    for line_no, fields in enumerate(csv.reader(path.open(encoding="utf-8-sig", newline="")), 1):
        if not fields:
            continue
        prefix = 2 if layout in ("matrix", "labelled") else 1
        if len(fields) != channels + prefix:
            raise ValueError(f"Line {line_no}: expected {channels + prefix} columns, got {len(fields)}")
        timestamp = float(fields[0])
        values = [float(v) for v in fields[prefix:]]
        if not math.isfinite(timestamp) or not all(math.isfinite(v) for v in values):
            raise ValueError(f"Line {line_no}: non-finite number")
        label = fields[1] if layout == "labelled" else None
        records.append((timestamp, label, values))
        if layout == "matrix":
            index = int(fields[1])
            if index == 0:
                if group: groups.append(group)
                group = []
            if index != len(group):
                raise ValueError(f"Line {line_no}: expected matrix row {len(group)}, got {index}; incomplete/reordered data")
            if group and timestamp != group[0][0]:
                raise ValueError(f"Line {line_no}: timestamp changed within matrix")
            group.append((timestamp, values))
    if group: groups.append(group)
    if rows_per_matrix is not None:
        for i, matrix in enumerate(groups):
            if len(matrix) != rows_per_matrix:
                raise ValueError(f"Matrix {i}: expected {rows_per_matrix} rows, got {len(matrix)}")
    return records, groups

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", type=Path)
    parser.add_argument("--layout", choices=["vector","matrix","labelled"], required=True)
    parser.add_argument("--channels", type=int, required=True)
    parser.add_argument("--rows-per-matrix", type=int)
    args = parser.parse_args()
    if args.channels < 1: parser.error("--channels must be positive")
    if args.rows_per_matrix is not None and (args.layout != "matrix" or args.rows_per_matrix < 1):
        parser.error("--rows-per-matrix requires matrix layout and a positive count")
    try: records, groups = read(args.path,args.layout,args.channels,args.rows_per_matrix)
    except (ValueError,OSError) as exc: parser.exit(1, f"Cannot interpret file: {exc}\n")
    print(f"{len(records)} CSV rows; {args.channels} value columns")
    if groups: print(f"{len(groups)} matrices; row counts: {sorted(set(map(len,groups)))}")
    if records:
        print(f"Publication timestamp span: {records[-1][0]-records[0][0]:.6f} s")
        print(f"First values: {records[0][2]}")
    print("Counts do not prove completeness. Matrix timestamps are not per-sample acquisition times.")

if __name__ == "__main__":
    main()
