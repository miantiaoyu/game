"""Public-information-only benchmark policies for 暗潮航线.

The Monte Carlo model treats opponents' unknown hands as uniformly distributed.
It does not infer a hand range from their bets or swaps. Swap values complete
the public board and then settle: future betting, opponents' subsequent swaps, and the value
of winning by making opponents fold are deliberately not modeled.
"""

from __future__ import annotations

import random

from .model import DECK, Card, Observation


STRATEGY_NAMES = ("calling_station", "tight", "equity", "bluffer", "navigator")
LABELS = {
    "calling_station": "跟注到底",
    "tight": "谨慎保守",
    "equity": "概率决策",
    "bluffer": "主动诈唬",
    "navigator": "概率改航",
}

# Explicit heuristic choices, not fitted or optimized against these simulations.
# Raise thresholds also depend on the neutral 1 / active_players showdown share.
THRESHOLDS = {
    "equity_raise_floor": 0.45,
    "equity_raise_over_fair_share": 0.18,
    "tight_call_odds_margin": 0.08,
    "tight_call_fair_share_multiplier": 0.90,
    "tight_raise_floor": 0.60,
    "tight_raise_over_fair_share": 0.25,
    "bluffer_raise_floor": 0.32,
    "bluffer_raise_over_fair_share": 0.10,
    "bluff_probability": 0.08,
    "swap_min_value_gain": 0.10,
}


def _unknown_cards(obs: Observation) -> tuple[Card, ...]:
    visible = set(obs.hand) | set(obs.board) | set(obs.discarded)
    # Folded players' private cards remain unknown. Integrating over their
    # possible holdings leaves this same uniform marginal draw distribution.
    return tuple(card for card in DECK if card not in visible)


def _showdown_share(
    hand: tuple[Card, Card],
    board: tuple[Card, ...],
    opponent_cards: list[Card],
) -> float:
    """Expected pot fraction for this deal, sharing ties equally."""
    color_totals = [0, 0, 0, 0]
    for color, value in board:
        color_totals[color] += value
    our_score = max(value + color_totals[color] for color, value in hand)
    ties = 1
    for offset in range(0, len(opponent_cards), 2):
        first_color, first_value = opponent_cards[offset]
        second_color, second_value = opponent_cards[offset + 1]
        other_score = max(
            first_value + color_totals[first_color],
            second_value + color_totals[second_color],
        )
        if other_score > our_score:
            return 0.0
        if other_score == our_score:
            ties += 1
    return 1.0 / ties


def estimate_equity(
    obs: Observation, rng: random.Random, samples: int = 96
) -> float:
    """Estimate showdown pot share using an independent Monte Carlo RNG.

    This is tie-adjusted showdown equity, not probability of a sole win.
    ``rng`` is dedicated to this estimate; Strategy creates it from one fixed
    getrandbits(64) draw so sample count does not advance its behavior RNG.
    """
    if samples < 1:
        raise ValueError("samples must be positive")
    if obs.active_players <= 1:
        return 1.0
    unknown = _unknown_cards(obs)
    opponent_count = 2 * (obs.active_players - 1)
    future_count = 3 - len(obs.board)
    total = 0.0
    for _ in range(samples):
        drawn = rng.sample(unknown, opponent_count + future_count)
        opponents = drawn[:opponent_count]
        final_board = obs.board + tuple(drawn[opponent_count:])
        total += _showdown_share(obs.hand, final_board, opponents)
    return total / samples


def estimate_swap_equities(
    obs: Observation, rng: random.Random, samples: int = 96
) -> tuple[float, tuple[float, ...]]:
    """Estimate keeping and replacing each public card with paired samples.

    Each sample uses the same hypothetical opponents and remaining public cards
    for every option, and a single additional draw for the replacement. The
    outgoing public card remains excluded from the unknown deck, so it can
    never be redrawn. There is no access to the actual deck or private hands.
    """
    if samples < 1:
        raise ValueError("samples must be positive")
    unknown = _unknown_cards(obs)
    opponent_count = 2 * (obs.active_players - 1)
    future_count = 3 - len(obs.board)
    keep_total = 0.0
    swap_totals = [0.0] * len(obs.board)
    for _ in range(samples):
        drawn = rng.sample(unknown, opponent_count + future_count + 1)
        opponents = drawn[:opponent_count]
        future = tuple(drawn[opponent_count : opponent_count + future_count])
        replacement = drawn[-1]
        keep_total += _showdown_share(obs.hand, obs.board + future, opponents)
        for index in range(len(obs.board)):
            changed_board = (
                obs.board[:index] + (replacement,) + obs.board[index + 1 :] + future
            )
            swap_totals[index] += _showdown_share(obs.hand, changed_board, opponents)
    return keep_total / samples, tuple(total / samples for total in swap_totals)


class Strategy:
    """Stateless policy; all persistent randomness belongs to the caller."""

    def __init__(
        self, name: str, samples: int = 96, swaps_enabled: bool = True
    ) -> None:
        if name not in STRATEGY_NAMES:
            raise ValueError(f"Unknown strategy: {name}")
        if samples < 1:
            raise ValueError("samples must be positive")
        self.name = name
        self.samples = samples
        self.swaps_enabled = swaps_enabled

    def choose_action(self, obs: Observation, rng: random.Random) -> str:
        passive_action = "call" if obs.to_call else "check"
        if self.name == "calling_station":
            return passive_action

        mc_rng = random.Random(rng.getrandbits(64))
        equity = estimate_equity(obs, mc_rng, self.samples)
        fair_share = 1.0 / obs.active_players
        pot_odds = obs.to_call / (obs.pot + obs.to_call) if obs.to_call else 0.0
        can_raise = obs.raises_left > 0 and obs.chips >= obs.to_call + 1

        if self.name == "tight":
            raise_threshold = max(
                THRESHOLDS["tight_raise_floor"],
                fair_share + THRESHOLDS["tight_raise_over_fair_share"],
            )
            call_threshold = max(
                pot_odds + THRESHOLDS["tight_call_odds_margin"],
                fair_share * THRESHOLDS["tight_call_fair_share_multiplier"],
            )
        elif self.name == "bluffer":
            raise_threshold = max(
                THRESHOLDS["bluffer_raise_floor"],
                fair_share + THRESHOLDS["bluffer_raise_over_fair_share"],
            )
            call_threshold = pot_odds
        else:
            # navigator and equity intentionally use identical betting logic.
            raise_threshold = max(
                THRESHOLDS["equity_raise_floor"],
                fair_share + THRESHOLDS["equity_raise_over_fair_share"],
            )
            call_threshold = pot_odds

        if can_raise and equity >= raise_threshold:
            return "raise"
        if (
            self.name == "bluffer"
            and can_raise
            and rng.random() < THRESHOLDS["bluff_probability"]
        ):
            return "raise"
        if obs.to_call and (obs.chips < obs.to_call or equity < call_threshold):
            return "fold"
        return passive_action

    def choose_swap(self, obs: Observation, rng: random.Random) -> int | None:
        if (
            self.name != "navigator"
            or not self.swaps_enabled
            or obs.chips < obs.swap_cost
            or len(obs.board) != 2
            or obs.active_players <= 1
        ):
            return None
        mc_rng = random.Random(rng.getrandbits(64))
        keep_equity, swap_equities = estimate_swap_equities(obs, mc_rng, self.samples)
        keep_value = keep_equity * obs.pot
        swap_values = tuple(
            equity * (obs.pot + obs.swap_cost) - obs.swap_cost
            for equity in swap_equities
        )
        best_index = max(range(len(swap_values)), key=swap_values.__getitem__)
        if swap_values[best_index] > keep_value + THRESHOLDS["swap_min_value_gain"]:
            return best_index
        return None
