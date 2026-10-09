"""Persistent-stack rules engine, with clonable pre-swap counterfactuals.

Only regular contributions establish side-pot eligibility. Swap fees are held
in separate pots whose eligible players are frozen before any swap occurs.
"""

from __future__ import annotations

from copy import deepcopy
import random

from .game import seed_for
from .persistent_model import Action, Config, DECK, Observation, Policy, Pot
from .three_suit_analysis import best_score


def regular_pots(contributions: list[int] | tuple[int, ...], folded: list[bool] | tuple[bool, ...]) -> tuple[Pot, ...]:
    """Layer all contributions, including dead money from folded players."""
    previous = 0
    pots = []
    for level in sorted(set(contributions) - {0}):
        contributors = [p for p, amount in enumerate(contributions) if amount >= level]
        amount = (level - previous) * len(contributors)
        eligible = tuple(p for p in contributors if not folded[p])
        pots.append(Pot(amount, eligible))
        previous = level
    return tuple(pots)


def _event(state: dict, kind: str, **values: object) -> None:
    if state["trace"]:
        state["actions"].append({"kind": kind, **values})


def _live(state: dict) -> list[int]:
    return [p for p in state["start_order"] if not state["folded"][p]]


def _contested(state: dict) -> bool:
    return any(len(pot.eligible) >= 2 for pot in regular_pots(state["contributions"], state["folded"]))


def _pay(state: dict, player: int, amount: int, regular: bool = True) -> None:
    if type(amount) is not int or amount < 0 or amount > state["stacks"][player]:
        raise ValueError("Invalid payment")
    state["stacks"][player] -= amount
    if regular:
        state["contributions"][player] += amount
    if not state["stacks"][player]:
        state["all_in_players"].add(player)


def _refund_uncalled(state: dict) -> None:
    amounts = state["contributions"]
    maximum = max(amounts)
    leaders = [p for p, amount in enumerate(amounts) if amount == maximum]
    if maximum and len(leaders) == 1:
        player = leaders[0]
        second = max((amount for p, amount in enumerate(amounts) if p != player), default=0)
        refund = maximum - second
        if state["folded"][player]:
            raise AssertionError("A folded player cannot own unmatched excess")
        amounts[player] -= refund
        state["stacks"][player] += refund
        _event(state, "refund", player=player, amount=refund)


def _pot_limit_pool(state: dict) -> int:
    """Keep pending wagers in P; only exclude genuinely locked regular layers.

    A player with chips may still respond to a currently unmatched layer. Its
    provisional one-person eligibility must not remove that wager from P.
    """
    responders = {p for p in _live(state) if state["stacks"][p] > 0}
    return sum(pot.amount for pot in regular_pots(state["contributions"], state["folded"])
               if len(set(pot.eligible) | responders) >= 2)


def _observation(state: dict, player: int, street: int, *, street_contributions: list[int] | None = None,
                 to_call: int = 0, min_raise: int = 0, max_add: int = 0,
                 can_raise: bool = False, fee: int = 0, eligible: tuple[int, ...] = (),
                 rank: int = 0, count: int = 0) -> Observation:
    return Observation(
        player=player, hand=state["hands"][player], board=tuple(state["board"]),
        discarded=tuple(state["discarded"]), stacks=tuple(state["stacks"]),
        contributions=tuple(state["contributions"]), folded=tuple(state["folded"]),
        street_contributions=tuple(street_contributions or [0] * len(state["stacks"])),
        ante=state["ante"], street=street, to_call=to_call, min_raise=min_raise,
        max_add=max_add, can_raise=can_raise,
        pots=regular_pots(state["contributions"], state["folded"]),
        bonus_pots=tuple(state["bonus_pots"]), swap_fee=fee,
        swap_eligible=eligible, swap_rank=rank, swap_count=count,
    )


def _bet(state: dict, policy: Policy, street: int) -> None:
    n = len(state["stacks"])
    contributions = [0] * n
    last_target = [0] * n
    committed_action = [False] * n
    target = 0
    last_full = 2 * state["ante"]
    pending = {p for p in _live(state) if state["stacks"][p] > 0}
    order = state["start_order"]
    cursor = 0
    while pending and len(_live(state)) > 1:
        while order[cursor] not in pending:
            cursor = (cursor + 1) % len(order)
        player = order[cursor]
        cursor = (cursor + 1) % len(order)
        due = target - contributions[player]
        others_can_respond = any(p != player and state["stacks"][p] > 0 for p in _live(state))
        if not others_can_respond and due == 0:
            pending.remove(player)
            continue
        rights = not committed_action[player] or target - last_target[player] >= last_full
        stack = state["stacks"][player]
        max_add = min(stack, _pot_limit_pool(state) + 2 * due) if rights and others_can_respond else min(stack, due)
        can_raise = rights and others_can_respond and max_add > due
        # A below-minimum increase is legal only when it uses every last chip.
        minimum = due + last_full
        if max_add < minimum and max_add != stack:
            can_raise = False
            max_add = min(stack, due)
        view = _observation(state, player, street, street_contributions=contributions,
                            to_call=due, min_raise=minimum, max_add=max_add,
                            can_raise=can_raise)
        action = policy.choose_action(view, state["action_rngs"][player])
        if not isinstance(action, Action):
            raise ValueError("Policy must return Action")
        if action.kind == "fold":
            if due == 0:
                raise ValueError("Cannot fold when checking is free")
            state["folded"][player] = True
            pending.remove(player)
            added = 0
        elif action.kind in {"check", "call"}:
            if action.kind == "check" and due:
                raise ValueError("Cannot check facing a bet")
            added = min(stack, due)
            _pay(state, player, added)
            contributions[player] += added
            pending.remove(player)
            if due:
                committed_action[player] = True
                last_target[player] = target
        elif action.kind == "raise":
            added = action.amount
            if type(added) is not int or not can_raise or added <= due or added > max_add:
                raise ValueError("Raise exceeds rights, stack, or pot limit")
            increment = added - due
            if increment < last_full and added != stack:
                raise ValueError("An incomplete raise must be all-in")
            _pay(state, player, added)
            contributions[player] += added
            target += increment
            if increment >= last_full:
                last_full = increment
            committed_action[player] = True
            last_target[player] = target
            pending = {p for p in _live(state) if p != player and state["stacks"][p] > 0}
        else:
            raise ValueError(f"Unknown action: {action.kind}")
        state["action_count"] += 1
        state["raises"] += action.kind == "raise"
        state["max_action"] = max(state["max_action"], added)
        _event(state, "action", street=street, player=player, action=action.kind,
               amount=added, to_call=due, target=target, min_raise=minimum,
               max_add=max_add, can_raise=can_raise)
    _refund_uncalled(state)


def _reveal(state: dict) -> None:
    state["board"].append(state["deck"].pop())
    _event(state, "reveal", card=state["board"][-1], number=len(state["board"]))


def window_order(snapshot: dict) -> list[int]:
    """Quoted-window membership is independent of prices and affordability."""
    pots = regular_pots(snapshot["contributions"], snapshot["folded"])
    return [p for p in snapshot["start_order"]
            if not snapshot["folded"][p] and snapshot["stacks"][p] > 0
            and any(p in pot.eligible and len(pot.eligible) >= 2 for pot in pots)]


def _settle(state: dict) -> dict:
    def merge_eligibility(pots: tuple[Pot, ...] | list[Pot]) -> list[Pot]:
        totals: dict[tuple[int, ...], int] = {}
        for pot in pots:
            eligible = tuple(sorted(pot.eligible))
            totals[eligible] = totals.get(eligible, 0) + pot.amount
        return [Pot(amount, eligible) for eligible, amount in totals.items()]

    # Artificial contribution breakpoints and the number of fee payments must
    # not multiply the odd-chip advantage. Keep regular and fee ledgers apart.
    regular = merge_eligibility(regular_pots(state["contributions"], state["folded"]))
    bonus = merge_eligibility([
        Pot(pot.amount, tuple(p for p in pot.eligible if not state["folded"][p]))
        for pot in state["bonus_pots"]
    ])
    pots = regular + bonus
    showdown = any(len(pot.eligible) >= 2 for pot in pots)
    if showdown:
        while len(state["board"]) < 3:
            _reveal(state)
        strengths = {p: best_score(state["hands"][p], tuple(state["board"])) for p in _live(state)}
    else:
        strengths = {}
    awards = [0] * len(state["stacks"])
    settlement = []
    for index, pot in enumerate(pots):
        if not pot.eligible:
            raise AssertionError("Orphan pot")
        winners = list(pot.eligible)
        if len(winners) > 1:
            highest = max(strengths[p] for p in winners)
            winners = [p for p in state["start_order"] if p in winners and strengths[p] == highest]
        share, remainder = divmod(pot.amount, len(winners))
        for offset, player in enumerate(winners):
            awards[player] += share + (offset < remainder)
        settlement.append({"amount": pot.amount, "eligible": list(pot.eligible),
                           "winners": winners, "bonus": index >= len(regular)})
    for player, amount in enumerate(awards):
        state["stacks"][player] += amount
    assert sum(state["stacks"]) == sum(state["start_stacks"])
    assert all(stack >= 0 for stack in state["stacks"])
    result = {
        "hand_index": state["hand_index"], "ante": state["ante"],
        "start_stacks": list(state["start_stacks"]), "end_stacks": list(state["stacks"]),
        "start_order": list(state["start_order"]), "window_order": list(state["window_order"]),
        "decisions": state["decisions"], "fees": state["fees"],
        "had_side_pot": len({pot.eligible for pot in regular}) > 1, "all_in_count": len(state["all_in_players"]),
        "showdown": showdown, "fold_count": sum(state["folded"][p] for p in state["start_order"]),
        "action_count": state["action_count"], "raises": state["raises"], "max_action": state["max_action"],
        "regular_pot": sum(state["contributions"]), "awards": awards,
        "contributions": list(state["contributions"]), "pots": settlement,
        "board": list(state["board"]), "discarded": list(state["discarded"]),
    }
    if state["trace"]:
        result["actions"] = state["actions"]
        result["hands"] = state["hands"]
    return result


def finish_window(snapshot: dict, policy: Policy, config: Config = Config(), trace: bool = False) -> dict:
    """Finish a cloned state; the supplied snapshot and random streams survive."""
    state = deepcopy(snapshot)
    state["trace"] = trace
    order = window_order(state)
    if config.reverse_swaps:
        order.reverse()
    state["window_order"] = order
    regular = regular_pots(state["contributions"], state["folded"])
    quotes = []
    count = len(order)
    for rank, player in enumerate(order, 1):
        eligible_pots = [pot for pot in regular if player in pot.eligible and len(pot.eligible) >= 2]
        exposure = sum(pot.amount for pot in eligible_pots)
        if count == 1:
            numerator, denominator = config.high_bps, 10000
        else:
            numerator = config.low_bps * (count - 1) + (config.high_bps - config.low_bps) * (rank - 1)
            denominator = 10000 * (count - 1)
        fee = max(state["ante"], (exposure * numerator + denominator - 1) // denominator)
        quotes.append((player, rank, fee, exposure, eligible_pots[-1].eligible))
    previous = {}
    for player, rank, fee, exposure, eligible in quotes:
        affordable = state["stacks"][player] >= fee
        slot = None
        if config.swaps_enabled and affordable:
            view = _observation(state, player, 1, fee=fee, eligible=eligible, rank=rank, count=count)
            slot = policy.choose_swap(view, state["swap_rngs"][player])
            if slot is not None and (type(slot) is not int or slot not in (0, 1)):
                raise ValueError("Swap must select one of the two public slots")
        decision = {"player": player, "rank": rank, "count": count, "fee": fee,
                    "exposure": exposure, "eligible": list(eligible), "affordable": affordable,
                    "slot": slot, "overwritten": False}
        state["decisions"].append(decision)
        if slot is not None:
            _pay(state, player, fee, regular=False)
            state["fees"] += fee
            state["bonus_pots"].append(Pot(fee, eligible))
            removed = state["board"][slot]
            state["discarded"].append(removed)
            state["board"][slot] = state["deck"].pop()
            if slot in previous:
                previous[slot]["overwritten"] = True
            previous[slot] = decision
            _event(state, "swap", player=player, rank=rank, fee=fee, slot=slot,
                   removed=removed, added=state["board"][slot], eligible=list(eligible))
    if _contested(state):
        _reveal(state)
        _bet(state, policy, 2)
    return _settle(state)


def _prepare_hand(seed: int, hand_index: int, stacks: list[int], button: int,
                  policy: Policy, config: Config, trace: bool) -> dict:
    n = len(stacks)
    order = [(button + step) % n for step in range(n) if stacks[(button + step) % n] > 0]
    deck = list(DECK)
    random.Random(seed_for(seed, hand_index, "deck")).shuffle(deck)
    hands = [() for _ in range(n)]
    for player in range(n):
        if stacks[player] > 0:
            hands[player] = (deck.pop(), deck.pop())
    state = {
        "hand_index": hand_index, "start_stacks": list(stacks), "stacks": list(stacks),
        "start_order": order, "window_order": [], "deck": deck, "hands": hands,
        "board": [], "discarded": [], "contributions": [0] * n,
        "folded": [stack == 0 for stack in stacks], "bonus_pots": [],
        "decisions": [], "fees": 0, "all_in_players": set(),
        "action_count": 0, "raises": 0, "max_action": 0,
        "ante": config.early_ante if hand_index <= config.ante_switch else config.late_ante,
        "trace": trace, "actions": [],
        "action_rngs": [random.Random(seed_for(seed, hand_index, p, "action")) for p in range(n)],
        "swap_rngs": [random.Random(seed_for(seed, hand_index, p, "swap")) for p in range(n)],
    }
    for player in order:
        amount = min(stacks[player], state["ante"])
        _pay(state, player, amount)
        _event(state, "ante", player=player, amount=amount)
    for street in (0, 1):
        if not _contested(state):
            break
        _reveal(state)
        _bet(state, policy, street)
    return state


def run_match(seed: int, policy: Policy, config: Config = Config(), initial_button: int = 0,
              capture_windows: bool = False, trace: bool = False) -> dict:
    if not 2 <= config.players <= 5 or config.initial_chips < 1 or config.hands < 1:
        raise ValueError("Invalid match size or bankroll")
    if not 0 <= initial_button < config.players or min(config.early_ante, config.late_ante) < 1:
        raise ValueError("Invalid button or ante")
    if not 0 <= config.low_bps <= config.high_bps:
        raise ValueError("Invalid swap fee schedule")
    stacks = [config.initial_chips] * config.players
    button = initial_button
    hands = []
    windows = []
    for hand_index in range(1, config.hands + 1):
        if sum(stack > 0 for stack in stacks) < 2:
            break
        snapshot = _prepare_hand(seed, hand_index, stacks, button, policy, config, trace)
        if capture_windows and window_order(snapshot):
            windows.append(snapshot)
        result = finish_window(snapshot, policy, config, trace)
        hands.append(result)
        stacks = result["end_stacks"]
        for step in range(1, config.players + 1):
            candidate = (button + step) % config.players
            if stacks[candidate] > 0:
                button = candidate
                break
    return {"seed": seed, "final_stacks": stacks, "hands": hands, "windows": windows}
