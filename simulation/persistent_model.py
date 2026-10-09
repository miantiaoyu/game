"""Shared public API for the 27-card persistent-bankroll experiment."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol
import random

DECK = tuple(range(27))


@dataclass(frozen=True)
class Pot:
    amount: int
    eligible: tuple[int, ...]


@dataclass(frozen=True)
class Config:
    players: int = 5
    initial_chips: int = 100
    hands: int = 12
    ante_switch: int = 6
    early_ante: int = 1
    late_ante: int = 2
    low_bps: int = 500
    high_bps: int = 1000
    swaps_enabled: bool = True
    reverse_swaps: bool = False


@dataclass(frozen=True)
class Observation:
    player: int
    hand: tuple[int, int]
    board: tuple[int, ...]
    discarded: tuple[int, ...]
    stacks: tuple[int, ...]
    contributions: tuple[int, ...]
    folded: tuple[bool, ...]
    street_contributions: tuple[int, ...]
    ante: int
    street: int
    to_call: int
    # Minimum NEW contribution for a full raise, including the call.
    min_raise: int
    max_add: int
    can_raise: bool
    pots: tuple[Pot, ...]
    bonus_pots: tuple[Pot, ...] = ()
    swap_fee: int = 0
    swap_eligible: tuple[int, ...] = ()
    swap_rank: int = 0
    swap_count: int = 0


@dataclass(frozen=True)
class Action:
    kind: str
    # For kind='raise', amount is NEW chips added in this action, including call.
    amount: int = 0


class Policy(Protocol):
    def choose_action(self, obs: Observation, rng: random.Random) -> Action: ...

    def choose_swap(self, obs: Observation, rng: random.Random) -> int | None: ...
