"""Validate the estimands and denominators in the multi-player experiment."""

import unittest
from unittest.mock import patch

from simulation import multi_swap


class AlwaysSwapPolicy:
    """Make enabled/disabled identity errors observable without Monte Carlo."""

    def __init__(self, name, samples, swaps_enabled):
        self.name = name
        self.swaps_enabled = swaps_enabled

    def choose_action(self, obs, rng):
        return "call" if obs.to_call else "check"

    def choose_swap(self, obs, rng):
        return 0 if self.swaps_enabled else None


def paired_records(game, windows):
    """Each window supplies order and the first/last players' score effects."""
    normal = {"game": game, "voyages": [], "players": []}
    reverse = {"game": game, "voyages": [], "players": []}
    for voyage, (order, first_delta, last_delta) in enumerate(windows):
        normal["voyages"].append({"voyage": voyage, "window_order": order})
        reverse["voyages"].append({"voyage": voyage, "window_order": order[::-1]})
        for seat in range(5):
            delta = 0
            if order and seat == order[0]:
                delta = first_delta
            elif order and seat == order[-1]:
                delta = last_delta
            normal["players"].append({"voyage": voyage, "seat": seat,
                                      "net_points": seat, "win_share": 0.2})
            reverse["players"].append({"voyage": voyage, "seat": seat,
                                       "net_points": seat + delta,
                                       "win_share": 0.2 + delta / 100})
    return normal, reverse


class MultiSwapStatisticsTests(unittest.TestCase):
    def test_overwrite_rate_counts_swaps_while_outcomes_count_all_opportunities(self):
        rows = [
            {"swap_index": 0, "overwritten": 0, "net_points": 8, "win_share": 1},
            {"swap_index": 1, "overwritten": 1, "net_points": -5, "win_share": 0},
            {"swap_index": None, "overwritten": 0, "net_points": 3, "win_share": 0.5},
            {"swap_index": None, "overwritten": 0, "net_points": -1, "win_share": 0},
        ]
        result = multi_swap.decision_statistics(rows)
        self.assertEqual(result["opportunities"], 4)
        self.assertEqual(result["swaps"], 2)
        self.assertEqual(result["swap_rate"], 0.5)
        self.assertEqual(result["overwritten_rate_per_swap"], 0.5)
        self.assertEqual(result["mean_net_points"], 1.25)
        self.assertEqual(result["win_share"], 0.375)

    def test_missing_games_preserve_original_clusters(self):
        result = multi_swap.nullable_interval([(0, 0), (4, 0), (5, 4), (9, 4)])
        self.assertEqual(result["clusters"], 2)
        self.assertEqual(result["games_used"], 4)
        self.assertEqual(result["mean"], 2)
        self.assertAlmostEqual(result["ci95"][0], 2 - 1.96 * 2)
        self.assertAlmostEqual(result["ci95"][1], 2 + 1.96 * 2)

    def test_unavailable_or_single_cluster_does_not_fabricate_an_interval(self):
        result = multi_swap.nullable_interval([])
        self.assertIsNone(result["mean"])
        self.assertIsNone(result["ci95"])
        self.assertEqual(result["games_used"], 0)
        result = multi_swap.nullable_interval([(0, 1), (4, 3)])
        self.assertEqual(result["mean"], 2)
        self.assertEqual(result["clusters"], 1)
        self.assertIsNone(result["ci95"])

    def test_reverse_pairs_same_seat_and_weights_games_not_windows(self):
        pairs = [
            paired_records(0, [([3, 0, 2], 2, -1), ([1, 2, 3, 4, 0], 6, -3)]),
            paired_records(4, [([], 0, 0)]),
            paired_records(5, [([2, 4, 1], 10, -6)]),
        ]
        result = multi_swap.paired_order_comparison(
            [normal for normal, _ in pairs], [reverse for _, reverse in pairs]
        )["by_window_participants"]
        combined = result["all"]
        self.assertEqual(combined["eligible_voyages"], 3)
        first = combined["original_first"]["net_points"]
        self.assertEqual(first["mean"], 7)  # mean(mean(2, 6), 10)
        self.assertEqual(first["games_used"], 2)
        self.assertEqual(first["clusters"], 2)
        self.assertEqual(combined["original_last"]["net_points"]["mean"], -4)
        self.assertAlmostEqual(combined["original_first"]["win_share"]["mean"], 0.07)
        self.assertEqual(result["3"]["original_first"]["net_points"]["mean"], 6)
        self.assertEqual(result["5"]["original_first"]["net_points"]["mean"], 6)
        self.assertIsNone(result["5"]["original_first"]["net_points"]["ci95"])
        self.assertEqual(result["2"]["eligible_voyages"], 0)
        self.assertIsNone(result["2"]["original_first"]["net_points"]["mean"])

    def test_reverse_rejects_changed_window_membership(self):
        normal, reverse = paired_records(0, [([3, 0, 2], 1, -1)])
        reverse["voyages"][0]["window_order"] = [2, 1, 3]
        with self.assertRaises(AssertionError):
            multi_swap.paired_order_comparison([normal], [reverse])

    def test_target_disable_follows_identity_through_every_seat(self):
        occupied_seats = set()
        with patch.object(multi_swap, "Strategy", AlwaysSwapPolicy):
            for game in range(5):
                result = multi_swap.run_game((20261008, game, 1, "target_disabled"))
                occupied_seats.add(result["seating"].index("P1"))
                for identity in multi_swap.IDENTITIES:
                    decisions = [row for row in result["decisions"]
                                 if row["identity"] == identity]
                    self.assertEqual(len(decisions), 10)
                    self.assertEqual(sum(row["swap_index"] is not None for row in decisions),
                                     0 if identity == "P1" else 10)
                self.assertEqual(sum(result["scores"].values()) + result["burned"], 500)
        self.assertEqual(occupied_seats, set(range(5)))

    def test_aggregate_separates_passes_retained_cards_and_overwritten_cards(self):
        with patch.object(multi_swap, "Strategy", AlwaysSwapPolicy):
            records = [multi_swap.run_game((20261008, game, 1, "target_disabled"))
                       for game in range(10)]
        result = multi_swap.aggregate(records)
        # In 100 windows, four players each replace slot zero. Three of the
        # four resulting cards are overwritten; P1 declines its opportunity.
        self.assertEqual(result["window_voyages"], 100)
        self.assertEqual(result["swap_opportunities"], 500)
        self.assertEqual(result["swaps"], 400)
        self.assertEqual(result["overwritten_swaps"], 300)
        self.assertEqual(result["swap_rate"], 0.8)
        self.assertEqual(result["overwritten_rate_per_swap"], 0.75)
        self.assertEqual(result["multi_swap_voyages"], 100)
        self.assertEqual(result["repeated_slot_rate_multi_swap_voyages"], 1)
        for condition, count in (("passed", 100), ("retained", 100), ("overwritten", 300)):
            self.assertEqual(result["conditional_outcomes"][condition]["opportunities"], count)

    def test_target_pair_uses_p1_and_cancels_shared_game_variation(self):
        normal, disabled = [], []
        for game in range(10):
            seating = multi_swap.seating_for(20261008, game)
            base = 80 + game * 7
            normal.append({"game": game, "seating": seating,
                           "scores": {"P1": base + 3, "P2": 50 - game},
                           "win_share": {"P1": 1, "P2": 0}})
            disabled.append({"game": game, "seating": seating,
                             "scores": {"P1": base, "P2": 100 + game},
                             "win_share": {"P1": 0.5, "P2": 0.5}})
        result = multi_swap.paired_target_comparison(normal, disabled)
        points = result["points_per_match_difference"]
        self.assertEqual(points["mean"], 3)
        self.assertEqual(points["ci95"], [3, 3])
        self.assertEqual(points["clusters"], 2)
        self.assertEqual(result["match_win_share_difference"]["mean"], 0.5)
        with self.assertRaises(AssertionError):
            multi_swap.paired_target_comparison(normal, disabled[::-1])


if __name__ == "__main__":
    unittest.main()
