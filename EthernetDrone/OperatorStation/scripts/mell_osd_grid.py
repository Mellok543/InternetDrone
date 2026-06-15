#!/usr/bin/env python3
import json
import time
from pathlib import Path

LAYOUT_FILE = Path("/home/mell/osd_layout.json")
VALUES_FILE = Path("/tmp/drone_osd_values.json")
OUTPUT_FILE = Path("/tmp/drone_osd.txt")


def load_json(path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return default


def put_text(grid, row, col, text):
    if row < 0 or row >= len(grid):
        return
    cols = len(grid[0])
    for i, ch in enumerate(text):
        x = col + i
        if 0 <= x < cols:
            grid[row][x] = ch


def render_osd():
    layout = load_json(LAYOUT_FILE, {"cols": 30, "rows": 16, "elements": []})
    values = load_json(VALUES_FILE, {})

    cols = int(layout.get("cols", 30))
    rows = int(layout.get("rows", 16))

    grid = [[" " for _ in range(cols)] for _ in range(rows)]

    for elem in layout.get("elements", []):
        if not elem.get("enabled", True):
            continue

        row = int(elem.get("row", 0))
        col = int(elem.get("col", 0))
        template = elem.get("template", "")

        try:
            text = template.format(**values)
        except Exception:
            text = template

        put_text(grid, row, col, text)

    return "\n".join("".join(row).rstrip() for row in grid)


def main():
    last = None
    while True:
        text = render_osd()
        if text != last:
            OUTPUT_FILE.write_text(text, encoding="utf-8")
            last = text
        time.sleep(0.2)


if __name__ == "__main__":
    main()
