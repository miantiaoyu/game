"""Card-type frequency probe, separate from policies and the game engine.

This does not simulate betting, swaps, opponent inference, or win rates. It
enumerates unconditioned three/five-card deals and samples uniformly dealt
two-private/three-public layouts to measure the effect of choosing a triple.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import random
from collections import Counter
from itertools import combinations
from pathlib import Path

Card = tuple[int, int]
DECK = tuple((color, value) for color in range(4) for value in range(1, 10))
THREE_NAMES = ("散牌", "对子", "顺子", "同花", "三条", "同花顺")
# Candidate A uses the familiar full-house-over-flush order for comparison.
# Its category frequencies do not depend on that ordering: all five are used.
FIVE_NAMES = ("散牌", "对子", "两对", "三条", "顺子", "同花", "葫芦", "四条", "同花顺")


def triple_score(cards: tuple[Card, ...], private: Card | None = None) -> tuple[int, ...]:
    """Rank a triple; if private is supplied, a pair must include that card."""
    values = sorted((card[1] for card in cards), reverse=True)
    counts = Counter(values)
    flush = len({card[0] for card in cards}) == 1
    straight = len(counts) == 3 and values[0] - values[-1] == 2
    if straight and flush:
        return (5, values[0])
    if len(counts) == 1:
        return (4, values[0])
    if flush:
        return (3, *values)
    if straight:
        return (2, values[0])
    if len(counts) == 2:
        pair = next(value for value, count in counts.items() if count == 2)
        if private is None or private[1] == pair:
            kicker = next(value for value, count in counts.items() if count == 1)
            return (1, pair, kicker)
    return (0, *values)


def five_score(cards: tuple[Card, ...]) -> tuple[int, ...]:
    """Candidate A: use all five cards, with no wild or wrapping ranks."""
    values = sorted((card[1] for card in cards), reverse=True)
    counts = Counter(values)
    groups = sorted(((count, value) for value, count in counts.items()), reverse=True)
    flush = len({card[0] for card in cards}) == 1
    straight = len(counts) == 5 and values[0] - values[-1] == 4
    if straight and flush:
        return (8, values[0])
    if groups[0][0] == 4:
        return (7, groups[0][1], groups[1][1])
    if groups[0][0] == 3 and groups[1][0] == 2:
        return (6, groups[0][1], groups[1][1])
    if flush:
        return (5, *values)
    if straight:
        return (4, values[0])
    if groups[0][0] == 3:
        return (3, groups[0][1], *sorted((v for v, n in counts.items() if n == 1), reverse=True))
    pairs = sorted((v for v, n in counts.items() if n == 2), reverse=True)
    if len(pairs) == 2:
        return (2, *pairs, next(v for v, n in counts.items() if n == 1))
    if pairs:
        return (1, pairs[0], *sorted((v for v, n in counts.items() if n == 1), reverse=True))
    return (0, *values)


def best_private_triple(hand: tuple[Card, ...], board: tuple[Card, ...], anchored_pair: bool = False) -> tuple[int, ...]:
    return max(triple_score((private, *public), private if anchored_pair else None) for private in hand for public in combinations(board, 2))


def rows(names: tuple[str, ...], counts: Counter[int], total: int) -> list[dict]:
    return [
        {"category": name, "count": counts[index], "total": total, "frequency": counts[index] / total}
        for index, name in reversed(list(enumerate(names)))
    ]


def markdown_table(data: list[dict]) -> str:
    lines = ["| 牌型 | 次数 | 比例 |", "| --- | ---: | ---: |"]
    lines.extend(f"| {row['category']} | {row['count']:,} | {row['frequency']:.4%} |" for row in data)
    return "\n".join(lines)


def probe(samples: int, seed: int) -> dict:
    if samples <= 0:
        raise ValueError("samples must be positive")
    raw_three: Counter[int] = Counter()
    tagged_three: Counter[int] = Counter()
    for cards in combinations(DECK, 3):
        raw_three[triple_score(cards)[0]] += 1
        for private in cards:
            tagged_three[triple_score(cards, private)[0]] += 1
    raw_five: Counter[int] = Counter(five_score(cards)[0] for cards in combinations(DECK, 5))
    rng = random.Random(seed)
    sampled_best: Counter[int] = Counter()
    sampled_anchored: Counter[int] = Counter()
    sampled_five: Counter[int] = Counter()
    public_pair_or_trip_only = 0
    for _ in range(samples):
        cards = tuple(rng.sample(DECK, 5))
        hand, board = cards[:2], cards[2:]
        sampled_best[best_private_triple(hand, board)[0]] += 1
        sampled_anchored[best_private_triple(hand, board, anchored_pair=True)[0]] += 1
        five = five_score(cards)
        sampled_five[five[0]] += 1
        # Cases in which A's category-defining pair/triple is entirely public.
        if five[0] in (1, 3):
            defining_value = five[1]
            if all(card[1] != defining_value for card in hand):
                public_pair_or_trip_only += 1

    assert sum(raw_three.values()) == math.comb(36, 3) == 7140
    assert [raw_three[i] for i in range(6)] == [4620, 1728, 420, 308, 36, 28]
    assert sum(tagged_three.values()) == 3 * math.comb(36, 3) == 21420
    assert sum(raw_five.values()) == math.comb(36, 5) == 376992
    assert [raw_five[i] for i in range(9)] == [123420, 193536, 36288, 16128, 5100, 484, 1728, 288, 20]
    assert sum(sampled_best.values()) == samples
    assert sum(sampled_anchored.values()) == samples
    assert sum(sampled_five.values()) == samples
    # Low private cards can beat high cards by creating a straight or trips.
    assert triple_score(((0, 1), (1, 2), (2, 3)), (0, 1)) > triple_score(((0, 9), (1, 2), (2, 3)), (0, 9))
    assert triple_score(((0, 2), (1, 2), (2, 2)), (0, 2)) > triple_score(((0, 9), (1, 2), (2, 2)), (0, 9))
    assert triple_score(((0, 9), (1, 2), (2, 2)), (0, 9))[0] == 0
    return {
        "seed": seed,
        "samples": samples,
        "method": "exact base combinations; uniform 2-private/3-public deals without actions",
        "raw_three_exact": rows(THREE_NAMES, raw_three, sum(raw_three.values())),
        "anchored_one_private_two_public_exact": rows(THREE_NAMES, tagged_three, sum(tagged_three.values())),
        "five_all_exact": rows(FIVE_NAMES, raw_five, sum(raw_five.values())),
        "two_private_three_public_best_sample": rows(THREE_NAMES, sampled_best, samples),
        "anchored_two_private_three_public_best_sample": rows(THREE_NAMES, sampled_anchored, samples),
        "five_all_sample": rows(FIVE_NAMES, sampled_five, samples),
        "five_sample_category_entirely_public_pair_or_trip": {
            "count": public_pair_or_trip_only,
            "total": samples,
            "frequency": public_pair_or_trip_only / samples,
        },
    }


def write_results(output: Path, result: dict) -> None:
    output.mkdir(parents=True, exist_ok=True)
    (output / "frequencies.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for name in ("raw_three_exact", "anchored_one_private_two_public_exact", "five_all_exact", "two_private_three_public_best_sample", "anchored_two_private_three_public_best_sample", "five_all_sample"):
        with (output / f"{name}.csv").open("w", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=("category", "count", "total", "frequency"))
            writer.writeheader()
            writer.writerows(result[name])
    public_only = result["five_sample_category_entirely_public_pair_or_trip"]["frequency"]
    report = f"""# 牌型频率探针

这是发牌分布分析，不是策略对战，也没有模拟改航、下注或撤退。种子为 `{result['seed']}`，随机样本 `{result['samples']:,}` 局面。牌库为四色各 1—9，不含环绕顺子；1 仅为最小点数。

## 推荐方案：1 张私牌 + 2 张公共牌，六组选最强

以下才是推荐规则中**最终选出的牌型**频率。每次无放回抽取 5 张不同牌，前 2 张为私牌、后 3 张为公共牌；对子可以由两张公共牌组成，仍必须使用私牌作第三张牌。以下是蒙特卡洛估计，不能称为精确枚举，也不是发生过玩家操作后的实际频率。

{markdown_table(result['two_private_three_public_best_sample'])}

## 基础对照：无角色的随机三张牌，精确枚举

共 C(36,3) = 7,140 组。不区分私牌；所有对子都算对子。此表用来理解牌库构成，不能直接当作游戏最后选出的牌型分布。

{markdown_table(result['raw_three_exact'])}

## 可选变体：对子必须包含私牌，基础精确枚举

共 36 × C(35,2) = 21,420 个带角色组合。对子必须包含私牌，但还没有“2 私牌、3 公牌选最优”的选择收益。

{markdown_table(result['anchored_one_private_two_public_exact'])}

此条件不属于推荐默认规则；默认规则中，固定一私两公的边际频率与上面的无角色三张表一致。条件会产生“看起来是一对，但算散牌”的特殊情况，故仅供规则取舍参考。

## 可选变体：对子必须包含私牌，六组选最优

同一批两私三公布局，改用私牌对子条件重新选择最强组合：

{markdown_table(result['anchored_two_private_three_public_best_sample'])}

## 五张全用方案：精确枚举

共 C(36,5) = 376,992 组。所有五张都用，所以随机 2 私牌 + 3 公牌的边际牌型分布与此表相同；各玩家共享公牌，彼此牌型并不独立。展示沿用熟悉的葫芦高于同花的顺序，仅作候选对照；这副短牌库中同花比葫芦稀有，若采用五张方案应另行决定是否调整顺序。

{markdown_table(result['five_all_exact'])}

同一批随机布局中，有 {public_only:.2%} 的布局在五张方案下是“仅公牌组成对子或三条，私牌仅为踢脚牌”。两张私牌虽然参与五张比较，却未参与决定该牌型的重复点数组合。这是对五张方案结构的观察，不代表该玩家一定与对手平手。

## 口径与边界

- 每个精确表的各类互斥：同花不包含同花顺，顺子不包含同花顺。
- 不比较玩家、不产生获胜者、不报告胜率。最终最优三张的分布会受改航、撤退及对手策略影响，这里没有建模。
- 100,000 独立样本下，单个比例最保守的近似 95% 误差半宽为 0.31 个百分点；稀有牌型的误差需按实际频率计算。
- 单张私牌加两个公牌共六种候选组合，候选相互重叠，不可按六次独立抽牌估算。
- 脚本内对枚举总数、各基础牌型计数、低点数组牌例子和私牌对子条件进行了确定性校验。

复现：`python3 -m simulation.hand_type_probe --samples {result['samples']} --seed {result['seed']} --output reports/hand-types`
"""
    (output / "report.md").write_text(report, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--samples", type=int, default=100_000)
    parser.add_argument("--seed", type=int, default=20261008)
    parser.add_argument("--output", type=Path, default=Path("reports/hand-types"))
    args = parser.parse_args()
    result = probe(args.samples, args.seed)
    write_results(args.output, result)
    print(markdown_table(result["two_private_three_public_best_sample"]))
    print(f"Saved frequency probe to {args.output}")


if __name__ == "__main__":
    main()
