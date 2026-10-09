"""Rules engine. Policy observations never expose opponents or the deck."""

from collections import Counter
from dataclasses import dataclass
import hashlib
import random
from typing import Protocol

from .model import Card, DECK, Observation, strength


def seed_for(seed: int, *parts: object) -> int:
    payload = ":".join(map(str, (seed, *parts))).encode()
    return int.from_bytes(hashlib.blake2b(payload, digest_size=8).digest(), "big")


class Policy(Protocol):
    name: str

    def choose_action(self, obs: Observation, rng: random.Random) -> str: ...

    def choose_swap(self, obs: Observation, rng: random.Random) -> int | None: ...


@dataclass
class VoyageResult:
    scores: tuple[int, ...]
    winners: tuple[int, ...]
    metrics: tuple[Counter, ...]
    burned: int
    showdown: bool
    pot: int
    board: tuple[Card, ...]
    discarded: tuple[Card, ...]
    events: list[dict]


def play_voyage(
    strategies: list[Policy],
    starter: int = 0,
    deck_seed: int = 0,
    decision_seed: int = 0,
    trace: bool = False,
    reverse_swap_order: bool = False,
    swap_costs: tuple[int, ...] | None = None,
    initial_chips: int = 10,
) -> VoyageResult:
    n = len(strategies)
    if not 3 <= n <= 5:
        raise ValueError("The prototype supports 3–5 players")
    if not 0 <= starter < n:
        raise ValueError("Invalid starting seat")
    if swap_costs is None:
        costs = (2,) * n
    else:
        if type(swap_costs) is not tuple or len(swap_costs) < n:
            raise ValueError("swap_costs must be a tuple with at least one cost per player")
        if any(type(cost) is not int or cost <= 0 for cost in swap_costs):
            raise ValueError("swap_costs must contain positive integers")
        costs = swap_costs
    maximum_spend = 1 + 6 + max(costs)
    if type(initial_chips) is not int or initial_chips < maximum_spend:
        raise ValueError("initial_chips must be an integer covering the ante, six bets, and maximum swap cost")
    deck = list(DECK)
    random.Random(deck_seed).shuffle(deck)
    hands = [tuple(deck.pop() for _ in range(2)) for _ in range(n)]
    action_rngs = [random.Random(seed_for(decision_seed, seat, "action")) for seat in range(n)]
    swap_rngs = [random.Random(seed_for(decision_seed, seat, "swap")) for seat in range(n)]
    chips = [initial_chips - 1] * n
    active = set(range(n))
    board: list[Card] = []
    discarded: list[Card] = []
    metrics = [Counter(voyages=1) for _ in range(n)]
    events: list[dict] = []
    pot = n

    def record(kind: str, **data: object) -> None:
        if trace:
            events.append({"kind": kind, **data})

    def observe(seat: int, stage: int, to_call: int = 0, raises_left: int = 2,
                swap_cost: int = 2) -> Observation:
        return Observation(
            hand=hands[seat], board=tuple(board), discarded=tuple(discarded),
            active_players=len(active), pot=pot, to_call=to_call,
            raises_left=raises_left, stage=stage, chips=chips[seat],
            swap_cost=swap_cost,
        )

    def spend(seat: int, amount: int) -> None:
        nonlocal pot
        if amount < 0 or chips[seat] < amount:
            raise AssertionError("Illegal payment")
        chips[seat] -= amount
        pot += amount

    def bet(stage: int) -> None:
        target = 0
        contributions = [0] * n
        pending = set(active)
        cursor = starter
        while pending and len(active) > 1:
            while cursor not in pending:
                cursor = (cursor + 1) % n
            seat = cursor
            cursor = (cursor + 1) % n
            due = target - contributions[seat]
            obs = observe(seat, stage, due, 2 - target)
            if obs.raises_left:
                metrics[seat]["raise_opportunities"] += 1
            action = strategies[seat].choose_action(obs, action_rngs[seat])
            if action not in {"fold", "check", "call", "raise"}:
                raise ValueError(f"Unknown action: {action}")
            if action == "check" and due:
                raise ValueError("Cannot check while owing a call")
            if action == "raise" and target >= 2:
                raise ValueError("Only two table-wide raises per betting stage")
            metrics[seat]["actions"] += 1
            metrics[seat][action] += 1
            record("action", seat=seat, stage=stage, action=action, to_call=due, target=target)
            if action == "fold":
                active.remove(seat)
                pending.remove(seat)
            elif action == "raise":
                target += 1
                spend(seat, target - contributions[seat])
                contributions[seat] = target
                pending = active - {seat}
            else:
                spend(seat, due)
                contributions[seat] = target
                pending.remove(seat)
        if len(active) > 1:
            assert all(contributions[seat] == target for seat in active)

    for stage in range(3):
        board.append(deck.pop())
        record("reveal", stage=stage, card=board[-1])
        if stage == 1:
            window_order = [(starter + offset) % n for offset in range(n)]
            if reverse_swap_order:
                window_order.reverse()
            window_order = tuple(seat for seat in window_order if seat in active)
            participants = len(window_order)
            record("swap_window", order=window_order)
            previous_swappers: dict[int, int] = {}
            for rank, seat in enumerate(window_order, start=1):
                metrics[seat]["swap_opportunities"] += 1
                metrics[seat]["window_rank"] = rank
                metrics[seat]["window_participants"] = participants
                cost = costs[rank - 1]
                index = strategies[seat].choose_swap(observe(seat, stage, swap_cost=cost), swap_rngs[seat])
                if index is not None and (type(index) is not int or not 0 <= index < len(board)):
                    raise ValueError("Invalid swap index")
                record("swap_decision", seat=seat, rank=rank, participants=participants,
                       index=index, pot=pot, cost=cost)
                if index is not None:
                    spend(seat, cost)
                    removed = board[index]
                    discarded.append(removed)
                    board[index] = deck.pop()
                    metrics[seat]["swaps"] += 1
                    metrics[seat]["swap_spent"] += cost
                    if index in previous_swappers:
                        metrics[previous_swappers[index]]["swap_overwritten"] = 1
                        metrics[seat]["swaps_overwriting_previous"] = 1
                    previous_swappers[index] = seat
                    record("swap", seat=seat, removed=removed, added=board[index],
                           index=index, rank=rank, participants=participants, cost=cost)
        bet(stage)
        if len(active) == 1:
            break

    showdown = len(active) > 1
    if showdown:
        strengths = {seat: strength(hands[seat], tuple(board)) for seat in active}
        highest = max(strengths.values())
        winners = tuple(sorted(seat for seat in active if strengths[seat] == highest))
    else:
        winners = tuple(active)
    award, burned = divmod(pot, len(winners))
    for seat in active:
        metrics[seat]["showdowns"] += int(showdown)
    for seat in winners:
        chips[seat] += award
        metrics[seat]["win_share"] += 1 / len(winners)
        metrics[seat]["winning_voyages"] += 1
        metrics[seat]["uncontested_wins"] += int(not showdown)
        metrics[seat]["swapped_win_share"] += metrics[seat]["swaps"] / len(winners)
    for seat in range(n):
        metrics[seat]["points"] += chips[seat]
        metrics[seat]["net_points"] += chips[seat] - initial_chips
    assert sum(chips) + burned == initial_chips * n
    assert all(value >= initial_chips - maximum_spend for value in chips)
    assert len(set(card for hand in hands for card in hand) | set(board) | set(discarded)) == 2 * n + len(board) + len(discarded)
    record("settle", winners=winners, scores=tuple(chips), pot=pot, burned=burned)
    return VoyageResult(tuple(chips), winners, tuple(metrics), burned, showdown, pot, tuple(board), tuple(discarded), events)
