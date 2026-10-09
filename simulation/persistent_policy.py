"""Public-information Monte Carlo policy for persistent-bankroll games.

Opponents' hands are uniform over unseen cards. Each candidate is valued at
showdown with the current pools: future bets, folds, and later board replacements
are not forecast. Fees retain their own eligibility, rather than buying side-pot
rights. Engine-owned action/swap RNGs are advanced by one draw per estimate.
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache
from itertools import combinations, permutations
from math import ceil, isfinite
import random

from .persistent_model import Action, DECK, Observation, Pot
from .three_suit_analysis import triple_score


def encode_score(score: tuple[int, ...]) -> int:
    """Preserve the reference tuple ordering in a four-digit integer."""
    return sum(value * (10 ** (3 - index)) for index, value in enumerate(score))


def _make_triple_lookup() -> tuple[int, ...]:
    lookup = [0] * (27 ** 3)
    for cards in combinations(DECK, 3):
        value = encode_score(triple_score(cards))
        for a, b, c in permutations(cards):
            lookup[a * 729 + b * 27 + c] = value
    return tuple(lookup)


_TRIPLE = _make_triple_lookup()


@lru_cache(maxsize=65536)
def fast_score(hand: tuple[int, int], board: tuple[int, ...]) -> int:
    """Best of nine legal triples, using one or two private cards.

    Inputs must be a two-card hand and a completed three-card board. No sorting,
    combinations, or category evaluation occurs in the Monte Carlo hot path.
    """
    a, b = hand
    c, d, e = board
    lookup = _TRIPLE
    ab = a * 729 + b * 27
    ac = a * 729 + c * 27
    bc = b * 729 + c * 27
    return max(
        lookup[ab + c], lookup[ab + d], lookup[ab + e],
        lookup[ac + d], lookup[ac + e], lookup[a * 729 + d * 27 + e],
        lookup[bc + d], lookup[bc + e], lookup[b * 729 + d * 27 + e],
    )


def pots_after_call(obs: Observation, amount: int) -> tuple[Pot, ...]:
    """Build eligibility from ordinary contributions after a capped call.

    Folded contributions stay in the pool but folded players cannot win it.
    Bonus pools never count toward contribution levels or side-pot eligibility.
    A singleton top tier is guaranteed to its contributor and therefore offsets
    that contributor's unmatched expenditure in an expected-value calculation.
    """
    added = min(max(amount, 0), obs.stacks[obs.player], obs.to_call)
    if not added:
        return obs.pots
    contributions = list(obs.contributions)
    contributions[obs.player] += added
    pools = []
    previous = 0
    for level in sorted(set(contributions) - {0}):
        participants = [seat for seat, value in enumerate(contributions) if value >= level]
        eligible = tuple(seat for seat in participants if not obs.folded[seat])
        pools.append(Pot((level - previous) * len(participants), eligible))
        previous = level
    return tuple(pools)


def _sampling_context(obs: Observation) -> tuple[tuple[int, ...], tuple[int, ...], int]:
    visible = set(obs.hand + obs.board + obs.discarded)
    unknown = tuple(card for card in DECK if card not in visible)
    opponents = tuple(seat for seat, folded in enumerate(obs.folded) if seat != obs.player and not folded)
    return unknown, opponents, 3 - len(obs.board)


def _scores(obs: Observation, opponents: tuple[int, ...], cards: list[int], board: tuple[int, ...]) -> list[int]:
    scores = [-1] * len(obs.stacks)
    scores[obs.player] = fast_score(obs.hand, board)
    for index, seat in enumerate(opponents):
        offset = index * 2
        scores[seat] = fast_score((cards[offset], cards[offset + 1]), board)
    return scores


def _share(scores: list[int], player: int, eligible: tuple[int, ...]) -> float:
    if player not in eligible or scores[player] < 0:
        return 0.0
    ours = scores[player]
    ties = 1
    for seat in eligible:
        if seat == player or scores[seat] < 0:
            continue
        score = scores[seat]
        if score > ours:
            return 0.0
        if score == ours:
            ties += 1
    return 1.0 / ties


@dataclass(frozen=True)
class PayoutEstimate:
    # Includes normal pools followed by existing bonus pools, in that order.
    pot_returns: tuple[float, ...]
    expected_return: float
    accessible_pot: int
    fair_share: float


@dataclass(frozen=True)
class SwapEstimate:
    keep_return: float
    swap_returns: tuple[float, ...]
    fee_recovery: tuple[float, ...]


def estimate_payouts(obs: Observation, rng: random.Random, samples: int = 64,
                     call_amount: int = 0) -> PayoutEstimate:
    """Expected chip awards per pool, including post-call side-pot caps."""
    if type(samples) is not int or samples < 1:
        raise ValueError("samples must be a positive integer")
    pools = pots_after_call(obs, call_amount) + obs.bonus_pots
    unknown, opponents, future_count = _sampling_context(obs)
    opponent_cards = 2 * len(opponents)
    totals = [0.0] * len(pools)
    eligible_pools = [(index, pool) for index, pool in enumerate(pools)
                      if obs.player in pool.eligible and pool.amount > 0]
    for _ in range(samples):
        drawn = rng.sample(unknown, opponent_cards + future_count)
        board = obs.board + tuple(drawn[opponent_cards:])
        scores = _scores(obs, opponents, drawn, board)
        for index, pool in eligible_pools:
            totals[index] += pool.amount * _share(scores, obs.player, pool.eligible)
    returns = tuple(total / samples for total in totals)
    accessible = sum(pool.amount for _, pool in eligible_pools)
    fair_return = sum(pool.amount / sum(not obs.folded[seat] for seat in pool.eligible)
                      for _, pool in eligible_pools)
    return PayoutEstimate(returns, sum(returns), accessible,
                          fair_return / accessible if accessible else 0.0)


def estimate_swaps(obs: Observation, rng: random.Random, samples: int = 64) -> SwapEstimate:
    """Pair keep/swap deals; include the payer's share of the new fee pool.

    The same opponent hands, future public cards, and extra replacement card
    are used across candidates. An outgoing public card remains unavailable.
    """
    if type(samples) is not int or samples < 1:
        raise ValueError("samples must be a positive integer")
    if len(obs.board) != 2:
        raise ValueError("Swap estimates require the two-card swap window")
    unknown, opponents, future_count = _sampling_context(obs)
    opponent_cards = 2 * len(opponents)
    pools = tuple(pool for pool in obs.pots + obs.bonus_pots
                  if obs.player in pool.eligible and pool.amount > 0)
    keep_total = 0.0
    swap_totals = [0.0] * len(obs.board)
    recovery_totals = [0.0] * len(obs.board)
    for _ in range(samples):
        drawn = rng.sample(unknown, opponent_cards + future_count + 1)
        future = tuple(drawn[opponent_cards:opponent_cards + future_count])
        replacement = drawn[-1]
        board = obs.board + future
        scores = _scores(obs, opponents, drawn, board)
        keep_total += sum(pool.amount * _share(scores, obs.player, pool.eligible) for pool in pools)
        for index in range(len(obs.board)):
            changed = obs.board[:index] + (replacement,) + obs.board[index + 1:] + future
            scores = _scores(obs, opponents, drawn, changed)
            recovery = obs.swap_fee * _share(scores, obs.player, obs.swap_eligible)
            recovery_totals[index] += recovery
            swap_totals[index] += recovery + sum(pool.amount * _share(scores, obs.player, pool.eligible) for pool in pools)
    return SwapEstimate(keep_total / samples,
                        tuple(value / samples for value in swap_totals),
                        tuple(value / samples for value in recovery_totals))


class EquityPolicy:
    """Symmetric local-value policy; aggression tunes strong-hand raises.

    aggression=0 disables raises. Higher positive values lower the equity margin
    above a fair split; the raise size remains about half the current pool.
    Changes in stack distribution affect legal amounts, pool eligibility, and
    affordability. The policy does not anticipate later players overwriting a
    replacement and does not model opponents' action-conditioned hand ranges.
    """

    name = "equity"

    def __init__(self, samples: int = 64, aggression: float = 1.0,
                 swap_margin: float = 0.1) -> None:
        if type(samples) is not int or samples < 1:
            raise ValueError("samples must be a positive integer")
        if not isfinite(aggression) or aggression < 0:
            raise ValueError("aggression must be finite and nonnegative")
        if not isfinite(swap_margin) or swap_margin < 0:
            raise ValueError("swap_margin must be finite and nonnegative")
        self.samples = samples
        self.aggression = aggression
        self.swap_margin = swap_margin

    def choose_action(self, obs: Observation, rng: random.Random) -> Action:
        call = min(obs.to_call, obs.stacks[obs.player])
        estimate = estimate_payouts(obs, random.Random(rng.getrandbits(64)), self.samples, call)
        if call and estimate.expected_return < call:
            return Action("fold")
        share = estimate.expected_return / estimate.accessible_pot if estimate.accessible_pot else 0.0
        if self.aggression > 0 and obs.can_raise and obs.max_add > obs.to_call:
            threshold = max(0.45, estimate.fair_share + 0.18 / self.aggression)
            if share >= threshold:
                pool_after_call = sum(pool.amount for pool in obs.pots + obs.bonus_pots) + call
                desired = max(obs.min_raise, call + ceil(pool_after_call * 0.5))
                amount = min(desired, obs.max_add, obs.stacks[obs.player])
                if amount >= obs.min_raise or amount == obs.stacks[obs.player]:
                    return Action("raise", amount)
        return Action("call" if obs.to_call else "check")

    def choose_swap(self, obs: Observation, rng: random.Random) -> int | None:
        if len(obs.board) != 2 or obs.swap_fee <= 0 or obs.stacks[obs.player] < obs.swap_fee:
            return None
        estimate = estimate_swaps(obs, random.Random(rng.getrandbits(64)), self.samples)
        index = max(range(len(estimate.swap_returns)), key=estimate.swap_returns.__getitem__)
        if estimate.swap_returns[index] - obs.swap_fee > estimate.keep_return + self.swap_margin:
            return index
        return None
