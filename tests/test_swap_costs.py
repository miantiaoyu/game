"""Verify rank-dependent swap fees, bankroll accounting, and fee-aware policies."""

from dataclasses import replace
import random
import unittest
from unittest.mock import patch

from simulation.game import play_voyage
from simulation.model import Observation
from simulation.strategies import Strategy


COSTS = (2, 3, 4, 5, 6)


class FeePolicy:
    name = "fee_scripted"

    def __init__(self, swap=0, fold=False, raise_always=False):
        self.swap = swap
        self.fold = fold
        self.raise_always = raise_always
        self.swap_observations = []

    def choose_action(self, obs, rng):
        if self.fold and obs.stage == 0:
            return "fold"
        if self.raise_always and obs.raises_left:
            return "raise"
        return "call" if obs.to_call else "check"

    def choose_swap(self, obs, rng):
        self.swap_observations.append(obs)
        return self.swap


def events(result, kind):
    return [event for event in result.events if event["kind"] == kind]


def obs(**changes):
    values = dict(
        hand=((0, 9), (1, 9)), board=((2, 8), (3, 8)), discarded=(),
        active_players=5, pot=10, to_call=0, raises_left=2, stage=1, chips=10,
    )
    values.update(changes)
    return Observation(**values)


class SwapFeeRulesTests(unittest.TestCase):
    def test_fee_follows_actual_window_rank_and_is_public_before_decision(self):
        players = [FeePolicy() for _ in range(5)]
        result = play_voyage(players, starter=2, swap_costs=COSTS, initial_chips=14, trace=True)
        decisions = events(result, "swap_decision")
        self.assertEqual([event["seat"] for event in decisions], [2, 3, 4, 0, 1])
        self.assertEqual([event["cost"] for event in decisions], list(COSTS))
        self.assertEqual([event["cost"] for event in events(result, "swap")], list(COSTS))
        self.assertEqual([event["pot"] for event in decisions], [5, 7, 10, 14, 19])
        self.assertEqual([player.swap_observations[0].swap_cost for player in players], [5, 6, 2, 3, 4])
        self.assertEqual([metric["swap_spent"] for metric in result.metrics], [5, 6, 2, 3, 4])
        self.assertEqual(result.pot, 25)
        self.assertEqual(sum(result.scores) + result.burned, 70)

    def test_passing_does_not_discount_later_players(self):
        players = [FeePolicy(swap=None if seat in (0, 2) else 0) for seat in range(5)]
        result = play_voyage(players, swap_costs=COSTS, initial_chips=14, trace=True)
        decisions = events(result, "swap_decision")
        self.assertEqual([event["cost"] for event in decisions], list(COSTS))
        self.assertEqual([event["cost"] for event in events(result, "swap")], [3, 5, 6])
        self.assertEqual([event["pot"] for event in decisions], [5, 5, 8, 8, 13])
        self.assertEqual([metric["swap_spent"] for metric in result.metrics], [0, 3, 0, 5, 6])
        self.assertEqual(result.pot, 19)

    def test_folding_compacts_ranks_before_assigning_fees(self):
        players = [FeePolicy(fold=seat in (1, 3)) for seat in range(5)]
        result = play_voyage(players, starter=2, swap_costs=COSTS, initial_chips=14, trace=True)
        decisions = events(result, "swap_decision")
        self.assertEqual([event["seat"] for event in decisions], [2, 4, 0])
        self.assertEqual([event["rank"] for event in decisions], [1, 2, 3])
        self.assertEqual([event["cost"] for event in decisions], [2, 3, 4])
        self.assertEqual([metric["swap_spent"] for metric in result.metrics], [4, 0, 2, 0, 3])
        self.assertEqual(players[1].swap_observations, [])
        self.assertEqual(players[3].swap_observations, [])

    def test_reversal_assigns_fees_using_new_rank_after_folds(self):
        players = [FeePolicy(fold=seat in (1, 3)) for seat in range(5)]
        result = play_voyage(players, starter=2, swap_costs=COSTS, initial_chips=14,
                             reverse_swap_order=True, trace=True)
        decisions = events(result, "swap_decision")
        self.assertEqual([event["seat"] for event in decisions], [0, 4, 2])
        self.assertEqual([event["cost"] for event in decisions], [2, 3, 4])
        self.assertEqual([metric["swap_spent"] for metric in result.metrics], [2, 0, 4, 0, 3])

    def test_maximum_bets_and_swaps_conserve_scores_and_allow_exact_bankroll(self):
        for initial_chips in (13, 14):
            for count in (3, 4, 5):
                with self.subTest(initial_chips=initial_chips, players=count):
                    players = [FeePolicy(raise_always=True) for _ in range(count)]
                    result = play_voyage(players, swap_costs=COSTS, initial_chips=initial_chips)
                    self.assertEqual(result.pot, count * 7 + sum(COSTS[:count]))
                    self.assertEqual(sum(result.scores) + result.burned, initial_chips * count)
                    self.assertEqual(sum(metric["net_points"] for metric in result.metrics), -result.burned)
                    award = result.pot // len(result.winners)
                    for seat, score in enumerate(result.scores):
                        reserve = score - (award if seat in result.winners else 0)
                        self.assertEqual(reserve, initial_chips - 7 - COSTS[seat])
                        self.assertEqual(result.metrics[seat]["net_points"], score - initial_chips)

    def test_explicit_fixed_fee_and_default_bankroll_preserve_default_result(self):
        for seed in (0, 7, 42):
            players = [Strategy("navigator", samples=16) for _ in range(5)]
            args = dict(deck_seed=seed, decision_seed=seed, starter=seed % 5, trace=True)
            default = play_voyage(players, **args)
            explicit = play_voyage(players, swap_costs=(2,) * 5, initial_chips=10, **args)
            self.assertEqual(default, explicit)
            self.assertTrue(all(event["cost"] == 2 for event in events(default, "swap_decision")))

    def test_invalid_fee_tables_and_insufficient_bankroll_are_rejected_before_play(self):
        for costs in ((), (2, 3), [2, 3, 4, 5, 6], (2, 3, 4, 5, True),
                      (2, 3, 4, 5, 6.0), (2, 3, 4, 5, 0), (2, 3, 4, 5, -1)):
            with self.subTest(costs=costs), self.assertRaises(ValueError):
                play_voyage([FeePolicy() for _ in range(5)], swap_costs=costs, initial_chips=14)
        for initial_chips in (12, 0, True, 14.0):
            with self.subTest(initial_chips=initial_chips), self.assertRaises(ValueError):
                play_voyage([FeePolicy() for _ in range(5)], swap_costs=COSTS, initial_chips=initial_chips)
        with self.assertRaises(ValueError):
            play_voyage([FeePolicy() for _ in range(3)], initial_chips=8)


class FeeAwarePolicyTests(unittest.TestCase):
    def test_higher_fee_can_make_the_same_equity_improvement_unprofitable(self):
        policy = Strategy("navigator")
        with patch("simulation.strategies.estimate_swap_equities", return_value=(0.2, (0.4, 0.3))):
            self.assertEqual(policy.choose_swap(obs(swap_cost=2), random.Random(1)), 0)
            self.assertIsNone(policy.choose_swap(obs(swap_cost=6), random.Random(1)))

    def test_swap_value_includes_actual_fee_in_the_pot_as_well_as_cost(self):
        # Keeping is worth 5.5. Swapping at fee 6 is .75 * 16 - 6 = 6.
        # Adding only the old fee 2 to the pot would incorrectly reject it.
        with patch("simulation.strategies.estimate_swap_equities", return_value=(0.55, (0.75, 0.6))):
            self.assertEqual(Strategy("navigator").choose_swap(obs(swap_cost=6), random.Random(1)), 0)

    def test_affordability_uses_actual_fee_before_running_estimator(self):
        policy = Strategy("navigator")
        with patch("simulation.strategies.estimate_swap_equities", return_value=(0, (1, 0))) as estimator:
            self.assertIsNone(policy.choose_swap(obs(chips=5, swap_cost=6), random.Random(1)))
            estimator.assert_not_called()
            self.assertEqual(policy.choose_swap(obs(chips=6, swap_cost=6), random.Random(1)), 0)
            estimator.assert_called_once()

    def test_observation_default_fee_retains_original_decision(self):
        view = obs()
        self.assertEqual(view.swap_cost, 2)
        self.assertEqual(
            Strategy("navigator", samples=16).choose_swap(view, random.Random(7)),
            Strategy("navigator", samples=16).choose_swap(replace(view, swap_cost=2), random.Random(7)),
        )


if __name__ == "__main__":
    unittest.main()
