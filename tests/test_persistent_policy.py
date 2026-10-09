"""Public-information valuation, side-pot caps, and fee accounting checks."""

from dataclasses import replace
from itertools import combinations
import random
import unittest
from unittest.mock import patch

from simulation.persistent_model import Action, DECK, Observation, Pot
from simulation.persistent_policy import (
    EquityPolicy, PayoutEstimate, SwapEstimate, _TRIPLE, encode_score,
    estimate_payouts, estimate_swaps, fast_score, pots_after_call,
)
from simulation.three_suit_analysis import best_score, triple_score


def observation(**changes):
    values = dict(
        player=0, hand=(0, 9), board=(1, 10), discarded=(),
        stacks=(50, 50, 50), contributions=(10, 10, 10), folded=(False, False, False),
        street_contributions=(0, 0, 0), ante=1, street=1, to_call=0,
        min_raise=2, max_add=30, can_raise=True, pots=(Pot(30, (0, 1, 2)),),
        bonus_pots=(), swap_fee=3, swap_eligible=(0, 1, 2), swap_rank=1, swap_count=3,
    )
    values.update(changes)
    return Observation(**values)


class FastCardTests(unittest.TestCase):
    def test_encoded_triples_preserve_reference_order_and_all_permutations(self):
        for a, b, c in combinations(DECK, 3):
            encoded = encode_score(triple_score((a, b, c)))
            self.assertEqual(_TRIPLE[a * 729 + b * 27 + c], encoded)
            self.assertEqual(_TRIPLE[c * 729 + a * 27 + b], encoded)
        ordered = sorted({triple_score(cards) for cards in combinations(DECK, 3)})
        self.assertEqual(sorted(encode_score(score) for score in ordered),
                         [encode_score(score) for score in ordered])

    def test_fast_nine_candidates_equal_reference_best_for_sampled_layouts(self):
        rng = random.Random(20261008)
        for _ in range(1000):
            draw = rng.sample(DECK, 5)
            hand, board = tuple(draw[:2]), tuple(draw[2:])
            self.assertEqual(fast_score(hand, board), encode_score(best_score(hand, board)))

    def test_trips_beat_straight_flush_and_public_only_trips_are_ineligible(self):
        self.assertGreater(fast_score((8, 17), (26, 0, 1)),
                           fast_score((6, 7), (8, 9, 10)))
        self.assertLess(fast_score((0, 10), (8, 17, 26)), 5000)
        self.assertEqual(fast_score((8, 17), (26, 0, 1)), 5900)


class PotValuationTests(unittest.TestCase):
    def test_short_all_in_call_caps_new_side_pot_rights(self):
        obs = observation(stacks=(5, 50, 50), contributions=(10, 30, 30), to_call=20,
                          pots=(Pot(30, (0, 1, 2)), Pot(40, (1, 2))))
        expected = (Pot(45, (0, 1, 2)), Pot(30, (1, 2)))
        self.assertEqual(pots_after_call(obs, 20), expected)
        self.assertEqual(pots_after_call(obs, 999), expected)
        with patch("simulation.persistent_policy._scores", return_value=[100, 20, 10]):
            estimate = estimate_payouts(obs, random.Random(1), 3, call_amount=20)
        self.assertEqual(estimate.pot_returns, (45.0, 0.0))
        self.assertEqual(estimate.expected_return, 45.0)
        self.assertEqual(estimate.accessible_pot, 45)
        self.assertAlmostEqual(estimate.fair_share, 1 / 3)

    def test_folded_money_stays_in_pools_but_cannot_win(self):
        obs = observation(stacks=(5, 50, 50), contributions=(10, 30, 30), to_call=20,
                          folded=(False, True, False), pots=(Pot(30, (0, 2)), Pot(40, (2,))))
        self.assertEqual(pots_after_call(obs, 5), (Pot(45, (0, 2)), Pot(30, (2,))))
        with patch("simulation.persistent_policy._scores", return_value=[100, -1, 20]):
            estimate = estimate_payouts(obs, random.Random(1), 2, call_amount=5)
        self.assertEqual(estimate.expected_return, 45)
        self.assertEqual(estimate.fair_share, 0.5)

    def test_each_pool_uses_own_eligibility_and_ties(self):
        obs = observation(pots=(Pot(30, (0, 1, 2)), Pot(40, (1, 2))),
                          bonus_pots=(Pot(12, (0, 2)), Pot(9, (1, 2))))
        with patch("simulation.persistent_policy._scores", return_value=[100, 100, 20]):
            estimate = estimate_payouts(obs, random.Random(1), 2)
        self.assertEqual(estimate.pot_returns, (15, 0, 12, 0))
        self.assertEqual(estimate.expected_return, 27)
        self.assertEqual(estimate.accessible_pot, 42)

    def test_swap_fee_recovery_does_not_purchase_side_pot_rights(self):
        obs = observation(pots=(Pot(30, (0, 1, 2)), Pot(40, (1, 2))),
                          bonus_pots=(Pot(12, (0, 2)),), swap_fee=9)
        with patch("simulation.persistent_policy._scores", side_effect=(
            [100, 200, 0], [200, 100, 0], [100, 100, 0],
        )):
            estimate = estimate_swaps(obs, random.Random(1), 1)
        self.assertEqual(estimate.keep_return, 12)
        self.assertEqual(estimate.swap_returns, (51, 31.5))
        self.assertEqual(estimate.fee_recovery, (9, 4.5))

    def test_own_fee_recovery_uses_new_fee_eligibility(self):
        obs = observation(swap_eligible=(1, 2))
        with patch("simulation.persistent_policy._scores", return_value=[100, 20, 10]):
            estimate = estimate_swaps(obs, random.Random(1), 1)
        self.assertEqual(estimate.fee_recovery, (0, 0))
        self.assertEqual(estimate.swap_returns, (30, 30))


class SamplingTests(unittest.TestCase):
    def test_draws_exclude_own_cards_board_and_discards_and_pair_swap_candidates(self):
        obs = observation(discarded=(20, 21), folded=(False, False, True))
        samples = []
        score_calls = []
        real_sample = random.Random.sample

        def inspect_sample(rng, population, count):
            self.assertEqual(set(population), set(DECK) - set(obs.hand + obs.board + obs.discarded))
            # One active opponent, one future public card, one replacement.
            self.assertEqual(count, 4)
            draw = real_sample(rng, population, count)
            samples.append(draw)
            return draw

        def inspect_scores(view, opponents, cards, board):
            self.assertEqual(opponents, (1,))
            score_calls.append((tuple(cards), board))
            return [100, 50, -1]

        with patch.object(random.Random, "sample", inspect_sample), patch(
            "simulation.persistent_policy._scores", side_effect=inspect_scores
        ):
            estimate_swaps(obs, random.Random(1), 4)
        self.assertEqual(len(samples), 4)
        self.assertEqual(len(score_calls), 12)
        for index, draw in enumerate(samples):
            keep, swap0, swap1 = score_calls[index * 3:index * 3 + 3]
            self.assertEqual(keep[0], swap0[0])
            self.assertEqual(keep[0], swap1[0])
            self.assertEqual(keep[1], obs.board + (draw[-2],))
            self.assertEqual(swap0[1], (draw[-1], obs.board[1], draw[-2]))
            self.assertEqual(swap1[1], (obs.board[0], draw[-1], draw[-2]))

    def test_sample_count_does_not_advance_parent_decision_rng(self):
        for method in ("choose_action", "choose_swap"):
            first, second = random.Random(9), random.Random(9)
            getattr(EquityPolicy(1), method)(observation(), first)
            getattr(EquityPolicy(100), method)(observation(), second)
            self.assertEqual(first.getstate(), second.getstate())


class PolicyTests(unittest.TestCase):
    def test_call_decision_uses_actual_all_in_amount(self):
        obs = observation(to_call=20, stacks=(5, 50, 50), can_raise=False)
        with patch("simulation.persistent_policy.estimate_payouts", return_value=PayoutEstimate((6,), 6, 30, 1/3)) as estimator:
            self.assertEqual(EquityPolicy().choose_action(obs, random.Random(1)), Action("call"))
        self.assertEqual(estimator.call_args.args[-1], 5)
        with patch("simulation.persistent_policy.estimate_payouts", return_value=PayoutEstimate((4,), 4, 30, 1/3)):
            self.assertEqual(EquityPolicy().choose_action(obs, random.Random(1)), Action("fold"))

    def test_strong_hand_raises_half_pot_subject_to_bounds(self):
        obs = observation(to_call=4, min_raise=8, max_add=38)
        with patch("simulation.persistent_policy.estimate_payouts", return_value=PayoutEstimate((30,), 30, 34, 1/3)):
            self.assertEqual(EquityPolicy().choose_action(obs, random.Random(1)), Action("raise", 21))
            self.assertEqual(EquityPolicy().choose_action(replace(obs, max_add=15), random.Random(1)), Action("raise", 15))
            self.assertEqual(EquityPolicy(aggression=0).choose_action(obs, random.Random(1)), Action("call"))
            self.assertEqual(EquityPolicy().choose_action(replace(obs, can_raise=False), random.Random(1)), Action("call"))

    def test_only_all_in_can_raise_short_of_full_minimum(self):
        obs = observation(to_call=4, min_raise=12, max_add=8, stacks=(8, 50, 50))
        with patch("simulation.persistent_policy.estimate_payouts", return_value=PayoutEstimate((30,), 30, 34, 1/3)):
            self.assertEqual(EquityPolicy().choose_action(obs, random.Random(1)), Action("raise", 8))
            self.assertEqual(EquityPolicy().choose_action(replace(obs, stacks=(50, 50, 50)), random.Random(1)), Action("call"))

    def test_swap_requires_full_fee_and_deducts_it_from_total_return(self):
        with patch("simulation.persistent_policy.estimate_swaps", return_value=SwapEstimate(10, (12, 13), (1, 2))) as estimator:
            self.assertIsNone(EquityPolicy().choose_swap(observation(swap_fee=3), random.Random(1)))
            self.assertEqual(EquityPolicy().choose_swap(observation(swap_fee=2), random.Random(1)), 1)
            estimator.reset_mock()
            self.assertIsNone(EquityPolicy().choose_swap(observation(swap_fee=6, stacks=(5, 50, 50)), random.Random(1)))
            estimator.assert_not_called()

    def test_invalid_policy_parameters(self):
        for kwargs in ({"samples": 0}, {"samples": True}, {"aggression": -1},
                       {"aggression": float("nan")}, {"swap_margin": -0.1}):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                EquityPolicy(**kwargs)


if __name__ == "__main__":
    unittest.main()
