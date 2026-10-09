"""Check sequential replacement, overwrite attribution, and order controls."""

from collections import deque
import unittest

from simulation.game import play_voyage


class ScriptedPolicy:
    name = "scripted"

    def __init__(self, swap=None, actions=()):
        self.swap = swap
        self.actions = deque(actions)
        self.swap_observations = []

    def choose_action(self, obs, rng):
        if self.actions:
            return self.actions.popleft()
        return "call" if obs.to_call else "check"

    def choose_swap(self, obs, rng):
        self.swap_observations.append(obs)
        return self.swap


class MultiSwapTests(unittest.TestCase):
    def test_three_replacements_of_one_slot_attribute_both_overwrites(self):
        players = [ScriptedPolicy(swap=0) for _ in range(3)]
        result = play_voyage(players, trace=True)
        swaps = [event for event in result.events if event["kind"] == "swap"]
        decisions = [event for event in result.events if event["kind"] == "swap_decision"]

        self.assertEqual([m["swap_overwritten"] for m in result.metrics], [1, 1, 0])
        self.assertEqual([m["swaps_overwriting_previous"] for m in result.metrics], [0, 1, 1])
        self.assertEqual([m["window_rank"] for m in result.metrics], [1, 2, 3])
        self.assertEqual([m["window_participants"] for m in result.metrics], [3, 3, 3])
        self.assertEqual([event["pot"] for event in decisions], [3, 5, 7])
        self.assertEqual([event["index"] for event in decisions], [0, 0, 0])
        self.assertEqual([event["rank"] for event in swaps], [1, 2, 3])
        self.assertTrue(all(event["index"] == 0 and event["participants"] == 3 for event in swaps))
        self.assertEqual(swaps[0]["added"], swaps[1]["removed"])
        self.assertEqual(swaps[1]["added"], swaps[2]["removed"])
        self.assertEqual(result.board[0], swaps[2]["added"])
        self.assertEqual(result.discarded, tuple(event["removed"] for event in swaps))
        self.assertEqual(players[1].swap_observations[0].board[0], swaps[0]["added"])
        self.assertEqual(players[2].swap_observations[0].board[0], swaps[1]["added"])
        self.assertEqual(sum(result.scores) + result.burned, 30)

    def test_overwrite_tracking_is_independent_for_the_two_slots(self):
        result = play_voyage(
            [ScriptedPolicy(swap=index) for index in (0, 1, 0, 1, None)], trace=True
        )
        self.assertEqual([m["swap_overwritten"] for m in result.metrics], [1, 1, 0, 0, 0])
        self.assertEqual([m["swaps_overwriting_previous"] for m in result.metrics], [0, 0, 1, 1, 0])
        swaps = [event for event in result.events if event["kind"] == "swap"]
        self.assertEqual(swaps[0]["added"], swaps[2]["removed"])
        self.assertEqual(swaps[1]["added"], swaps[3]["removed"])
        self.assertEqual(result.board[:2], (swaps[2]["added"], swaps[3]["added"]))
        declined = [event for event in result.events if event["kind"] == "swap_decision"][-1]
        self.assertEqual(declined, dict(kind="swap_decision", seat=4, rank=5,
                                       participants=5, index=None, pot=13, cost=2))

    def test_reversing_swap_window_preserves_betting_order(self):
        def run(reverse):
            players = [ScriptedPolicy(swap=seat % 2) for seat in range(4)]
            players[2].actions = deque(["raise"])
            return play_voyage(players, starter=2, deck_seed=9, decision_seed=11,
                               trace=True, reverse_swap_order=reverse)

        forward, reverse = run(False), run(True)
        for stage in range(3):
            action_events = lambda result: [event for event in result.events
                                            if event["kind"] == "action" and event["stage"] == stage]
            self.assertEqual(action_events(forward), action_events(reverse))
        get_order = lambda result: next(event["order"] for event in result.events
                                       if event["kind"] == "swap_window")
        self.assertEqual(get_order(forward), (2, 3, 0, 1))
        self.assertEqual(get_order(reverse), (1, 0, 3, 2))
        self.assertEqual([m["window_rank"] for m in reverse.metrics], [2, 1, 4, 3])

    def test_reverse_window_skips_folded_players_and_compacts_ranks(self):
        players = [ScriptedPolicy(swap=0) for _ in range(5)]
        players[1].actions = deque(["fold"])
        players[3].actions = deque(["fold"])
        result = play_voyage(players, starter=2, trace=True, reverse_swap_order=True)
        order = next(event["order"] for event in result.events if event["kind"] == "swap_window")
        self.assertEqual(order, (0, 4, 2))
        self.assertEqual([m["window_rank"] for m in result.metrics], [1, 0, 3, 0, 2])
        self.assertEqual([m["window_participants"] for m in result.metrics], [3, 0, 3, 0, 3])
        decisions = [event for event in result.events if event["kind"] == "swap_decision"]
        self.assertEqual([event["seat"] for event in decisions], [0, 4, 2])
        self.assertEqual([event["rank"] for event in decisions], [1, 2, 3])
        self.assertEqual(players[1].swap_observations, [])
        self.assertEqual(players[3].swap_observations, [])

    def test_no_swaps_preserve_outcomes_when_window_is_reversed(self):
        for n in (3, 4, 5):
            for seed in range(4):
                with self.subTest(players=n, seed=seed):
                    args = dict(starter=seed % n, deck_seed=seed, decision_seed=seed, trace=True)
                    forward = play_voyage([ScriptedPolicy() for _ in range(n)], **args)
                    reverse = play_voyage([ScriptedPolicy() for _ in range(n)],
                                          reverse_swap_order=True, **args)
                    for field in ("scores", "winners", "burned", "showdown", "pot", "board", "discarded"):
                        self.assertEqual(getattr(forward, field), getattr(reverse, field))
                    for forward_metrics, reverse_metrics in zip(forward.metrics, reverse.metrics):
                        self.assertEqual(
                            {key: value for key, value in forward_metrics.items() if key != "window_rank"},
                            {key: value for key, value in reverse_metrics.items() if key != "window_rank"},
                        )
                    gameplay = lambda result: [event for event in result.events
                                               if event["kind"] not in {"swap_window", "swap_decision"}]
                    self.assertEqual(gameplay(forward), gameplay(reverse))


if __name__ == "__main__":
    unittest.main()
