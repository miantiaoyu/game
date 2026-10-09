"""Original betting-position statistics for full trajectory reversal runs."""

import unittest
from dataclasses import replace

from simulation.reverse_order_experiment import (
    METRICS,
    hand_positions,
    match_position_means,
    paired_position_change,
    per_position_summary,
    positional_summary,
    variant_config,
)


def fake_hand(index, start_order, net, window_order):
    return hand_positions({"hand_index": index, "start_order": start_order,
                           "window_order": window_order, "start_stacks": [100] * 5,
                           "end_stacks": [100 + value for value in net]})


def fake_match(game, hands):
    return {"game": game, "seed": 100 + game, "initial_button": game % 5,
            "position_means": {"all": match_position_means(hands),
                               "five_players": match_position_means(hands, 5)}}


class ReverseOrderStatisticsTests(unittest.TestCase):
    def test_original_betting_positions_include_players_absent_from_window(self):
        hand = fake_hand(1, [3, 0, 1, 2, 4], [7, 3, -4, -2, -4], [2, 0])
        self.assertEqual((hand["first_player"], hand["last_player"]), (3, 4))
        self.assertEqual(hand["net_first_minus_last"], 2)
        self.assertEqual(hand["profit_first_minus_last"], 0)
        self.assertEqual((hand["first_in_window"], hand["last_in_window"]), (0, 0))

    def test_profit_means_positive_net_not_nonnegative(self):
        hand = fake_hand(1, [0, 1, 2], [0, -2, 2, 0, 0], [])
        self.assertEqual(hand["first_profit"], 0)
        self.assertEqual(hand["last_profit"], 1)
        self.assertEqual(hand["profit_first_minus_last"], -1)

    def test_actual_paid_fees_include_passes_and_absences_as_zero(self):
        hand = {"hand_index": 1, "start_order": [4, 3, 2, 1, 0], "window_order": [0, 2, 4],
                "start_stacks": [100] * 5, "end_stacks": [100] * 5,
                "decisions": [{"player": 0, "fee": 2, "slot": None},
                              {"player": 2, "fee": 4, "slot": 0},
                              {"player": 4, "fee": 6, "slot": 1}]}
        result = hand_positions(hand)
        self.assertEqual(result["first_fee_paid"], 6)
        self.assertEqual(result["last_fee_paid"], 0)
        self.assertEqual(result["first_in_window"], 1)
        self.assertEqual(result["last_in_window"], 1)

    def test_all_participant_counts_are_mixed_with_equal_hand_weights(self):
        first = fake_hand(1, [0, 1, 2, 3, 4], [10, 0, 0, 0, -10], [])
        second = fake_hand(2, [1, 3], [0, -4, 0, 4, 0], [])
        mixed = match_position_means([first, second])
        five = match_position_means([first, second], participants=5)
        self.assertEqual(mixed["net_first_minus_last"], 6)
        self.assertEqual(mixed["profit_first_minus_last"], 0)
        self.assertEqual(five["net_first_minus_last"], 20)
        self.assertEqual(five["hands"], 1)

    def test_confidence_interval_uses_matches_not_unequal_hand_counts(self):
        a = fake_hand(1, [0, 1], [1, -1, 0, 0, 0], [])
        b = fake_hand(2, [2, 4], [0, 0, 5, 0, -5], [])
        result = positional_summary([fake_match(0, [a, a, a]), fake_match(1, [b])], "all")
        self.assertEqual(result["net_first_minus_last"]["mean"], 6)
        self.assertEqual(result["matches"], 2)
        self.assertEqual(result["hands"], 4)
        self.assertAlmostEqual(result["net_first_minus_last"]["ci95"][0], 6 - 1.96 * 4)

    def test_paired_change_aligns_match_ids_and_subtracts_before_interval(self):
        a = fake_hand(1, [0, 1], [50, -50, 0, 0, 0], [])
        b = fake_hand(1, [0, 1], [-50, 50, 0, 0, 0], [])
        ar = fake_hand(1, [0, 1], [51, -51, 0, 0, 0], [])
        br = fake_hand(1, [0, 1], [-48, 48, 0, 0, 0], [])
        result = paired_position_change([fake_match(0, [a]), fake_match(1, [b])],
                                        [fake_match(1, [br]), fake_match(0, [ar])], "all")
        self.assertEqual(result["net_first_minus_last"]["mean"], 3)
        self.assertAlmostEqual(result["net_first_minus_last"]["ci95"][0], 3 - 1.96)
        self.assertEqual(result["first_net"]["mean"], 1.5)
        self.assertEqual(result["last_net"]["mean"], -1.5)
        mismatch = fake_match(1, [br])
        mismatch["seed"] += 1
        with self.assertRaises(AssertionError):
            paired_position_change([fake_match(1, [b])], [mismatch], "all")

    def test_five_player_position_metrics_are_clustered_by_match(self):
        first = {"game": 0, **fake_hand(1, [3, 0, 1, 2, 4], [0, 0, 0, 2, -2], [])}
        second = {"game": 1, **fake_hand(1, [1, 2, 3, 4, 0], [-8, 8, 0, 0, 0], [])}
        result = per_position_summary([first, first, first, second])
        self.assertEqual(result[0]["net_chips"]["mean"], 5)
        self.assertEqual(result[0]["net_chips"]["matches"], 2)
        self.assertEqual(result[0]["hands"], 4)
        self.assertEqual(result[0]["profit_rate"]["mean"], 1)

    def test_reversal_uses_same_fee_profile_and_only_sets_window_flag(self):
        normal, reverse = variant_config("steep_normal"), variant_config("steep_reverse")
        self.assertEqual((normal.low_bps, normal.high_bps), (300, 1500))
        self.assertEqual((reverse.low_bps, reverse.high_bps), (300, 1500))
        self.assertFalse(normal.reverse_swaps)
        self.assertTrue(reverse.reverse_swaps)

    def test_disabled_swaps_remove_reversal_effect_on_full_trajectory(self):
        from simulation.persistent_game import run_match
        from simulation.persistent_policy import EquityPolicy

        normal = replace(variant_config("baseline_normal"), swaps_enabled=False)
        reverse = replace(variant_config("baseline_reverse"), swaps_enabled=False)
        a = run_match(1734, EquityPolicy(samples=8), normal, initial_button=2, trace=True)
        b = run_match(1734, EquityPolicy(samples=8), reverse, initial_button=2, trace=True)
        self.assertEqual(a["final_stacks"], b["final_stacks"])
        self.assertEqual(len(a["hands"]), len(b["hands"]))
        for first, second in zip(a["hands"], b["hands"]):
            for key in ("start_order", "start_stacks", "end_stacks", "board", "awards", "actions", "pots"):
                self.assertEqual(first[key], second[key])


if __name__ == "__main__":
    unittest.main()
