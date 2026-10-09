"""Behavioral checks for rules, accounting, and the policy information boundary."""

from collections import deque
from dataclasses import fields
import random
import unittest
from unittest.mock import patch

from simulation.game import play_voyage
from simulation.model import DECK, Observation, strength
from simulation.run import interval, paired_comparison
from simulation.strategies import (
    STRATEGY_NAMES,
    Strategy,
    estimate_equity,
    estimate_swap_equities,
)


class ScriptedPolicy:
    name = "scripted"

    def __init__(self, actions=(), swap=None, always_raise=False):
        self.actions = deque(actions)
        self.swap = swap
        self.always_raise = always_raise
        self.observations = []

    def choose_action(self, obs, rng):
        self.observations.append(obs)
        if self.actions:
            return self.actions.popleft()
        if self.always_raise and obs.raises_left:
            return "raise"
        return "call" if obs.to_call else "check"

    def choose_swap(self, obs, rng):
        self.observations.append(obs)
        return self.swap


def fixed_deal(draw_order):
    """Patch only shuffling; retain the real engine's dealing and settlement."""
    if len(set(draw_order)) != len(draw_order):
        raise ValueError("A physical card can occur only once in a deal")
    cards = [card for card in DECK if card not in draw_order]
    cards.extend(reversed(draw_order))

    def shuffle(_rng, deck):
        deck[:] = cards

    return patch.object(random.Random, "shuffle", shuffle)


def observation(**changes):
    values = dict(
        hand=((0, 9), (1, 9)),
        board=((0, 8), (0, 7), (0, 6)),
        discarded=(),
        active_players=3,
        pot=9,
        to_call=1,
        raises_left=1,
        stage=2,
        chips=7,
    )
    values.update(changes)
    return Observation(**values)


class RuleTests(unittest.TestCase):
    def test_score_chooses_one_hand_card_and_only_same_color_support(self):
        self.assertEqual(
            strength(((0, 5), (2, 9)), ((0, 7), (1, 4), (0, 2))), 14
        )
        self.assertEqual(
            strength(((0, 5), (2, 9)), ((2, 6), (1, 4), (0, 2))), 15
        )
        self.assertEqual(strength(((0, 5), (0, 9)), ((0, 2),)), 11)

    def test_two_raises_reopen_action_for_previous_players(self):
        players = [
            ScriptedPolicy(["check", "call"]),
            ScriptedPolicy(["raise", "call"]),
            ScriptedPolicy(["raise"]),
        ]
        result = play_voyage(players, trace=True)
        actions = [
            (event["seat"], event["action"], event["to_call"])
            for event in result.events
            if event["kind"] == "action" and event["stage"] == 0
        ]
        self.assertEqual(
            actions,
            [(0, "check", 0), (1, "raise", 0), (2, "raise", 1),
             (0, "call", 2), (1, "call", 1)],
        )
        self.assertEqual(result.pot, 9)
        self.assertEqual(sum(result.scores) + result.burned, 30)

    def test_last_survivor_wins_without_further_reveals_or_swaps(self):
        players = [ScriptedPolicy(["fold"]), ScriptedPolicy(["fold"]), ScriptedPolicy()]
        result = play_voyage(players, trace=True)
        self.assertEqual(result.winners, (2,))
        self.assertEqual(result.scores, (9, 9, 12))
        self.assertFalse(result.showdown)
        self.assertEqual(len(result.board), 1)
        self.assertEqual(len(players[2].observations), 0)
        self.assertEqual(sum(m["swap_opportunities"] for m in result.metrics), 0)

    def test_tied_winners_share_pot_and_discard_remainder(self):
        draws = [
            (0, 9), (2, 1), (1, 9), (2, 2), (3, 1), (2, 3),
            (0, 1), (1, 1), (3, 2),
        ]
        with fixed_deal(draws):
            result = play_voyage([ScriptedPolicy() for _ in range(3)])
        self.assertEqual(result.winners, (0, 1))
        self.assertEqual(result.scores, (10, 10, 9))
        self.assertEqual(result.burned, 1)
        self.assertEqual([m["win_share"] for m in result.metrics], [0.5, 0.5, 0])

    def test_all_players_can_pay_maximum_bets_and_one_swap(self):
        for n in (3, 4, 5):
            with self.subTest(players=n):
                players = [ScriptedPolicy(swap=0, always_raise=True) for _ in range(n)]
                result = play_voyage(players, trace=True)
                self.assertEqual(result.pot, 9 * n)
                self.assertEqual(sum(result.scores) + result.burned, 10 * n)
                self.assertEqual(len(result.discarded), n)
                self.assertEqual(len(result.board), 3)
                self.assertTrue(all(m["swaps"] == 1 for m in result.metrics))
                for seat in set(range(n)) - set(result.winners):
                    self.assertEqual(result.scores[seat], 1)

    def test_later_swappers_see_previous_replacement_and_public_discard(self):
        players = [ScriptedPolicy(swap=0), ScriptedPolicy(), ScriptedPolicy()]
        result = play_voyage(players, trace=True)
        swap = next(event for event in result.events if event["kind"] == "swap")
        later_view = next(obs for obs in players[1].observations if obs.stage == 1)
        self.assertEqual(later_view.discarded, (swap["removed"],))
        self.assertEqual(later_view.board[0], swap["added"])
        self.assertNotIn(swap["removed"], later_view.board)
        self.assertEqual(later_view.pot, 5)

    def test_illegal_check_and_third_raise_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "Cannot check"):
            play_voyage([ScriptedPolicy(["raise"]), ScriptedPolicy(["check"]), ScriptedPolicy()])
        with self.assertRaisesRegex(ValueError, "Only two"):
            play_voyage([ScriptedPolicy(["raise"]) for _ in range(3)])

    def test_seeded_benchmark_is_reproducible_and_conserves_points(self):
        for n in (3, 4, 5):
            for seed in range(4):
                with self.subTest(players=n, seed=seed):
                    players = [Strategy(name, samples=12) for name in STRATEGY_NAMES[:n]]
                    args = dict(starter=seed % n, deck_seed=seed, decision_seed=seed, trace=True)
                    result = play_voyage(players, **args)
                    self.assertEqual(result, play_voyage(players, **args))
                    self.assertEqual(sum(result.scores) + result.burned, n * 10)
                    self.assertAlmostEqual(sum(m["win_share"] for m in result.metrics), 1)

    def test_evaluating_but_declining_swaps_preserves_paired_betting(self):
        def players(swaps_enabled):
            return [Strategy("navigator", samples=12, swaps_enabled=swaps_enabled)] + [
                Strategy("calling_station") for _ in range(2)
            ]

        # Force the enabled navigator to run its estimator but decline the swap.
        # That computation must not alter later betting randomness.
        with patch.dict("simulation.strategies.THRESHOLDS", {"swap_min_value_gain": 1e9}):
            main = play_voyage(players(True), deck_seed=42, decision_seed=9, trace=True)
            control = play_voyage(players(False), deck_seed=42, decision_seed=9, trace=True)
        self.assertEqual(main.metrics[0]["swap_opportunities"], 1)
        self.assertEqual(main.metrics[0]["swaps"], 0)
        self.assertEqual(main, control)


class InformationAndEquityTests(unittest.TestCase):
    def test_observation_exposes_only_own_hand_and_public_information(self):
        self.assertEqual(
            {field.name for field in fields(Observation)},
            {"hand", "board", "discarded", "active_players", "pot", "to_call",
             "raises_left", "stage", "chips", "swap_cost"},
        )
        players = [ScriptedPolicy() for _ in range(3)]
        play_voyage(players)
        private_hands = [player.observations[0].hand for player in players]
        self.assertEqual(len(set(card for hand in private_hands for card in hand)), 6)
        for seat, player in enumerate(players):
            for obs in player.observations:
                self.assertEqual(obs.hand, private_hands[seat])
                self.assertTrue(set(obs.board).isdisjoint(card for hand in private_hands for card in hand))

    def test_equity_recognizes_unbeatable_and_losing_final_hands(self):
        self.assertEqual(estimate_equity(observation(), random.Random(1), 40), 1)
        losing = observation(hand=((0, 1), (0, 2)), board=((1, 9), (2, 9), (3, 9)))
        self.assertEqual(estimate_equity(losing, random.Random(1), 40), 0)

    def test_equity_counts_three_way_ties_as_one_third(self):
        obs = observation(hand=((0, 9), (3, 1)), board=((0, 1), (1, 1), (2, 1)))
        with patch.object(random.Random, "sample", return_value=[(1, 9), (3, 2), (2, 9), (3, 3)]):
            self.assertAlmostEqual(estimate_equity(obs, random.Random(1), 3), 1 / 3)

    def test_equity_and_swap_sampling_exclude_all_visible_cards(self):
        obs = observation(board=((0, 8), (1, 7)), discarded=((2, 9), (3, 9)), stage=1)
        visible = set(obs.hand + obs.board + obs.discarded)
        samples = []
        real_sample = random.Random.sample

        def inspect_sample(rng, population, k):
            self.assertEqual(set(population), set(DECK) - visible)
            drawn = real_sample(rng, population, k)
            self.assertEqual(len(set(drawn)), k)
            samples.append(drawn)
            return drawn

        with patch.object(random.Random, "sample", inspect_sample):
            estimate_equity(obs, random.Random(7), 4)
            keep, replacements = estimate_swap_equities(obs, random.Random(8), 4)
        self.assertEqual(len(samples), 8)
        self.assertTrue(all(0 <= value <= 1 for value in (keep,) + replacements))

    def test_monte_carlo_sample_count_does_not_advance_behavior_rng(self):
        for name in ("equity", "bluffer", "navigator"):
            with self.subTest(strategy=name):
                # An unbeatable hand follows the same branch at any sample count.
                rng_a, rng_b = random.Random(7), random.Random(7)
                Strategy(name, samples=1).choose_action(observation(), rng_a)
                Strategy(name, samples=50).choose_action(observation(), rng_b)
                self.assertEqual(rng_a.getstate(), rng_b.getstate())


class StatisticsTests(unittest.TestCase):
    def test_five_copies_of_each_block_do_not_count_as_independent_samples(self):
        # Two independent block means, 0 and 2, have mean 1 and SE 1.
        result = interval([0.0] * 5 + [2.0] * 5)
        self.assertEqual(result["clusters"], 2)
        self.assertEqual(result["mean"], 1)
        self.assertAlmostEqual(result["ci95"][0], 1 - 1.96)
        self.assertAlmostEqual(result["ci95"][1], 1 + 1.96)
        # An incomplete final block must preserve the game-weighted mean.
        partial = interval([0.0] * 5 + [2.0])
        self.assertAlmostEqual(partial["mean"], 1 / 3)
        self.assertEqual(partial["clusters"], 2)

    def test_paired_intervals_remove_shared_deal_variation(self):
        def record(scores):
            top = max(scores.values())
            winners = [name for name, score in scores.items() if score == top]
            return {
                "scores": scores,
                "win_credit": {
                    name: 1 / len(winners) if name in winners else 0
                    for name in STRATEGY_NAMES
                },
            }

        main, control = [], []
        for game in range(10):
            scores = {name: 100 for name in STRATEGY_NAMES}
            scores.update(navigator=80 + 5 * game, equity=120 - 5 * game)
            control.append(record(scores))
            changed = dict(scores)
            changed["navigator"] += 4
            changed["equity"] -= 4
            main.append(record(changed))
        differences = paired_comparison(main, control)
        for name, expected in (("navigator", 4), ("equity", -4), ("bluffer", 0)):
            result = differences[name]["points_per_match_difference"]
            self.assertEqual(result["mean"], expected)
            self.assertEqual(result["ci95"], [expected, expected])
        # Only game 4 changes navigator's victory credit: 1/5 becomes 1.
        self.assertAlmostEqual(differences["navigator"]["win_rate_difference"]["mean"], 0.08)


if __name__ == "__main__":
    unittest.main()
