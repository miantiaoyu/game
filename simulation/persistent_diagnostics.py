"""Derive price-rounding and profitable-hand diagnostics from saved CSVs."""

from __future__ import annotations

import argparse
from collections import defaultdict
import csv
import json
from pathlib import Path
from statistics import mean

from .persistent_experiment import interval


def analyze(directory: Path) -> dict:
    prices = defaultdict(lambda: defaultdict(list))
    with (directory / "windows.csv").open() as stream:
        for row in csv.DictReader(stream):
            if row["scope"] == "paired_normal" and int(row["participants"]) >= 2:
                key = int(row["game"]), int(row["hand_index"])
                prices[row["profile"]][key].append(int(row["fee_quote"]))
    profits = defaultdict(lambda: defaultdict(list))
    with (directory / "paired-windows.csv").open() as stream:
        for row in csv.DictReader(stream):
            normal, reverse = json.loads(row["normal_net"]), json.loads(row["reverse_net"])
            first, last = int(row["first"]), int(row["last"])
            effect = ((int(normal[first] > 0) - int(reverse[first] > 0))
                      + (int(reverse[last] > 0) - int(normal[last] > 0))) / 2
            profits[row["profile"]][int(row["game"])].append(effect)
    economies = defaultdict(list)
    with (directory / "matches.csv").open() as stream:
        for row in csv.DictReader(stream):
            economies[row["profile"]].append(row)
    result = {}
    for profile, windows in prices.items():
        equal = sum(len(set(quotes)) == 1 for quotes in windows.values())
        matches = economies[profile]
        result[profile] = {
            "equal_quote_windows": equal,
            "comparable_windows": len(windows),
            "equal_quote_fraction": equal / len(windows),
            "profit_probability_order_effect": interval([mean(values) for values in profits[profile].values()]),
            "early_end_rate": mean(int(row["hands_played"]) < 12 for row in matches) if matches else None,
            "mean_survivors": mean(len(json.loads(row["final_stacks"])) - int(row["busted_players"]) for row in matches) if matches else None,
        }
    return {
        "method": "Exploratory diagnostics. Profit means strictly positive net chips in a hand, not showdown victory. Apply the same symmetric early/late contrast, average windows within match, and compute the interval over independent matches.",
        "profiles": result,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    args = parser.parse_args()
    result = analyze(args.input)
    (args.input / "pricing-diagnostics.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
