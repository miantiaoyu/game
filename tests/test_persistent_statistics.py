"""Statistical estimands and denominators for persistent-bankroll experiments."""

import unittest

from simulation.persistent_experiment import aggregate_pairs, decision_summary, economy_summary, interval, paired_effect, paired_profile_difference, profile_config, summarize_match


class PersistentStatisticsTests(unittest.TestCase):
    def test_early_advantage_sign_uses_same_identities(self):
        normal = {"hand_index": 2, "window_order": [3, 1, 4], "start_stacks": [100] * 5, "end_stacks": [100, 100, 100, 109, 91]}
        reverse = {"hand_index": 2, "window_order": [4, 1, 3], "start_stacks": [100] * 5, "end_stacks": [100, 100, 100, 101, 99]}
        effect = paired_effect(normal, reverse)
        self.assertEqual(effect["original_first_early_gain"], 8)
        self.assertEqual(effect["original_last_early_gain"], 8)
        self.assertEqual(effect["early_advantage"], 8)
        self.assertEqual(effect["first"], 3)
        self.assertEqual(effect["last"], 4)

    def test_mismatched_snapshot_or_order_rejected(self):
        normal = {"hand_index": 0, "window_order": [1, 4], "start_stacks": [100] * 5, "end_stacks": [100] * 5}
        reverse = {**normal, "window_order": [4, 2]}
        with self.assertRaises(AssertionError):
            paired_effect(normal, reverse)
        reverse = {**normal, "window_order": [4, 1], "start_stacks": [99] * 5}
        with self.assertRaises(AssertionError):
            paired_effect(normal, reverse)

    def test_no_comparison_for_single_player_window(self):
        hand = {"hand_index": 0, "window_order": [2], "start_stacks": [100] * 5, "end_stacks": [100] * 5}
        self.assertIsNone(paired_effect(hand, hand))

    def test_ci_equal_weights_independent_matches_not_windows(self):
        result = aggregate_pairs([
            {"game": 0, "participants": 2, "early_advantage": 0},
            {"game": 0, "participants": 3, "early_advantage": 4},
            {"game": 9, "participants": 2, "early_advantage": 8},
        ])
        # Match means 2 and 8, not the window mean 4.
        self.assertEqual(result["all"]["mean"], 5)
        self.assertEqual(result["all"]["matches"], 2)
        self.assertEqual(result["all"]["windows"], 3)
        self.assertAlmostEqual(result["all"]["ci95"][0], 5 - 1.96 * 3)
        self.assertEqual(result["by_participants"]["2"]["mean"], 4)
        self.assertIsNone(result["by_participants"]["3"]["ci95"])
        self.assertIsNone(result["by_participants"]["5"]["mean"])

    def test_zero_action_windows_remain_in_estimate(self):
        result = aggregate_pairs([
            {"game": 0, "participants": 2, "early_advantage": 0},
            {"game": 0, "participants": 2, "early_advantage": 10},
            {"game": 1, "participants": 2, "early_advantage": 0},
        ])["all"]
        self.assertEqual(result["mean"], 2.5)
        self.assertEqual(result["windows"], 3)
        self.assertEqual(result["matches"], 2)

    def test_sparse_intervals_do_not_fabricate_precision(self):
        self.assertEqual(interval([]), {"mean": None, "ci95": None, "matches": 0})
        self.assertEqual(interval([7]), {"mean": 7, "ci95": None, "matches": 1})

    def test_profile_difference_pairs_match_ids_before_computing_interval(self):
        candidate = [{"game": 9, "early_advantage": -96}, {"game": 0, "early_advantage": 102}]
        baseline = [{"game": 0, "early_advantage": 100}, {"game": 9, "early_advantage": -100}]
        result = paired_profile_difference(candidate, baseline)
        self.assertEqual(result["mean"], 3)  # Same-match differences 2 and 4.
        self.assertEqual(result["matches"], 2)
        self.assertAlmostEqual(result["ci95"][0], 3 - 1.96)
        self.assertAlmostEqual(result["ci95"][1], 3 + 1.96)
        self.assertEqual([row["game"] for row in result["match_differences"]], [0, 9])
        with self.assertRaises(AssertionError):
            paired_profile_difference(candidate, baseline[:1])

    def test_swap_and_overwrite_have_distinct_denominators(self):
        rows = [
            {"swapped": 1, "overwritten": 1, "fee_paid": 3, "fee_quote": 3, "net_chips": 5},
            {"swapped": 1, "overwritten": 0, "fee_paid": 7, "fee_quote": 7, "net_chips": -7},
            {"swapped": 0, "overwritten": 0, "fee_paid": 0, "fee_quote": 9, "net_chips": 8},
        ]
        result = decision_summary(rows)
        self.assertEqual(result["opportunities"], 3)
        self.assertEqual(result["swaps"], 2)
        self.assertEqual(result["swap_rate"], 2 / 3)
        self.assertEqual(result["overwritten_rate_per_swap"], 0.5)
        self.assertEqual(result["mean_fee_per_swap"], 5)
        self.assertEqual(result["mean_net_chips_descriptive"], 2)

    def test_reverse_changes_order_without_changing_profile(self):
        config = profile_config("steep", reverse=True)
        self.assertEqual((config.low_bps, config.high_bps), (300, 1500))
        self.assertTrue(config.reverse_swaps)
        self.assertEqual(config.initial_chips, 100)
        self.assertEqual(config.hands, 12)
        self.assertFalse(profile_config("no_swap").swaps_enabled)

    def test_start_positions_include_folded_players_and_rotate_identities(self):
        first = {"hand_index": 1, "start_order": [3, 4, 0, 1, 2], "window_order": [0, 2],
                 "start_stacks": [100] * 5, "end_stacks": [110, 99, 95, 97, 99],
                 "decisions": [], "had_side_pot": False, "all_in_count": 0}
        second = {"hand_index": 2, "start_order": [4, 0, 1, 2, 3], "window_order": [0, 1],
                  "start_stacks": first["end_stacks"], "end_stacks": [115, 109, 90, 90, 96],
                  "decisions": [], "had_side_pot": False, "all_in_count": 0}
        record = {"final_stacks": second["end_stacks"], "hands": [first, second]}
        match = summarize_match(record, 0, 100, "baseline", 3)
        self.assertEqual(len(match["start_positions"]), 10)
        result = economy_summary([match], [])
        positions = {row["rank"]: row for row in result["start_positions_descriptive"]}
        # Original first seats 3 and 4 never reach either swap window.
        self.assertEqual(positions[1]["hands"], 2)
        self.assertEqual(positions[1]["mean_net_chips"], -3)
        self.assertEqual(positions[2]["mean_net_chips"], 2)
        self.assertEqual(sum(row["mean_net_chips"] for row in positions.values()), 0)


if __name__ == "__main__":
    unittest.main()
