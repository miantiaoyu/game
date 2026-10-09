"""Five navigators: normal, reversed, all disabled, and only P1 disabled.

Run with: python3 -m simulation.multi_swap --games 1000 --samples 256
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from concurrent.futures import ProcessPoolExecutor
import csv
import json
from math import sqrt
from multiprocessing import get_context
from pathlib import Path
import random
from statistics import mean, pvariance
import sys

from .game import play_voyage, seed_for
from .run import interval
from .strategies import Strategy, THRESHOLDS


VARIANTS = ("normal", "reversed", "disabled", "target_disabled")
IDENTITIES = tuple(f"P{number}" for number in range(1, 6))
LABELS = {"normal": "正常顺序", "reversed": "改航窗口逆序", "disabled": "全员关闭改航", "target_disabled": "仅 P1 关闭改航"}


def seating_for(seed: int, game: int) -> list[str]:
    """Rotate one random arrangement within each five-game block."""
    order = list(IDENTITIES)
    random.Random(seed_for(seed, "seats", game // 5)).shuffle(order)
    offset = game % 5
    return order[offset:] + order[:offset]


def run_game(
    job: tuple[int, int, int, str], *,
    swap_costs: tuple[int, ...] | None = None,
    initial_chips: int = 10,
) -> dict:
    seed, game, samples, variant = job
    seating = seating_for(seed, game)
    strategies = [
        Strategy("navigator", samples, variant != "disabled" and not (variant == "target_disabled" and identity == "P1"))
        for identity in seating
    ]
    scores = [0] * 5
    voyages = []
    decisions = []
    players = []
    for voyage in range(10):
        starter = voyage % 5
        result = play_voyage(
            strategies, starter=starter,
            deck_seed=seed_for(seed, game, voyage, "deck"),
            decision_seed=seed_for(seed, game, voyage, "decisions"),
            trace=True, reverse_swap_order=variant == "reversed",
            swap_costs=swap_costs, initial_chips=initial_chips,
        )
        windows = [event for event in result.events if event["kind"] == "swap_window"]
        order = list(windows[0]["order"]) if windows else []
        assert not order or len(order) >= 2
        swaps = [event for event in result.events if event["kind"] == "swap"]
        indices = [event["index"] for event in swaps]
        overwritten_count = sum(metric["swap_overwritten"] for metric in result.metrics)
        assert overwritten_count == len(indices) - len(set(indices))
        assert overwritten_count == sum(metric["swaps_overwriting_previous"] for metric in result.metrics)
        voyages.append({
            "variant": variant, "game": game, "voyage": voyage,
            "starter": starter, "window_order": order,
            "window_participants": len(order),
            "swap_sequence": [[event["seat"], event["index"]] for event in swaps],
            "swaps": len(swaps), "repeated_slot": len(set(indices)) < len(indices),
            "overwritten_swaps": sum(metric["swap_overwritten"] for metric in result.metrics),
            "burned": result.burned, "pot": result.pot,
            "showdown": result.showdown, "tied": len(result.winners) > 1,
        })
        for seat, metric in enumerate(result.metrics):
            scores[seat] += result.scores[seat]
            players.append({
                "variant": variant, "game": game, "voyage": voyage,
                "identity": seating[seat], "seat": seat,
                "bet_position": (seat - starter) % 5 + 1,
                "score": result.scores[seat], "net_points": result.scores[seat] - initial_chips,
                "win_share": metric["win_share"], "fold": metric["fold"],
                "swaps": metric["swaps"], "swap_opportunities": metric["swap_opportunities"],
                "raise": metric["raise"], "raise_opportunities": metric["raise_opportunities"],
            })
        for event in result.events:
            if event["kind"] != "swap_decision":
                continue
            seat = event["seat"]
            metric = result.metrics[seat]
            decisions.append({
                "variant": variant, "game": game, "voyage": voyage,
                "identity": seating[seat], "seat": seat,
                "bet_position": (seat - starter) % 5 + 1,
                "rank": event["rank"], "participants": event["participants"],
                "cost": event["cost"],
                "swap_index": event["index"], "pot_before": event["pot"],
                "overwritten": metric["swap_overwritten"],
                "overwriting_previous": metric["swaps_overwriting_previous"],
                "score": result.scores[seat], "net_points": result.scores[seat] - initial_chips,
                "win_share": metric["win_share"],
            })
    top = max(scores)
    winners = [seating[seat] for seat, score in enumerate(scores) if score == top]
    burned = sum(voyage["burned"] for voyage in voyages)
    assert sum(scores) + burned == initial_chips * 50
    return {
        "variant": variant, "game": game, "seating": seating,
        "initial_chips": initial_chips,
        "scores": dict(zip(seating, scores)), "winners": winners,
        "win_share": {identity: (1 / len(winners) if identity in winners else 0) for identity in seating},
        "burned": burned, "voyages": voyages, "decisions": decisions, "players": players,
    }


def ratio(numerator: float, denominator: float) -> float:
    return numerator / denominator if denominator else 0.0


def decision_statistics(rows: list[dict]) -> dict:
    count = len(rows)
    swaps = sum(row["swap_index"] is not None for row in rows)
    overwritten = sum(row["overwritten"] for row in rows)
    return {
        "opportunities": count, "swaps": swaps, "overwritten": overwritten,
        "swap_rate": ratio(swaps, count),
        "overwritten_rate_per_swap": ratio(overwritten, swaps),
        "mean_net_points": ratio(sum(row["net_points"] for row in rows), count),
        "win_share": ratio(sum(row["win_share"] for row in rows), count),
    }


def aggregate(records: list[dict]) -> dict:
    voyages = [row for record in records for row in record["voyages"]]
    decisions = [row for record in records for row in record["decisions"]]
    players = [row for record in records for row in record["players"]]
    voyage_count = len(voyages)
    window_count = sum(bool(row["window_order"]) for row in voyages)
    multi_count = sum(row["swaps"] >= 2 for row in voyages)
    repeated_count = sum(row["repeated_slot"] for row in voyages)
    swaps = sum(row["swaps"] for row in voyages)
    overwritten = sum(row["overwritten_swaps"] for row in voyages)
    distribution = Counter(row["swaps"] for row in voyages)
    strata = defaultdict(list)
    ranks = defaultdict(list)
    conditions = {"retained": [], "overwritten": [], "passed": []}
    for row in decisions:
        strata[row["participants"], row["rank"]].append(row)
        ranks[row["rank"]].append(row)
        condition = "passed" if row["swap_index"] is None else "overwritten" if row["overwritten"] else "retained"
        conditions[condition].append(row)
    identities = {}
    for identity in IDENTITIES:
        scores = [record["scores"][identity] for record in records]
        identities[identity] = {
            "match_win_share": interval([record["win_share"][identity] for record in records]),
            "points_per_match": interval(scores),
            "match_score_std": sqrt(pvariance(scores)),
        }
    seat_counts = {identity: [0] * 5 for identity in IDENTITIES}
    for record in records:
        for seat, identity in enumerate(record["seating"]):
            seat_counts[identity][seat] += 1
    bet_positions = []
    for position in range(1, 6):
        rows = [row for row in players if row["bet_position"] == position]
        bet_positions.append({
            "bet_position": position, "voyages": len(rows),
            "mean_net_points": mean(row["net_points"] for row in rows),
            "win_share": mean(row["win_share"] for row in rows),
            "fold_rate": mean(row["fold"] for row in rows),
            "swap_rate": ratio(sum(row["swaps"] for row in rows), sum(row["swap_opportunities"] for row in rows)),
        })
    all_scores = [score for record in records for score in record["scores"].values()]
    return {
        "games": len(records), "voyages": voyage_count,
        "window_voyages": window_count,
        "window_participant_distribution": {str(k): sum(row["window_participants"] == k for row in voyages) for k in range(2, 6)},
        "swaps": swaps, "swap_opportunities": len(decisions),
        "swap_rate": ratio(swaps, len(decisions)),
        "swap_count_distribution": {str(k): {"voyages": distribution[k], "rate": ratio(distribution[k], voyage_count)} for k in range(6)},
        "multi_swap_voyages": multi_count,
        "multi_swap_rate_all_voyages": ratio(multi_count, voyage_count),
        "repeated_slot_voyages": repeated_count,
        "repeated_slot_rate_all_voyages": ratio(repeated_count, voyage_count),
        "repeated_slot_rate_window_voyages": ratio(repeated_count, window_count),
        "repeated_slot_rate_multi_swap_voyages": ratio(repeated_count, multi_count),
        "overwritten_swaps": overwritten,
        "overwritten_rate_per_swap": ratio(overwritten, swaps),
        "window_strata": [{"participants": key[0], "rank": key[1], **decision_statistics(rows)} for key, rows in sorted(strata.items())],
        "window_ranks_unadjusted": [{"rank": rank, **decision_statistics(rows)} for rank, rows in sorted(ranks.items())],
        "conditional_outcomes": {condition: decision_statistics(rows) for condition, rows in conditions.items()},
        "bet_positions": bet_positions,
        "showdown_rate": mean(row["showdown"] for row in voyages),
        "early_finish_rate": mean(not row["showdown"] for row in voyages),
        "mean_pot": mean(row["pot"] for row in voyages),
        "fold_rate_per_player_voyage": mean(row["fold"] for row in players),
        "mean_folds_per_voyage": sum(row["fold"] for row in players) / voyage_count,
        "raise_rate_per_opportunity": ratio(sum(row["raise"] for row in players), sum(row["raise_opportunities"] for row in players)),
        "voyage_tie_rate": mean(row["tied"] for row in voyages),
        "match_tie_rate": mean(len(record["winners"]) > 1 for record in records),
        "mean_match_score": mean(all_scores),
        "pooled_match_score_std": sqrt(pvariance(all_scores)),
        "mean_within_match_score_variance": mean(pvariance(record["scores"].values()) for record in records),
        "burned_chips": sum(record["burned"] for record in records),
        "identities": identities, "seat_counts": seat_counts,
    }


def nullable_interval(indexed_values: list[tuple[int, float]]) -> dict:
    """Preserve the original five-game clusters if a game has no window."""
    if not indexed_values:
        return {"mean": None, "ci95": None, "clusters": 0, "games_used": 0}
    values = [value for _, value in indexed_values]
    if [game for game, _ in indexed_values] == list(range(len(indexed_values))) and len(values) >= 6:
        return {**interval(values), "games_used": len(values)}
    average = mean(values)
    residuals = defaultdict(float)
    for game, value in indexed_values:
        residuals[game // 5] += value - average
    groups = len(residuals)
    if groups < 2:
        bounds = None
    else:
        standard_error = sqrt(groups / (groups - 1) * sum(value ** 2 for value in residuals.values())) / len(values)
        bounds = [average - 1.96 * standard_error, average + 1.96 * standard_error]
    return {"mean": average, "ci95": bounds, "clusters": groups, "games_used": len(values)}


def paired_order_comparison(normal: list[dict], reversed_records: list[dict]) -> dict:
    """Track original first/last players when the entire window is reversed."""
    assert len(normal) == len(reversed_records)
    groups = {}
    for participants in (None, 2, 3, 4, 5):
        values = {role: {metric: [] for metric in ("net_points", "win_share")} for role in ("original_first", "original_last")}
        eligible_voyages = 0
        for original, reverse in zip(normal, reversed_records):
            assert original["game"] == reverse["game"]
            first_players = {(row["voyage"], row["seat"]): row for row in original["players"]}
            reverse_players = {(row["voyage"], row["seat"]): row for row in reverse["players"]}
            game_values = {role: {metric: [] for metric in ("net_points", "win_share")} for role in values}
            for voyage, reversed_voyage in zip(original["voyages"], reverse["voyages"]):
                order = voyage["window_order"]
                assert order == list(reversed(reversed_voyage["window_order"]))
                if not order or (participants is not None and len(order) != participants):
                    continue
                eligible_voyages += 1
                for role, seat in (("original_first", order[0]), ("original_last", order[-1])):
                    key = voyage["voyage"], seat
                    for metric in ("net_points", "win_share"):
                        game_values[role][metric].append(reverse_players[key][metric] - first_players[key][metric])
            for role in values:
                for metric in values[role]:
                    if game_values[role][metric]:
                        values[role][metric].append((original["game"], mean(game_values[role][metric])))
        groups["all" if participants is None else str(participants)] = {
            "eligible_voyages": eligible_voyages,
            **{role: {metric: nullable_interval(points) for metric, points in metrics.items()} for role, metrics in values.items()},
        }
    return {
        "difference": "reversed minus normal",
        "estimand": "Within each game, mean difference over eligible voyages for the originally first/last window participant; then equally weight games. This is the effect of reversing the entire window.",
        "by_window_participants": groups,
    }


def paired_target_comparison(normal: list[dict], target_disabled: list[dict]) -> dict:
    assert len(normal) == len(target_disabled)
    assert all(a["game"] == b["game"] and a["seating"] == b["seating"] for a, b in zip(normal, target_disabled))
    return {
        "identity": "P1",
        "difference": "normal minus target_disabled",
        "estimand": "Allowing P1 to swap while the other four players retain their swap option; same initial deals and seating paired by game.",
        "points_per_match_difference": interval([a["scores"]["P1"] - b["scores"]["P1"] for a, b in zip(normal, target_disabled)]),
        "match_win_share_difference": interval([a["win_share"]["P1"] - b["win_share"]["P1"] for a, b in zip(normal, target_disabled)]),
    }


def pct(value: float) -> str:
    return f"{100 * value:.2f}%"


def report_markdown(summary: dict) -> str:
    config = summary["config"]
    variants = summary["variants"]
    main = variants["normal"]
    lines = [
        "# 《暗潮航线》多人改航模拟", "",
        f"每组 {config['games']:,} 场完整游戏，每场 5 人、10 趟航行；四组共 {config['games'] * 40:,} 趟。五名玩家均使用同一概率改航策略，每次估计 {config['samples']} 个样本。种子 `{config['seed']}`。", "",
        "## 实验口径", "",
        "- 正常组按下注座次依次改航；逆序组只反转改航窗口，下注顺序保持不变；禁用组全员不能改航；另设仅 P1 关闭改航的组，其余四人仍可改航。每个玩家最多改航一次，允许重复替换同一位置，费用为 2。",
        "- 四组按场次和航行共用初始牌库及决策随机种子。改航后牌库消耗和后续决策可以发生变化。",
        "- 主实验中 P1–P5 是完全相同策略的身份编号；仅 P1 禁用组是例外。每五场对座位做一次完整轮换，每场起始位置轮换两圈；身份胜率差异不能解释为策略优劣。",
        "- 每趟净收益为所得补给减 10，胜率按并列人数分摊。窗口顺位从 1 开始，按仍在场的参与者排序；CSV 的绝对座位和牌位置从 0 开始，下注位置从 1 开始。",
        "- 只有至少两人仍在场且到达第二张公共牌时才有改航窗口。顺位统计只包含到达窗口的玩家；下注位置统计包含所有航行、所有玩家。",
        "- 顺序反转的配对差值先在同一场内对适用航行取均值，再以游戏为单位取平均；缺少适用窗口的场次跳过，原始块编号保留。P1 开关改航的对照直接比较同场整场总分及夺冠权重。所有 95% 区间均按原始五场座位平衡块聚类。", "",
        "## 四组节奏及波动", "",
        "| 组别 | 改航次数 | 多人改航 / 所有趟 | 重复位置 / 所有趟 | 摊牌率 | 平均奖池 | 弃牌 / 玩家趟 | 整场分数标准差 |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for variant in VARIANTS:
        row = variants[variant]
        lines.append(f"| {LABELS[variant]} | {row['swaps']} | {pct(row['multi_swap_rate_all_voyages'])} | {pct(row['repeated_slot_rate_all_voyages'])} | {pct(row['showdown_rate'])} | {row['mean_pot']:.2f} | {pct(row['fold_rate_per_player_voyage'])} | {row['pooled_match_score_std']:.2f} |")
    lines.extend([
        "", "全桌得分总和仅因平局余筹弃置而减少，近似零和；禁用组适合比较节奏和分数波动，全桌平均得分之差不能当作改航的个人收益。标准差汇总所有身份的整场分数。", "",
        "## 正常顺序：冲突频率", "",
        f"共有 {main['window_voyages']:,} 趟到达窗口，{main['swap_opportunities']:,} 次玩家机会；改航率为 {pct(main['swap_rate'])}。",
        f"同一公共牌位置被重复替换 {main['repeated_slot_voyages']:,} 趟，占所有航行 {pct(main['repeated_slot_rate_all_voyages'])}，占至少两次改航的航行 {pct(main['repeated_slot_rate_multi_swap_voyages'])}。共 {main['overwritten_swaps']:,} 次改航被后人覆盖，占所有改航 {pct(main['overwritten_rate_per_swap'])}。", "",
        "| 每趟改航次数 | 航行数 | 占全部航行 |", "| --- | ---: | ---: |",
    ])
    for count, row in main["swap_count_distribution"].items():
        lines.append(f"| {count} | {row['voyages']} | {pct(row['rate'])} |")
    lines.extend(["", "## 正常顺序：按窗口人数及顺位", "", "| 窗口人数 | 顺位 | 机会 | 改航率 | 改航被覆盖率 | 玩家单趟净收益 | 分摊胜率 |", "| --- | --- | ---: | ---: | ---: | ---: | ---: |"])
    for row in sorted(main["window_strata"], key=lambda row: (-row["participants"], row["rank"])):
        overwrite = pct(row["overwritten_rate_per_swap"]) if row["swaps"] else "—"
        lines.append(f"| {row['participants']} | {row['rank']} | {row['opportunities']} | {pct(row['swap_rate'])} | {overwrite} | {row['mean_net_points']:+.3f} | {pct(row['win_share'])} |")
    lines.extend(["", "被覆盖率以该行实际改航次数为分母；净收益与胜率包含该行全部窗口参与者。不同人数、顺位的牌况与幸存者构成不同，这些描述性差异不直接证明位置的因果收益。", "", "## 正常顺序：改航结果与收益的关联", "", "| 行为结果 | 玩家次数 | 单趟净收益 | 分摊胜率 |", "| --- | ---: | ---: | ---: |"])
    for key, label in (("retained", "改航且该位置未再被替换"), ("overwritten", "改航后被覆盖"), ("passed", "放弃改航")):
        row = main["conditional_outcomes"][key]
        lines.append(f"| {label} | {row['opportunities']} | {row['mean_net_points']:+.3f} | {pct(row['win_share'])} |")
    lines.extend(["", "这是按事后行为分组的关联。改航、被覆盖与放弃对应不同牌况和对手选择，不能据此算出改航或覆盖的因果收益。", "", "## 整个改航窗口逆序的配对结果", "", "差值为逆序组减正常组。跟踪正常组窗口中原本最先和最后行动的同一位玩家；在逆序组中，他们分别变为最后和最先行动。", "", "| 跟踪对象 | 适用游戏 | 净收益差值（95% 区间） | 分摊胜率差值，百分点（95% 区间） |", "| --- | ---: | --- | --- |"])
    paired = summary["paired_order"]["by_window_participants"]["all"]
    for role, label in (("original_first", "原窗口第一位"), ("original_last", "原窗口最后一位")):
        net, wins = paired[role]["net_points"], paired[role]["win_share"]
        def describe(value: dict, scale: int = 1) -> str:
            if value["mean"] is None:
                return "无适用数据"
            if value["ci95"] is None:
                return f"{value['mean'] * scale:+.3f}（不足两个簇）"
            low, high = value["ci95"]
            return f"{value['mean'] * scale:+.3f}（{low * scale:+.3f}，{high * scale:+.3f}）"
        lines.append(f"| {label} | {net['games_used']} | {describe(net)} | {describe(wins, 100)} |")
    lines.extend(["", "逆序同时改变整个窗口内所有人的决策环境及后续公共牌，不能解释为单独移动一个位置的纯粹因果效果。按窗口人数分层的配对结果也保存在 summary.json。", "", "## 所有航行的下注位置", "", "| 下注位置 | 正常净收益 | 正常分摊胜率 | 逆序净收益 | 禁用净收益 |", "| --- | ---: | ---: | ---: | ---: |"])
    for position in range(5):
        a, b, c = [variants[variant]["bet_positions"][position] for variant in ("normal", "reversed", "disabled")]
        lines.append(f"| {position + 1} | {a['mean_net_points']:+.3f} | {pct(a['win_share'])} | {b['mean_net_points']:+.3f} | {c['mean_net_points']:+.3f} |")
    target = summary["paired_target"]
    lines.extend(["", "## 其余四人都会改航时，自己是否值得保留改航", "", "固定跟踪身份 P1；正常组允许 P1 改航，对照组仅禁用 P1 的改航，其余四人仍可改航。差值为正常组减仅 P1 禁用组。", "", "| 指标 | P1 可改航 | P1 不可改航 | 配对差值（95% 区间） |", "| --- | ---: | ---: | --- |"])
    normal_target = variants["normal"]["identities"]["P1"]
    disabled_target = variants["target_disabled"]["identities"]["P1"]
    for label, key, difference_key, scale in (("整场得分", "points_per_match", "points_per_match_difference", 1), ("整场分摊胜率（%）", "match_win_share", "match_win_share_difference", 100)):
        delta = target[difference_key]
        low, high = delta["ci95"]
        lines.append(f"| {label} | {normal_target[key]['mean'] * scale:.3f} | {disabled_target[key]['mean'] * scale:.3f} | {delta['mean'] * scale:+.3f}（{low * scale:+.3f}，{high * scale:+.3f}） |")
    lines.extend(["", "P1 每五场完整轮换全部座位。这个配对对照衡量当前机器人和四名可改航对手下，给 P1 开放改航选项的效果；对手后续反应属于该效果的一部分，不能外推为所有策略下的最优选择。"])
    lines.extend([
        "", "## 局限与复现", "",
        "- 正常组和逆序组的五人均会根据底池赔率弃牌，也都能改航；所有组都不包含永不弃牌的固定玩家。",
        "- 仍按均匀未知手牌估计对手，不根据对手下注或改航更新其范围。改航估值不预判后来玩家覆盖；Observation 不提供改航顺位，所以机器人不会主动因先后手调整改航策略。",
        "- 当前策略评估局部摊牌价值，忽略未来下注和逼退收益。模拟能检查冲突、收益关联和整窗逆序的效果，不能充分验证真人的预判、报复、诱导或心理战。",
        "- 95% 区间仅表达样本波动，没有多重比较校正，不包含机器人模型偏差。稀少窗口的分层结果尤其需要谨慎。",
        "- summary.json 中四组均有完整顺位统计、条件结果、身份统计、平局比例、余筹和座位计数。", "",
        "```bash",
        f"python3 -m simulation.multi_swap --games {config['games']} --samples {config['samples']} --seed {config['seed']} --workers {config['workers']} --output {config['output']}",
        "```", "",
        "- games.csv：每组、每场、每个身份的总分、座位及分摊夺冠权重。",
        "- voyages.csv：每趟窗口实际顺序、改航序列（座位、公共牌位置）、次数、冲突、奖池及结算状态。",
        "- decisions.csv：每个到达窗口的玩家的行为与最终收益；空 swap_index 代表放弃。未到达窗口的玩家不进入该文件。",
        "- summary.json：参数及完整汇总。", "",
    ])
    return "\n".join(lines)


def write_outputs(output: Path, records_by_variant: dict, summary: dict) -> None:
    output.mkdir(parents=True, exist_ok=True)
    (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (output / "report.md").write_text(report_markdown(summary), encoding="utf-8")
    records = [record for variant in VARIANTS for record in records_by_variant[variant]]
    with (output / "games.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=["variant", "game", "identity", "seat", "score", "net_points", "win_share", "tied_top", "burned"])
        writer.writeheader()
        for record in records:
            for seat, identity in enumerate(record["seating"]):
                writer.writerow({"variant": record["variant"], "game": record["game"], "identity": identity, "seat": seat, "score": record["scores"][identity], "net_points": record["scores"][identity] - record.get("initial_chips", 10) * 10, "win_share": record["win_share"][identity], "tied_top": identity in record["winners"] and len(record["winners"]) > 1, "burned": record["burned"]})
    for filename, key, fields in (
        ("voyages.csv", "voyages", ["variant", "game", "voyage", "starter", "window_order", "window_participants", "swap_sequence", "swaps", "repeated_slot", "overwritten_swaps", "burned", "pot", "showdown", "tied"]),
        ("decisions.csv", "decisions", ["variant", "game", "voyage", "identity", "seat", "bet_position", "rank", "participants", "cost", "swap_index", "pot_before", "overwritten", "overwriting_previous", "score", "net_points", "win_share"]),
    ):
        with (output / filename).open("w", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=fields)
            writer.writeheader()
            for record in records:
                for original in record[key]:
                    row = dict(original)
                    for field in ("window_order", "swap_sequence"):
                        if field in row:
                            row[field] = json.dumps(row[field], separators=(",", ":"))
                    writer.writerow(row)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--games", type=int, default=1000)
    parser.add_argument("--samples", type=int, default=256)
    parser.add_argument("--seed", type=int, default=20261008)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--output", type=Path, default=Path("reports/multi-swap-1000"))
    args = parser.parse_args()
    if args.games < 10 or args.samples < 1 or args.workers < 1:
        parser.error("games must be >= 10; samples and workers must be positive")
    records_by_variant = {variant: [] for variant in VARIANTS}
    jobs = [(args.seed, game, args.samples, variant) for variant in VARIANTS for game in range(args.games)]
    with ProcessPoolExecutor(max_workers=args.workers, mp_context=get_context("spawn")) as pool:
        for record in pool.map(run_game, jobs, chunksize=1):
            variant = record["variant"]
            records_by_variant[variant].append(record)
            done = len(records_by_variant[variant])
            if done == 1 or done % 50 == 0 or done == args.games:
                print(f"{variant}: {done}/{args.games} complete", file=sys.stderr, flush=True)
    summary = {
        "config": {"games": args.games, "samples": args.samples, "seed": args.seed, "workers": args.workers, "output": str(args.output), "python_version": sys.version, "players": 5, "voyages_per_game": 10, "strategy": "navigator", "thresholds": THRESHOLDS},
        "variants": {variant: aggregate(records_by_variant[variant]) for variant in VARIANTS},
        "paired_order": paired_order_comparison(records_by_variant["normal"], records_by_variant["reversed"]),
        "paired_target": paired_target_comparison(records_by_variant["normal"], records_by_variant["target_disabled"]),
    }
    write_outputs(args.output, records_by_variant, summary)
    print(f"Reports written to {args.output}", flush=True)


if __name__ == "__main__":
    main()
