"""Validate pricing comparisons, fee accounting, and the common bankroll."""

import unittest
from unittest.mock import patch

from simulation import cost_ladder, multi_swap


class AlwaysSwapPolicy:
    def __init__(self, name, samples, swaps_enabled):
        self.name = name

    def choose_action(self, obs, rng):
        return "call" if obs.to_call else "check"

    def choose_swap(self, obs, rng):
        return 0


def pricing_records(game, windows):
    """Supply known per-player potential outcomes to audit paired arithmetic."""
    records = {
        variant: {"game": game, "seating": list(multi_swap.IDENTITIES),
                  "voyages": [], "players": [], "decisions": []}
        for variant in cost_ladder.VARIANTS
    }
    for voyage, (order, flat_first, flat_last, ladder_first, ladder_last, deltas) in enumerate(windows):
        for variant, record in records.items():
            ladder = variant.startswith("ladder")
            reverse = variant.endswith("reverse")
            record["voyages"].append({"voyage": voyage,
                                      "window_order": order[::-1] if reverse else order})
            for seat in range(5):
                points = game + seat * 7
                if ladder and seat in order:
                    points += deltas[order.index(seat)]
                if reverse and order:
                    if seat == order[0]:
                        points += ladder_first if ladder else flat_first
                    elif seat == order[-1]:
                        points += ladder_last if ladder else flat_last
                record["players"].append({"voyage": voyage, "seat": seat, "net_points": points})
                if seat in order:
                    rank = order.index(seat) + 1
                    record["decisions"].append({
                        "voyage": voyage, "seat": seat, "participants": len(order),
                        "rank": len(order) + 1 - rank if reverse else rank,
                        "net_points": points,
                    })
    return records


class CostLadderStatisticsTests(unittest.TestCase):
    def test_costs_distinguish_quoted_prices_from_payments_and_overwritten_fees(self):
        rows = [
            {"participants": 5, "rank": 1, "cost": 2, "swap_index": 0,
             "overwritten": 1, "pot_before": 5},
            {"participants": 5, "rank": 2, "cost": 3, "swap_index": None,
             "overwritten": 0, "pot_before": 7},
            {"participants": 5, "rank": 5, "cost": 6, "swap_index": 0,
             "overwritten": 0, "pot_before": 7},
        ]
        result = cost_ladder.cost_statistics([{"decisions": rows}])
        self.assertEqual(result["opportunities"], 3)
        self.assertEqual(result["swaps"], 2)
        self.assertEqual(result["total_paid"], 8)
        self.assertEqual(result["mean_paid_per_swap"], 4)
        self.assertAlmostEqual(result["mean_paid_per_opportunity"], 8 / 3)
        self.assertEqual(result["overwritten_cost"], 2)
        self.assertAlmostEqual(result["mean_pot_before"], 19 / 3)
        passed = next(row for row in result["strata"] if row["rank"] == 2)
        self.assertEqual(passed["total_paid"], 0)
        self.assertIsNone(passed["mean_paid_per_swap"])
        empty = cost_ladder.cost_statistics([])
        self.assertEqual(empty["total_paid"], 0)
        self.assertIsNone(empty["mean_paid_per_swap"])

    def test_flat_fourteen_chips_preserves_old_actions_and_net_results(self):
        for variant, old_variant in (("flat", "normal"), ("flat_reverse", "reversed")):
            with self.subTest(variant=variant):
                old = multi_swap.run_game((20261008, 7, 16, old_variant))
                new = cost_ladder.run_pricing_game((20261008, 7, 16, variant))
                self.assertEqual(new["initial_chips"], 14)
                self.assertEqual(new["seating"], old["seating"])
                self.assertEqual(new["win_share"], old["win_share"])
                for identity in multi_swap.IDENTITIES:
                    self.assertEqual(new["scores"][identity], old["scores"][identity] + 40)
                for key in ("voyages", "players", "decisions"):
                    self.assertEqual(len(new[key]), len(old[key]))
                    for old_row, new_row in zip(old[key], new[key]):
                        expected = {**old_row, "variant": variant}
                        if "score" in expected:
                            expected["score"] += 4
                        self.assertEqual(new_row, expected)
                self.assertEqual(sum(new["scores"].values()) + new["burned"], 700)

    def test_runner_maps_all_four_variants_to_correct_order_and_prices(self):
        with patch.object(multi_swap, "Strategy", AlwaysSwapPolicy):
            records = {variant: cost_ladder.run_pricing_game((7, 2, 1, variant))
                       for variant in cost_ladder.VARIANTS}
        for variant, record in records.items():
            reverse = variant.endswith("reverse")
            ladder = variant.startswith("ladder")
            self.assertEqual(record["variant"], variant)
            self.assertEqual(record["initial_chips"], 14)
            for voyage in record["voyages"]:
                expected_order = [(voyage["starter"] + step) % 5 for step in range(5)]
                self.assertEqual(voyage["window_order"], expected_order[::-1] if reverse else expected_order)
                self.assertEqual(voyage["pot"], 25 if ladder else 15)
            for row in record["decisions"]:
                self.assertEqual(row["cost"], row["rank"] + 1 if ladder else 2)
                self.assertEqual(row["net_points"], row["score"] - 14)
                self.assertEqual(row["variant"], variant)
            self.assertEqual(sum(record["scores"].values()) + record["burned"], 700)

    def test_order_difference_in_differences_pairs_before_averaging(self):
        fixtures = [
            pricing_records(0, [
                ([3, 0, 2], 2, -4, 1, -1, [2, 0, -2]),
                ([1, 2, 3, 4, 0], 6, -2, 2, -2, [6, 3, 0, -3, -6]),
            ]),
            pricing_records(4, [([], 0, 0, 0, 0, [])]),
            pricing_records(5, [([2, 4, 1], 10, -2, 6, -2, [4, 0, -4])]),
        ]
        records = {variant: [fixture[variant] for fixture in fixtures]
                   for variant in cost_ladder.VARIANTS}
        self.assertEqual(cost_ladder.order_game_effects(records["flat"], records["flat_reverse"]),
                         [(0, 3.5), (5, 6)])
        self.assertEqual(cost_ladder.order_game_effects(records["ladder"], records["ladder_reverse"]),
                         [(0, 1.5), (5, 4)])
        result = cost_ladder.compare_pricing(records)
        self.assertEqual(result["flat_order_balance"]["mean"], 4.75)
        self.assertEqual(result["ladder_order_balance"]["mean"], 2.75)
        difference = result["change_in_order_balance"]
        self.assertEqual(difference["mean"], -2)
        self.assertEqual(difference["ci95"], [-2, -2])
        self.assertEqual(difference["clusters"], 2)
        self.assertEqual(difference["games_used"], 2)
        positions = {(row["participants"], row["rank"]): row
                     for row in result["ladder_minus_flat_by_original_window_position"]}
        self.assertEqual(positions[3, 1]["mean"], 3)
        self.assertEqual(positions[3, 3]["mean"], -3)
        self.assertEqual(positions[5, 1]["mean"], 6)
        self.assertIsNone(positions[5, 1]["ci95"])


if __name__ == "__main__":
    unittest.main()
