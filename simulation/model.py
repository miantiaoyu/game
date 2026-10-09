"""Shared rules and the deliberately limited information given to a policy."""

from dataclasses import dataclass

Card = tuple[int, int]
DECK: tuple[Card, ...] = tuple((color, value) for color in range(4) for value in range(1, 10))


def strength(hand: tuple[Card, Card], board: tuple[Card, ...]) -> int:
    support = [0, 0, 0, 0]
    for color, value in board:
        support[color] += value
    return max(value + support[color] for color, value in hand)


@dataclass(frozen=True)
class Observation:
    hand: tuple[Card, Card]
    board: tuple[Card, ...]
    discarded: tuple[Card, ...]
    active_players: int
    pot: int
    to_call: int
    raises_left: int
    stage: int
    chips: int
    swap_cost: int = 2
