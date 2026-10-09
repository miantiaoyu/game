"""Rules-level verification of persistent bankroll and frozen fee pots."""

from collections import defaultdict, deque
from copy import deepcopy
import random
import unittest
from unittest.mock import patch

from simulation.persistent_game import (
    _bet, _prepare_hand, _refund_uncalled, _settle,
    finish_window, regular_pots, run_match, window_order,
)
from simulation.persistent_model import Action, Config, DECK, Pot


class ScriptedPolicy:
    def __init__(self, actions=None, swaps=None):
        self.actions = {key: deque(value) for key, value in (actions or {}).items()}
        self.swaps = swaps or {}
        self.observations = defaultdict(list)
        self.swap_observations = []

    def choose_action(self, obs, rng):
        self.observations[obs.player].append(obs)
        queue = self.actions.get((obs.street, obs.player))
        return queue.popleft() if queue else Action("call" if obs.to_call else "check")

    def choose_swap(self, obs, rng):
        self.swap_observations.append(obs)
        return self.swaps.get(obs.player)


def state_at_window(stacks=(100, 100, 100), contributions=(10, 10, 10), folded=None,
                    board=(24, 25), hands=None, ante=1, order=None):
    n = len(stacks)
    hands = hands or [(2 * p, 2 * p + 1) for p in range(n)]
    used = set(board) | {card for hand in hands for card in hand}
    return {
        "hand_index": 1, "start_stacks": [s + c for s, c in zip(stacks, contributions)],
        "stacks": list(stacks), "contributions": list(contributions),
        "start_order": list(range(n)) if order is None else list(order), "window_order": [],
        "hands": hands, "board": list(board), "discarded": [],
        "deck": [card for card in DECK if card not in used],
        "folded": list(folded or [False] * n), "bonus_pots": [],
        "decisions": [], "fees": 0, "all_in_players": {p for p, s in enumerate(stacks) if s == 0},
        "action_count": 0, "raises": 0, "max_action": 0,
        "ante": ante, "trace": True, "actions": [],
        "action_rngs": [random.Random(p) for p in range(n)],
        "swap_rngs": [random.Random(100 + p) for p in range(n)],
    }


class PersistentPotTests(unittest.TestCase):
    def test_short_ante_forms_main_and_side_pot_without_eliminating_player(self):
        state = _prepare_hand(7, 1, [1, 20, 20], 0, ScriptedPolicy(),
                              Config(players=3, early_ante=2), False)
        self.assertEqual(state["contributions"], [1, 2, 2])
        self.assertEqual(regular_pots(state["contributions"], state["folded"]),
                         (Pot(3, (0, 1, 2)), Pot(2, (1, 2))))
        self.assertEqual(len(state["hands"][0]), 2)
        self.assertFalse(state["folded"][0])
        self.assertEqual(window_order(state), [1, 2])
        result = finish_window(state, ScriptedPolicy(), Config(players=3))
        self.assertEqual(sum(result["end_stacks"]), 41)

    def test_main_side_and_fee_pots_use_separate_eligibility(self):
        state = state_at_window(stacks=(0, 20, 20), contributions=(10, 30, 30),
                                hands=[(8, 17), (7, 16), (9, 22)], board=(26, 1, 3))
        # Player 0 has three nines; player 1 beats player 2 in the side pot.
        state["bonus_pots"] = [Pot(4, (1, 2))]
        state["stacks"][1] -= 4
        result = _settle(state)
        self.assertEqual(result["awards"], [30, 44, 0])
        self.assertEqual(result["end_stacks"], [30, 60, 20])
        self.assertEqual(result["pots"][-1]["eligible"], [1, 2])

    def test_odd_chips_follow_start_order_and_are_never_destroyed(self):
        state = state_at_window(stacks=(10, 10, 10), contributions=(1, 1, 1),
                                folded=(False, False, True), order=(1, 2, 0),
                                hands=[(8, 0), (17, 9), (7, 16)], board=(2, 11, 22))
        result = _settle(state)
        self.assertEqual(result["awards"], [1, 2, 0])
        self.assertEqual(sum(result["end_stacks"]), 33)

    def test_dead_contribution_levels_do_not_count_as_distinct_side_pots(self):
        state = state_at_window(contributions=(30, 30, 10), folded=(False, False, True))
        result = _settle(state)
        self.assertFalse(result["had_side_pot"])

    def test_same_eligibility_regular_layers_merge_before_assigning_odd_chips(self):
        state = state_at_window(stacks=(10,) * 5, contributions=(3, 3, 3, 1, 2),
                                folded=(False, False, False, True, True))
        with patch("simulation.persistent_game.best_score", return_value=(0, 9, 8, 7)):
            result = _settle(state)
        self.assertEqual(result["awards"], [4, 4, 4, 0, 0])
        self.assertEqual(len(result["pots"]), 1)
        self.assertEqual(result["pots"][0]["amount"], 12)

    def test_same_eligibility_fees_merge_but_remain_separate_from_regular_pot(self):
        state = state_at_window(stacks=(10,) * 3, contributions=(2,) * 3)
        state["stacks"][0] -= 3
        state["bonus_pots"] = [Pot(1, (0, 1, 2)) for _ in range(3)]
        with patch("simulation.persistent_game.best_score", return_value=(0, 9, 8, 7)):
            result = _settle(state)
        self.assertEqual(result["awards"], [3, 3, 3])
        self.assertEqual([pot["amount"] for pot in result["pots"]], [6, 3])
        self.assertEqual([pot["bonus"] for pot in result["pots"]], [False, True])

    def test_uncalled_refund_uses_folded_players_contributions(self):
        state = state_at_window(stacks=(0, 10, 10), contributions=(30, 20, 10),
                                folded=(False, True, False))
        _refund_uncalled(state)
        self.assertEqual(state["contributions"], [20, 20, 10])
        self.assertEqual(state["stacks"], [10, 10, 10])
        self.assertEqual(regular_pots(state["contributions"], state["folded"]),
                         (Pot(30, (0, 2)), Pot(20, (0,))))


class PersistentBettingTests(unittest.TestCase):
    def test_pending_wager_is_included_in_pot_limit(self):
        state = state_at_window()
        policy = ScriptedPolicy(actions={(2, 0): [Action("raise", 10)]})
        _bet(state, policy, 2)
        view = policy.observations[1][0]
        self.assertEqual(view.to_call, 10)
        self.assertEqual(view.min_raise, 20)
        self.assertEqual(view.max_add, 60)  # 30 old pot + 10 pending + 2*10

    def test_single_short_all_in_does_not_reopen_original_bettor(self):
        state = state_at_window(stacks=(100, 15, 100), contributions=(10, 10, 10))
        policy = ScriptedPolicy(actions={(2, 0): [Action("raise", 10)],
                                         (2, 1): [Action("raise", 15)]})
        _bet(state, policy, 2)
        reopened = policy.observations[0][-1]
        self.assertEqual(reopened.to_call, 5)
        self.assertFalse(reopened.can_raise)
        self.assertEqual(reopened.max_add, 5)

    def test_cumulative_short_all_ins_reopen_when_full_increment_is_faced(self):
        state = state_at_window(stacks=(100, 15, 20, 100), contributions=(10,) * 4)
        policy = ScriptedPolicy(actions={(2, 0): [Action("raise", 10)],
                                         (2, 1): [Action("raise", 15)],
                                         (2, 2): [Action("raise", 20)]})
        _bet(state, policy, 2)
        reopened = policy.observations[0][-1]
        self.assertEqual(reopened.to_call, 10)
        self.assertTrue(reopened.can_raise)
        self.assertEqual(reopened.min_raise, 20)

    def test_check_before_short_open_retains_raise_right_without_completion(self):
        state = state_at_window(stacks=(100, 1, 100), contributions=(10,) * 3)
        policy = ScriptedPolicy(actions={(2, 1): [Action("raise", 1)],
                                         (2, 0): [Action("check"), Action("raise", 3)]})
        _bet(state, policy, 2)
        reopened = policy.observations[0][-1]
        self.assertTrue(reopened.can_raise)
        self.assertEqual(reopened.min_raise, 3)

    def test_illegal_free_fold_and_raise_without_rights_are_rejected(self):
        with self.assertRaises(ValueError):
            _bet(state_at_window(), ScriptedPolicy(actions={(2, 0): [Action("fold")]}), 2)
        state = state_at_window(stacks=(100, 15, 100), contributions=(10,) * 3)
        policy = ScriptedPolicy(actions={(2, 0): [Action("raise", 10), Action("raise", 15)],
                                         (2, 1): [Action("raise", 15)]})
        with self.assertRaises(ValueError):
            _bet(state, policy, 2)

    def test_one_funded_player_cannot_create_uncontested_bet(self):
        state = state_at_window(stacks=(100, 0, 0), contributions=(10,) * 3)
        policy = ScriptedPolicy(actions={(2, 0): [Action("raise", 30)]})
        _bet(state, policy, 2)
        self.assertEqual(dict(policy.observations), {})
        self.assertEqual(state["contributions"], [10, 10, 10])


class PersistentSwapTests(unittest.TestCase):
    def test_quotes_freeze_before_fees_and_passes_and_reversal_changes_rank(self):
        snapshot = state_at_window(stacks=(30,) * 5, contributions=(8,) * 5)
        policy = ScriptedPolicy(swaps={p: 0 for p in range(5)})
        result = finish_window(snapshot, policy)
        self.assertEqual([row["fee"] for row in result["decisions"]], [2, 3, 3, 4, 4])
        self.assertEqual([row["overwritten"] for row in result["decisions"]], [True] * 4 + [False])
        self.assertEqual(result["fees"], 16)
        reversed_result = finish_window(snapshot, ScriptedPolicy(), Config(reverse_swaps=True))
        self.assertEqual(reversed_result["window_order"], [4, 3, 2, 1, 0])
        self.assertEqual([row["fee"] for row in reversed_result["decisions"]], [2, 3, 3, 4, 4])

    def test_inadequate_balance_skips_swap_without_removing_quoted_rank(self):
        snapshot = state_at_window(stacks=(1, 30, 30), contributions=(20,) * 3)
        policy = ScriptedPolicy(swaps={p: 0 for p in range(3)})
        result = finish_window(snapshot, policy)
        self.assertEqual(result["window_order"], [0, 1, 2])
        self.assertEqual([row["fee"] for row in result["decisions"]], [3, 5, 6])
        self.assertFalse(result["decisions"][0]["affordable"])
        self.assertIsNone(result["decisions"][0]["slot"])
        self.assertEqual([view.player for view in policy.swap_observations], [1, 2])

    def test_fee_cannot_unlock_main_only_players_side_pot(self):
        snapshot = state_at_window(stacks=(200, 200, 0), contributions=(40, 40, 10))
        policy = ScriptedPolicy(swaps={0: 0})
        result = finish_window(snapshot, policy)
        self.assertEqual([row["fee"] for row in result["decisions"]], [5, 9])
        self.assertEqual(result["decisions"][0]["eligible"], [0, 1])
        self.assertEqual(result["contributions"], [40, 40, 10])
        self.assertEqual(result["pots"][-1]["eligible"], [0, 1])
        # The bonus 5 does not enter the regular 90-chip pot-limit cap.
        self.assertEqual(policy.observations[0][0].max_add, 90)

    def test_folded_bonus_contender_leaves_winner_without_unlocking_main_only_player(self):
        snapshot = state_at_window(stacks=(20, 20, 0), contributions=(40, 40, 10))
        policy = ScriptedPolicy(swaps={0: 0}, actions={
            (2, 0): [Action("raise", 15)], (2, 1): [Action("fold")],
        })
        result = finish_window(snapshot, policy)
        bonus = [pot for pot in result["pots"] if pot["bonus"]]
        self.assertEqual(bonus, [{"amount": 5, "eligible": [0], "winners": [0], "bonus": True}])
        self.assertEqual(result["contributions"], [40, 40, 10])
        self.assertEqual(sum(result["end_stacks"]), 130)

    def test_exact_fee_all_in_keeps_original_bonus_eligibility(self):
        snapshot = state_at_window(stacks=(3, 20, 20), contributions=(20,) * 3)
        policy = ScriptedPolicy(swaps={0: 0})
        result = finish_window(snapshot, policy)
        self.assertTrue(result["decisions"][0]["affordable"])
        self.assertEqual(result["fees"], 3)
        self.assertEqual(result["all_in_count"], 1)
        self.assertFalse(policy.observations[0])
        self.assertIn(0, result["pots"][-1]["eligible"])

    def test_replaying_snapshot_is_identical_and_does_not_mutate_it(self):
        snapshot = state_at_window()
        before = deepcopy(snapshot)
        first = finish_window(snapshot, ScriptedPolicy(swaps={0: 0}), trace=True)
        second = finish_window(snapshot, ScriptedPolicy(swaps={0: 0}), trace=True)
        self.assertEqual(first, second)
        for key in snapshot:
            if key.endswith("_rngs"):
                self.assertEqual([rng.getstate() for rng in snapshot[key]],
                                 [rng.getstate() for rng in before[key]])
            else:
                self.assertEqual(snapshot[key], before[key])

    def test_fixed_match_conserves_500_chips_and_changes_ante_after_hand_six(self):
        result = run_match(31, ScriptedPolicy(), capture_windows=True, trace=True)
        self.assertEqual(len(result["hands"]), 12)
        self.assertEqual([hand["ante"] for hand in result["hands"]], [1] * 6 + [2] * 6)
        self.assertEqual(sum(result["final_stacks"]), 500)
        for previous, following in zip(result["hands"], result["hands"][1:]):
            self.assertEqual(previous["end_stacks"], following["start_stacks"])
        for snapshot in result["windows"]:
            self.assertEqual(len(snapshot["board"]), 2)
            self.assertEqual(snapshot["fees"], 0)
            self.assertEqual(snapshot["decisions"], [])


if __name__ == "__main__":
    unittest.main()
