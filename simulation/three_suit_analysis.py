"""Exact 27-card probabilities; one or two private cards in the final triple."""

from __future__ import annotations

import argparse
from collections import Counter
from itertools import combinations
from math import comb
from pathlib import Path
import csv
import json

DECK = tuple(range(27))
NAMES = ("散牌", "对子", "同花", "顺子", "同花顺", "三条")


def triple_score(cards: tuple[int, ...]) -> tuple[int, ...]:
    values = sorted((card % 9 + 1 for card in cards), reverse=True)
    flush = len({card // 9 for card in cards}) == 1
    distinct = len(set(values))
    if distinct == 1:
        return 5, values[0]
    straight = distinct == 3 and values[0] - values[-1] == 2
    if flush and straight:
        return 4, values[0]
    if straight:
        return 3, values[0]
    if flush:
        return (2, *values)
    if distinct == 2:
        pair = values[1]
        kicker = values[0] if values[0] != pair else values[-1]
        return 1, pair, kicker
    return (0, *values)


def legal_triples(hand: tuple[int, int], board: tuple[int, ...]) -> list[tuple[int, ...]]:
    private = set(hand)
    return [cards for cards in combinations(hand + board, 3) if private.intersection(cards)]


def best_score(hand: tuple[int, int], board: tuple[int, ...]) -> tuple[int, ...]:
    return max(map(triple_score, legal_triples(hand, board)))


def layout_masks(cards: tuple[int, ...], lookup: dict[tuple[int, ...], int]) -> list[int]:
    """Each of the ten public triples determines one private pair.

    For each layout, exclude exactly its public-only triple. Every other triple
    contains one or two private cards, giving nine legal candidates. Category
    multiplicities let us exclude that triple without rescoring nine times.
    """
    categories = [lookup[triple] for triple in combinations(cards, 3)]
    counts = Counter(categories)
    mask = sum(1 << category for category in counts)
    return [mask ^ (1 << category) if counts[category] == 1 else mask for category in categories]


def table_rows(counts: Counter, total: int) -> list[dict]:
    return [
        {"category": NAMES[index], "count": counts[index], "total": total, "probability": counts[index] / total}
        for index in range(5, -1, -1)
    ]


def enumerate_probabilities() -> dict:
    lookup = {triple: triple_score(triple)[0] for triple in combinations(DECK, 3)}
    raw = Counter(lookup.values())
    best = Counter()
    available = Counter()
    mask_counts = Counter()
    for cards in combinations(DECK, 5):
        for mask in layout_masks(cards, lookup):
            mask_counts[mask] += 1
            best[mask.bit_length() - 1] += 1
    for mask, count in mask_counts.items():
        for category in range(6):
            if mask & (1 << category):
                available[category] += count
    total = comb(27, 2) * comb(25, 3)
    assert sum(raw.values()) == comb(27, 3) == 2925
    assert [raw[index] for index in range(6)] == [1848, 648, 231, 168, 21, 9]
    assert sum(best.values()) == sum(mask_counts.values()) == total == 807300
    # A given triple appears in C(24,2) five-card sets. Nine of the ten role
    # assignments permit it. No five-card set can contain two different trips.
    assert available[5] == best[5] == 9 * comb(24, 2) * 9
    return {
        "rules": {
            "suits": 3, "ranks": list(range(1, 10)), "private_cards": 2,
            "public_cards": 3, "final_cards": 3, "minimum_private_used": 1,
            "maximum_private_used": 2, "legal_final_combinations": 9,
            "straight_wraps": False, "strongest_first": list(reversed(NAMES)),
        },
        "method": "Exact enumeration of all unordered 3-card hands and all 2-private/3-public role assignments; no betting, folding, or swapping.",
        "raw_triples": table_rows(raw, 2925),
        "best_legal_triple": table_rows(best, total),
        "category_available": table_rows(available, total),
        "layout_count": total,
        "availability_mask_counts": {str(mask): count for mask, count in sorted(mask_counts.items())},
    }


def write_results(output: Path, result: dict) -> None:
    output.mkdir(parents=True, exist_ok=True)
    (output / "probabilities.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for name in ("raw_triples", "best_legal_triple", "category_available"):
        rows = result[name]
        with (output / f"{name}.csv").open("w", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
            writer.writeheader()
            writer.writerows(rows)
    lines = [
        "# 三花色、1–9：精确牌型概率", "",
        "27 张不同牌，两张底牌、三张公共牌。最后恰好用三张，其中一张或两张必须是底牌；不能直接使用全部三张公共牌。最终有九种合法组合。1 只作最低数字，顺子为 123 至 789，共七段。", "",
        "按随机三张牌的基础稀有度建议：**三条 > 同花顺 > 顺子 > 同花 > 对子 > 散牌**。稀有度是设计依据，不是唯一能采用的强弱规则。", "",
        "## 两个不同口径的精确概率", "",
        "| 牌型 | 随机三张的组合数 | 随机三张概率 | 两底三公选出最强牌型的概率 |",
        "| --- | ---: | ---: | ---: |",
    ]
    for raw, best in zip(result["raw_triples"], result["best_legal_triple"]):
        lines.append(f"| {raw['category']} | {raw['count']} | {raw['probability']:.4%} | {best['probability']:.4%} |")
    lines.extend([
        "", "两列概率各自合计 100%。同花与顺子均排除同花顺。右列按以上强弱顺序选择最优，不能倒过来把选择后的频率当作一套新的强弱排序。", "",
        "## 基础组合公式", "",
        "- 全部三张组合：C(27,3) = 2925。",
        "- 三条：9 × C(3,3) = 9。每个数字仅有三张，必须全部拿到。",
        "- 同花顺：3 花色 × 7 段连续数字 = 21。",
        "- 纯顺子：7 × (3³ − 3) = 168。每个数字可选三种花色，再排除三种同花。",
        "- 纯同花：3 × [C(9,3) − 7] = 231。",
        "- 对子：9 × C(3,2) × 8 × 3 = 648。",
        "- 散牌：2925 减去以上类别 = 1848。", "",
        "## 完整布局枚举", "",
        "两底三公共有 C(27,2) × C(25,3) = 807300 个等概率无序布局；等价于 C(27,5) × C(5,2)。公共牌揭开先后不改变本次完整发牌边际分布。每个布局的十组三张子集中，只排除纯公共牌那一组，因此有九组合法候选。", "",
        "枚举复用了三张牌分类表，并分别统计最终最优牌型和各类是否存在。category_available.csv 的类别可以重叠，不能把该表加总当作 100%；best_legal_triple.csv 的类别互斥。", "",
        "这是无行动的精确发牌分布；玩家改航、弃牌以及共用公共牌会改变实战分布。这不是多人胜率，也没有验证揭牌时序或改航费用的平衡。", "",
        "## 揭牌进程中的已知组合数", "",
        "| 已揭公共牌 | 已知可组成的三张组合 |", "| --- | ---: |",
        "| 1 张 | 1：两张底牌 + 这张公共牌 |",
        "| 2 张 | 4：两底一公有 2 种，一底两公有 2 种 |",
        "| 3 张 | 9：两底一公有 3 种，一底两公有 6 种 |", "",
        "复现：`python3 -m simulation.three_suit_analysis --output reports/three-suit-exact`", "",
    ])
    (output / "report.md").write_text("\n".join(lines), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path("reports/three-suit-exact"))
    args = parser.parse_args()
    result = enumerate_probabilities()
    write_results(args.output, result)
    print(json.dumps({key: result[key] for key in ("raw_triples", "best_legal_triple", "layout_count")}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
