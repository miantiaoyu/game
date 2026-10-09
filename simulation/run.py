"""Run with: python3 -m simulation.run --games 1000 --seed 20261008."""

from __future__ import annotations

import argparse
from collections import Counter
from concurrent.futures import ProcessPoolExecutor
import csv
import json
from math import sqrt
from multiprocessing import get_context
from pathlib import Path
import random
from statistics import mean
import sys

from .game import play_voyage, seed_for
from .strategies import LABELS, STRATEGY_NAMES, THRESHOLDS, Strategy


def seating_for(seed: int, game: int) -> list[str]:
    # Every complete five-game block gives each strategy every seat once.
    order = list(STRATEGY_NAMES)
    random.Random(seed_for(seed, "seats", game // 5)).shuffle(order)
    offset = game % 5
    return order[offset:] + order[:offset]


def run_game(job: tuple[int, int, int, bool]) -> dict:
    seed, game, samples, swaps_enabled = job
    seating = seating_for(seed, game)
    strategies = [Strategy(name, samples, swaps_enabled) for name in seating]
    scores = [0] * 5
    totals = [Counter() for _ in range(5)]
    position_totals = [Counter() for _ in range(5)]
    navigator_positions = [Counter() for _ in range(5)]
    burned = showdown_count = tie_count = pot_total = 0
    for voyage in range(10):
        starter = voyage % 5
        result = play_voyage(
            strategies, starter=starter,
            deck_seed=seed_for(seed, game, voyage, "deck"),
            decision_seed=seed_for(seed, game, voyage, "decisions"),
        )
        burned += result.burned
        showdown_count += result.showdown
        tie_count += len(result.winners) > 1
        pot_total += result.pot
        for seat, name in enumerate(seating):
            scores[seat] += result.scores[seat]
            totals[seat].update(result.metrics[seat])
            position = (seat - starter) % 5
            position_totals[position].update(result.metrics[seat])
            if name == "navigator":
                navigator_positions[position].update(result.metrics[seat])
    highest = max(scores)
    winners = [seating[seat] for seat, score in enumerate(scores) if score == highest]
    assert sum(scores) + burned == 500
    return {
        "game": game, "swaps_enabled": swaps_enabled, "seating": seating,
        "scores": dict(zip(seating, scores)),
        "win_credit": {name: (1 / len(winners) if name in winners else 0) for name in seating},
        "winners": winners, "totals": dict(zip(seating, totals)),
        "positions": position_totals, "navigator_positions": navigator_positions,
        "burned": burned, "showdowns": showdown_count, "ties": tie_count,
        "pot_total": pot_total,
    }


def interval(values: list[float]) -> dict:
    average = mean(values)
    # Relative seating is shared inside each five-game balancing block.
    # Cluster on that block rather than assume the games are unconditionally
    # independent over random table arrangements. Also handles a partial block.
    residual_sums = [sum(value - average for value in values[start:start + 5]) for start in range(0, len(values), 5)]
    groups = len(residual_sums)
    if groups < 2:
        raise ValueError("Confidence intervals require at least two seating blocks")
    standard_error = sqrt(groups / (groups - 1) * sum(value ** 2 for value in residual_sums)) / len(values)
    half_width = 1.96 * standard_error
    return {"mean": average, "ci95": [average - half_width, average + half_width], "clusters": groups}


def ratio(numerator: float, denominator: float) -> float:
    return numerator / denominator if denominator else 0.0


def aggregate(records: list[dict]) -> dict:
    games = len(records)
    strategies = {}
    for name in STRATEGY_NAMES:
        totals = Counter()
        for record in records:
            totals.update(record["totals"][name])
        scores = [record["scores"][name] for record in records]
        strategies[name] = {
            "label": LABELS[name],
            "match_win_rate": interval([record["win_credit"][name] for record in records]),
            "match_top_rate": mean(name in record["winners"] for record in records),
            "match_tied_top_rate": mean(name in record["winners"] and len(record["winners"]) > 1 for record in records),
            "points_per_match": interval(scores),
            "net_points_per_voyage": interval([(score - 100) / 10 for score in scores]),
            "voyage_win_share": ratio(totals["win_share"], totals["voyages"]),
            "fold_rate_per_voyage": ratio(totals["fold"], totals["voyages"]),
            "raise_rate_per_opportunity": ratio(totals["raise"], totals["raise_opportunities"]),
            "swap_rate_per_opportunity": ratio(totals["swaps"], totals["swap_opportunities"]),
            "win_share_after_swap": ratio(totals["swapped_win_share"], totals["swaps"]),
            "counts": dict(totals),
        }
    positions = []
    navigator_positions = []
    for position in range(5):
        for key, destination in (("positions", positions), ("navigator_positions", navigator_positions)):
            totals = Counter()
            for record in records:
                totals.update(record[key][position])
            destination.append({
                "position": position + 1,
                "mean_net_points": ratio(totals["net_points"], totals["voyages"]),
                "swap_rate": ratio(totals["swaps"], totals["swap_opportunities"]),
                "voyages": totals["voyages"],
            })
    seat_counts = {name: [0] * 5 for name in STRATEGY_NAMES}
    for record in records:
        for seat, name in enumerate(record["seating"]):
            seat_counts[name][seat] += 1
    return {
        "games": games, "voyages": games * 10, "strategies": strategies,
        "showdown_rate": sum(record["showdowns"] for record in records) / (games * 10),
        "voyage_tie_rate": sum(record["ties"] for record in records) / (games * 10),
        "match_tie_rate": mean(len(record["winners"]) > 1 for record in records),
        "mean_pot": sum(record["pot_total"] for record in records) / (games * 10),
        "burned_chips": sum(record["burned"] for record in records),
        "seat_counts": seat_counts, "positions": positions,
        "navigator_positions": navigator_positions,
        "within_match_score_contrasts": {
            name + "_minus_equity": interval([record["scores"][name] - record["scores"]["equity"] for record in records])
            for name in ("navigator", "bluffer", "tight", "calling_station")
        },
    }


def paired_comparison(main: list[dict], control: list[dict]) -> dict:
    result = {}
    for name in STRATEGY_NAMES:
        result[name] = {
            "points_per_match_difference": interval([a["scores"][name] - b["scores"][name] for a, b in zip(main, control)]),
            "win_rate_difference": interval([a["win_credit"][name] - b["win_credit"][name] for a, b in zip(main, control)]),
        }
    return result


def pct(value: float) -> str:
    return f"{100 * value:.2f}%"


def report_markdown(summary: dict) -> str:
    config, main = summary["config"], summary["main"]
    lines = [
        "# 《暗潮航线》v0.1 策略模拟报告", "",
        f"主实验：{main['games']:,} 场完整游戏 × 5 人 × 10 趟航行，共 {main['voyages']:,} 趟。随机种子 `{config['seed']}`，每次决策使用 {config['samples']} 个蒙特卡洛样本。",
        "",
        "## 实验口径", "",
        "- 每场固定五种策略各一名；座位按五场一组随机排列并轮换，每种策略在每个座位出现相同次数（若场数不是五的倍数，最多相差一次）。每场每人先手两次。",
        "- 整场胜率按并列第一人数分摊；每趟胜率同样分摊。所有整场胜率之和为 100%。包含并列的夺冠率和并列占比另存于 summary.json。",
        "- 整场得分的中性基准为 100 分。每趟净收益 = 得分 − 10；弃置的平局余筹使全体平均略低于零。",
        "- 95% 区间使用五场座位平衡块作为独立簇，按均值 ± 1.96 × 簇稳健标准误估算（1000 场对应 200 簇）；同一块共享相对座次。不会把一场的十趟视为十个独立样本。区间未作多重比较校正。",
        "- 真实发牌、不同玩家决策、下注与改航、蒙特卡洛抽样使用分离的随机流。策略只看到自己的手牌、公共牌、公开弃牌、存活人数及筹码信息。",
        "", "## 五种策略", "",
        "| 策略 | 下注方式 | 改航方式 |", "| --- | --- | --- |",
        "| 跟注到底 | 过牌或跟注，从不弃牌、加码 | 从不改航 |",
        "| 谨慎保守 | 跟注要求 q ≥ max(底池赔率 + 0.08, 0.9 / 存活人数)；加码要求 q ≥ max(0.60, 1 / 人数 + 0.25) | 从不改航 |",
        "| 概率决策 | q ≥ 底池赔率时跟注；q ≥ max(0.45, 1 / 人数 + 0.18) 时加码 | 从不改航 |",
        "| 主动诈唬 | 跟注与概率决策相同；加码门槛降低为 max(0.32, 1 / 人数 + 0.10)，其余合法加码机会有 8% 概率诈唬 | 从不改航 |",
        "| 概率改航 | 与概率决策完全相同 | 评估两张公共牌，只有替换后的预计净价值比保留高出 0.10 筹码才支付 2 改航 |",
        "",
        "q 是预计摊牌奖池份额，已计入并列。底池赔率为 C / (P + C)，P 是行动前奖池，C 是跟注费用。无须跟注时，所有策略都会保留免费参与资格。",
        "",
        "改航使用成对假想牌局估计：保持价值为 q_keep × P；改航价值为 q_swap × (P + 2) − 2。这是忽略未来下注、其他玩家后续改航和逼退收益的局部估值。抽样评估中对平局使用精确分数份额，真实结算按整数分池并弃置余筹。",
        "", "## 主实验结果", "",
        "| 策略 | 整场胜率 | 95% 区间 | 平均整场分数 | 每趟净收益 | 每趟胜率 |",
        "| --- | ---: | --- | ---: | ---: | ---: |",
    ]
    ranked = sorted(STRATEGY_NAMES, key=lambda name: main["strategies"][name]["match_win_rate"]["mean"], reverse=True)
    for name in ranked:
        row = main["strategies"][name]
        low, high = row["match_win_rate"]["ci95"]
        lines.append(f"| {LABELS[name]} | {pct(row['match_win_rate']['mean'])} | {pct(low)}–{pct(high)} | {row['points_per_match']['mean']:.2f} | {row['net_points_per_voyage']['mean']:+.3f} | {pct(row['voyage_win_share'])} |")
    lines.extend([
        "", "## 行为统计", "",
        "| 策略 | 弃牌率 / 航行 | 加码率 / 合法机会 | 改航率 / 到达窗口 | 改航次数 |",
        "| --- | ---: | ---: | ---: | ---: |",
    ])
    for name in STRATEGY_NAMES:
        row = main["strategies"][name]
        lines.append(f"| {LABELS[name]} | {pct(row['fold_rate_per_voyage'])} | {pct(row['raise_rate_per_opportunity'])} | {pct(row['swap_rate_per_opportunity'])} | {row['counts'].get('swaps', 0)} |")
    navigator = main["strategies"]["navigator"]
    lines.extend([
        "",
        f"摊牌率 {pct(main['showdown_rate'])}；无需摊牌即结束 {pct(1-main['showdown_rate'])}；航行平局率 {pct(main['voyage_tie_rate'])}；整场并列第一率 {pct(main['match_tie_rate'])}；平均奖池 {main['mean_pot']:.2f}；平局共弃置 {main['burned_chips']} 枚余筹。",
        "",
        f"概率改航者实际改航后的分摊胜率为 {pct(navigator['win_share_after_swap'])}。这一条件统计受选择牌况影响，不能单独证明改航有效。",
    ])
    if "paired" in summary:
        control = summary["without_swaps"]
        delta = summary["paired"]["navigator"]
        points = delta["points_per_match_difference"]
        wins = delta["win_rate_difference"]
        lines.extend([
            "", "## 关闭改航的配对对照", "",
            f"另跑 {control['games']:,} 场。保留相同座位、每趟初始牌库和随机流种子，仅禁用改航。概率改航者的下注规则保持不变。改航消耗牌库、改变下注进程产生的后续变化属于机制效果。",
            "",
            "| 概率改航者 | 启用改航 | 禁用改航 | 配对差值（95% 区间） |",
            "| --- | ---: | ---: | --- |",
            f"| 平均整场分数 | {navigator['points_per_match']['mean']:.2f} | {control['strategies']['navigator']['points_per_match']['mean']:.2f} | {points['mean']:+.2f}（{points['ci95'][0]:+.2f}，{points['ci95'][1]:+.2f}） |",
            f"| 整场胜率 | {pct(navigator['match_win_rate']['mean'])} | {pct(control['strategies']['navigator']['match_win_rate']['mean'])} | {100*wins['mean']:+.2f} 个百分点（{100*wins['ci95'][0]:+.2f}，{100*wins['ci95'][1]:+.2f}） |",
            "",
            "启用减去禁用的差值按同一游戏编号逐场配对后计算区间。仅估计本策略、固定对手构成下开放改航选项的效果。",
        ])
    lines.extend([
        "", "## 对原型的初步判断", "",
        f"- 本次混合对手桌中，{LABELS[ranked[0]]}的整场胜率最高；该排名不代表普遍最优策略。",
        f"- 改航者在 {pct(navigator['swap_rate_per_opportunity'])} 的实际窗口选择支付费用。改航在当前策略下是一种有条件的选择，尚未表现为每次必选的动作；机制收益以配对对照为依据。",
        f"- 跟注到底的每趟胜率为 {pct(main['strategies']['calling_station']['voyage_win_share'])}，但整场胜率仅 {pct(main['strategies']['calling_station']['match_win_rate']['mean'])}、平均得分 {main['strategies']['calling_station']['points_per_match']['mean']:.2f}。赢下多少趟与最终拿到多少分需要分别衡量。",
        "- 本桌固定存在永不弃牌者，其余策略也不会在可免费过牌时弃牌，因此无法靠加码逼退全桌。当前摊牌比例不能用来判定游戏规则本身是否缺乏诈唬空间；下一轮应加入全部会弃牌的对手组合。",
        "- 后续优先比较多人都会改航的局面、不同改航费用，以及会根据加码与改航更新对手手牌范围的策略；这些问题尚未由本次实验回答。",
        "", "## 解读边界", "",
        "- 这是预先写定的启发式机器人对战，没有训练、参数寻优或纳什均衡求解。排名仅适用于当前对手组合。",
        "- 对手手牌按均匀未知牌估算；机器人不从下注或改航动作更新对手范围，也不观察历史习惯。因此无法据此证明读人、诱导或真人心理战已经成立。",
        "- 主动诈唬同时改变了强牌加码门槛并加入随机诈唬，其表现不能归因于诈唬这一个因素。",
        "- 主实验只有一位会改航的玩家。尚未验证多人连续改航、改航窗口的后手优势，以及 3 人、4 人局。",
        "- 概率估计使用有限样本，接近门槛的选择可能受抽样噪声影响；置信区间只反映本次游戏样本波动，不覆盖模型偏差。",
        "- 机器人用局部预计收益决策，没有按当前整场领先或落后情况调整风险；平均收益最高也不保证整场胜率最高。",
        "- 1000 场不能直接验证趣味性、规则理解成本和实际游玩时长。", "",
        "## 复现与原始数据", "",
        "```bash",
        f"python3 -m simulation.run --games {config['games']} --samples {config['samples']} --seed {config['seed']} --workers {config['workers']} --output {config['output']}" + (" --no-ablation" if not config["ablation"] else ""),
        "python3 -m unittest discover -s tests -v",
        "```", "",
        "- `summary.json`：参数、完整统计、各策略置信区间、位置统计、配对差值。",
        "- `games.csv`：每场座位、各策略总分、分摊胜利权重，包含主实验及对照。",
        "- `report.md`：本报告。", "",
    ])
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--games", type=int, default=1000)
    parser.add_argument("--samples", type=int, default=256)
    parser.add_argument("--seed", type=int, default=20261008)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--output", type=Path, default=Path("reports/1000-games"))
    parser.add_argument("--no-ablation", action="store_true")
    args = parser.parse_args()
    if args.games < 10 or args.samples < 1 or args.workers < 1:
        parser.error("games must be >= 10; samples and workers must be positive")
    main_records, control_records = [], []
    jobs = [
        (args.seed, game, args.samples, swaps)
        for game in range(args.games)
        for swaps in ([True] if args.no_ablation else [True, False])
    ]
    # Explicit spawn also works in sandboxes that prohibit the Unix socket
    # used by Python 3.14's POSIX forkserver default.
    with ProcessPoolExecutor(max_workers=args.workers, mp_context=get_context("spawn")) as pool:
        for index, result in enumerate(pool.map(run_game, jobs, chunksize=5), start=1):
            (main_records if result["swaps_enabled"] else control_records).append(result)
            if index % max(1, len(jobs) // 10) == 0:
                print(f"Completed {index}/{len(jobs)} matches", file=sys.stderr, flush=True)
    config = {
        "games": args.games, "samples": args.samples, "seed": args.seed,
        "workers": args.workers, "output": str(args.output), "ablation": not args.no_ablation,
        "players": 5, "voyages_per_match": 10, "thresholds": THRESHOLDS,
        "python_version": sys.version.split()[0],
    }
    summary = {"config": config, "main": aggregate(main_records)}
    if control_records:
        summary["without_swaps"] = aggregate(control_records)
        summary["paired"] = paired_comparison(main_records, control_records)
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (args.output / "report.md").write_text(report_markdown(summary), encoding="utf-8")
    fields = ["variant", "game", "seating", "winners"] + [f"{name}_{metric}" for name in STRATEGY_NAMES for metric in ("points", "win_credit")]
    with (args.output / "games.csv").open("w", encoding="utf-8", newline="") as output:
        writer = csv.DictWriter(output, fieldnames=fields)
        writer.writeheader()
        for record in sorted(main_records + control_records, key=lambda row: (row["game"], not row["swaps_enabled"])):
            row = {
                "variant": "main" if record["swaps_enabled"] else "without_swaps",
                "game": record["game"], "seating": "/".join(record["seating"]), "winners": "/".join(record["winners"]),
            }
            for name in STRATEGY_NAMES:
                row[f"{name}_points"] = record["scores"][name]
                row[f"{name}_win_credit"] = record["win_credit"][name]
            writer.writerow(row)
    print(f"Saved {args.output / 'report.md'}")


if __name__ == "__main__":
    main()
