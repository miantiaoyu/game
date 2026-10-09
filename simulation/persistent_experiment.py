"""Persistent-bankroll economics and matched swap-window order experiments.

Confidence intervals use independent matches. A match contributes one mean
over all its eligible windows, including windows in which nobody swaps.
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from concurrent.futures import ProcessPoolExecutor
from dataclasses import asdict, replace
import csv
import json
import math
from multiprocessing import get_context
from pathlib import Path
import random
from statistics import mean, pstdev, stdev

from .persistent_model import Config

PROFILES = {
    "flat": (500, 500),
    "baseline": (500, 1000),
    "steep": (300, 1500),
    "very_steep": (200, 2000),
    "no_swap": (500, 1000),
}
DEFAULT_PROFILES = ("flat", "baseline", "steep", "very_steep")
LABELS = {
    "flat": "5%–5%",
    "baseline": "5%–10%",
    "steep": "3%–15%",
    "very_steep": "2%–20%",
    "no_swap": "禁止改航",
}


def profile_config(name: str, reverse: bool = False) -> Config:
    low, high = PROFILES[name]
    return replace(Config(), low_bps=low, high_bps=high, swaps_enabled=name != "no_swap", reverse_swaps=reverse)


def interval(values: list[float]) -> dict:
    """Normal-approximation interval over already-independent match values."""
    if not values:
        return {"mean": None, "ci95": None, "matches": 0}
    estimate = mean(values)
    half = 1.96 * stdev(values) / math.sqrt(len(values)) if len(values) >= 2 else None
    return {"mean": estimate, "ci95": [estimate - half, estimate + half] if half is not None else None, "matches": len(values)}


def paired_effect(normal: dict, reverse: dict) -> dict | None:
    """Positive means an early-order benefit, holding player identities fixed."""
    order = normal["window_order"]
    if reverse["window_order"] != list(reversed(order)):
        raise AssertionError("paired branches changed window membership or did not reverse it")
    if normal["hand_index"] != reverse["hand_index"] or normal["start_stacks"] != reverse["start_stacks"]:
        raise AssertionError("paired branches did not start at the same hand")
    if len(order) < 2:
        return None
    normal_net = [b - a for a, b in zip(normal["start_stacks"], normal["end_stacks"])]
    reverse_net = [b - a for a, b in zip(reverse["start_stacks"], reverse["end_stacks"])]
    first, last = order[0], order[-1]
    first_gain = normal_net[first] - reverse_net[first]
    last_gain = reverse_net[last] - normal_net[last]
    return {
        "hand_index": normal["hand_index"],
        "participants": len(order),
        "window_order": list(order),
        "first": first,
        "last": last,
        "normal_net": normal_net,
        "reverse_net": reverse_net,
        "original_first_early_gain": first_gain,
        "original_last_early_gain": last_gain,
        "early_advantage": (first_gain + last_gain) / 2,
    }


def aggregate_pairs(rows: list[dict]) -> dict:
    def group_result(group: list[dict]) -> dict:
        per_match = defaultdict(list)
        for row in group:
            per_match[row["game"]].append(row["early_advantage"])
        values = {game: mean(effects) for game, effects in sorted(per_match.items())}
        return {
            **interval(list(values.values())),
            "windows": len(group),
            "match_means": [{"game": game, "early_advantage": value} for game, value in values.items()],
        }

    return {
        "all": group_result(rows),
        "by_participants": {str(count): group_result([r for r in rows if r["participants"] == count]) for count in range(2, Config().players + 1)},
    }


def paired_profile_difference(candidate: list[dict], baseline: list[dict]) -> dict:
    """Difference of within-match order effects; pair by immutable match ID."""
    current = {row["game"]: row["early_advantage"] for row in candidate}
    reference = {row["game"]: row["early_advantage"] for row in baseline}
    if len(current) != len(candidate) or len(reference) != len(baseline):
        raise AssertionError("duplicate match ID in profile comparison")
    if current.keys() != reference.keys():
        raise AssertionError("profiles do not share the same independent matches")
    differences = [{"game": game, "early_advantage_difference": current[game] - reference[game]} for game in sorted(current)]
    return {
        **interval([row["early_advantage_difference"] for row in differences]),
        "match_differences": differences,
    }


def decision_rows(hand: dict, game: int, seed: int, profile: str, scope: str) -> list[dict]:
    rows = []
    for decision in hand["decisions"]:
        player = decision["player"]
        swapped = decision["slot"] is not None
        rows.append({
            "scope": scope, "profile": profile, "game": game, "seed": seed,
            "hand_index": hand["hand_index"], "player": player,
            "rank": decision["rank"], "participants": decision["count"],
            "fee_quote": decision["fee"], "fee_paid": decision["fee"] if swapped else 0,
            "exposure": decision.get("exposure"), "affordable": decision.get("affordable"),
            "swapped": int(swapped), "slot": decision["slot"],
            "overwritten": int(decision["overwritten"]),
            "start_stack": hand["start_stacks"][player], "end_stack": hand["end_stacks"][player],
            "net_chips": hand["end_stacks"][player] - hand["start_stacks"][player],
        })
    return rows


def decision_summary(rows: list[dict]) -> dict:
    swaps = [row for row in rows if row["swapped"]]
    return {
        "opportunities": len(rows), "swaps": len(swaps),
        "swap_rate": len(swaps) / len(rows) if rows else None,
        "overwritten": sum(row["overwritten"] for row in swaps),
        "overwritten_rate_per_swap": sum(row["overwritten"] for row in swaps) / len(swaps) if swaps else None,
        "fees_paid": sum(row["fee_paid"] for row in rows),
        "mean_fee_quote": mean(row["fee_quote"] for row in rows) if rows else None,
        "mean_fee_per_swap": mean(row["fee_paid"] for row in swaps) if swaps else None,
        "mean_net_chips_descriptive": mean(row["net_chips"] for row in rows) if rows else None,
    }


def positions_summary(rows: list[dict]) -> list[dict]:
    strata = defaultdict(list)
    for row in rows:
        strata[row["participants"], row["rank"]].append(row)
    return [{"participants": count, "rank": rank, **decision_summary(group)} for (count, rank), group in sorted(strata.items())]


def summarize_match(record: dict, game: int, seed: int, profile: str, initial_button: int) -> dict:
    cfg = profile_config(profile)
    final = record["final_stacks"]
    if sum(final) != cfg.players * cfg.initial_chips:
        raise AssertionError("final bankroll is not conserved")
    all_decisions = [d for hand in record["hands"] for d in hand["decisions"]]
    swaps = [d for d in all_decisions if d["slot"] is not None]
    for hand in record["hands"]:
        if sum(hand["start_stacks"]) != sum(hand["end_stacks"]):
            raise AssertionError("hand bankroll is not conserved")
    start_positions = []
    for hand in record["hands"]:
        for rank, player in enumerate(hand["start_order"], 1):
            start_positions.append({"participants": len(hand["start_order"]), "rank": rank,
                                    "net_chips": hand["end_stacks"][player] - hand["start_stacks"][player]})
    return {
        "profile": profile, "game": game, "seed": seed, "initial_button": initial_button,
        "hands_played": len(record["hands"]), "final_stacks": final,
        "net_chips": [stack - cfg.initial_chips for stack in final],
        "busted_players": sum(stack == 0 for stack in final),
        "stack_stddev": pstdev(final), "largest_stack": max(final), "smallest_stack": min(final),
        "opportunities": len(all_decisions), "swaps": len(swaps),
        "fees_paid": sum(d["fee"] for d in swaps),
        "overwritten_swaps": sum(d["overwritten"] for d in swaps),
        "side_pot_hands": sum(bool(hand["had_side_pot"]) for hand in record["hands"]),
        "all_in_events": sum(hand["all_in_count"] for hand in record["hands"]),
        "windows_by_participants": dict(Counter(len(hand["window_order"]) for hand in record["hands"] if hand["window_order"])),
        "start_positions": start_positions,
    }


def run_job(job: tuple[int, int, int, tuple[str, ...], str, float]) -> dict:
    # Lazy imports keep the statistical estimands independently testable.
    from .persistent_game import finish_window, run_match
    from .persistent_policy import EquityPolicy

    game, seed, samples, profiles, mode, aggression = job
    initial_button = game % Config().players
    policy = EquityPolicy(samples=samples, aggression=aggression)
    need_baseline = mode in ("paired", "both") or game < 3
    baseline = run_match(seed, policy, profile_config("baseline"), initial_button=initial_button, capture_windows=mode in ("paired", "both"), trace=game < 3) if need_baseline else None
    matches, windows, pairs = [], [], []
    if mode in ("full", "both"):
        for profile in profiles:
            record = baseline if profile == "baseline" and baseline is not None else run_match(seed, policy, profile_config(profile), initial_button=initial_button)
            matches.append(summarize_match(record, game, seed, profile, initial_button))
            for hand in record["hands"]:
                windows.extend(decision_rows(hand, game, seed, profile, "trajectory"))
    if mode in ("paired", "both"):
        for snapshot in baseline["windows"]:
            reference_order = None
            for profile in profiles:
                normal = finish_window(snapshot, policy, profile_config(profile))
                reverse = finish_window(snapshot, policy, profile_config(profile, reverse=True))
                if reference_order is None:
                    reference_order = normal["window_order"]
                elif normal["window_order"] != reference_order:
                    raise AssertionError("profile changed the common baseline window membership")
                paired = paired_effect(normal, reverse)
                if paired is not None:
                    pairs.append({"profile": profile, "game": game, "seed": seed, **paired})
                windows.extend(decision_rows(normal, game, seed, profile, "paired_normal"))
                windows.extend(decision_rows(reverse, game, seed, profile, "paired_reverse"))
    trace = None
    if game < 3 and baseline is not None:
        trace = {"profile": "baseline", "game": game, "seed": seed, "initial_button": initial_button, "final_stacks": baseline["final_stacks"], "hands": baseline["hands"]}
    return {"matches": matches, "windows": windows, "pairs": pairs, "trace": trace}


def economy_summary(matches: list[dict], decisions: list[dict]) -> dict:
    total_hands = sum(row["hands_played"] for row in matches)
    total_players = len(matches) * Config().players
    outcomes = []
    for seat in range(Config().players):
        wins = []
        for row in matches:
            maximum = max(row["final_stacks"])
            ties = row["final_stacks"].count(maximum)
            wins.append((1 / ties) if row["final_stacks"][seat] == maximum else 0)
        outcomes.append({
            "seat": seat,
            "mean_final_chips": mean(row["final_stacks"][seat] for row in matches) if matches else None,
            "mean_net_chips": mean(row["net_chips"][seat] for row in matches) if matches else None,
            "championship_win_share": mean(wins) if wins else None,
        })
    support = Counter()
    start_positions = defaultdict(list)
    for row in matches:
        support.update(row["windows_by_participants"])
        for position in row["start_positions"]:
            start_positions[position["participants"], position["rank"]].append(position["net_chips"])
    return {
        "matches": len(matches), "hands": total_hands,
        "mean_hands_played": mean(row["hands_played"] for row in matches) if matches else None,
        "mean_largest_stack": mean(row["largest_stack"] for row in matches) if matches else None,
        "mean_stack_stddev": mean(row["stack_stddev"] for row in matches) if matches else None,
        "bust_rate_per_player": sum(row["busted_players"] for row in matches) / total_players if total_players else None,
        "matches_with_bust_rate": mean(row["busted_players"] > 0 for row in matches) if matches else None,
        "side_pot_hand_rate": sum(row["side_pot_hands"] for row in matches) / total_hands if total_hands else None,
        "all_in_events_per_hand": sum(row["all_in_events"] for row in matches) / total_hands if total_hands else None,
        "windows_by_participants": dict(sorted(support.items())),
        "decisions": decision_summary(decisions),
        "positions_descriptive": positions_summary(decisions),
        "start_positions_descriptive": [{"participants": count, "rank": rank, "hands": len(values), "mean_net_chips": mean(values)} for (count, rank), values in sorted(start_positions.items())],
        "seats_descriptive": outcomes,
    }


def aggregate(results: list[dict], config: dict) -> tuple[dict, list[dict], list[dict], list[dict], list[dict]]:
    matches = [r for result in results for r in result["matches"]]
    windows = [r for result in results for r in result["windows"]]
    pairs = [r for result in results for r in result["pairs"]]
    traces = [result["trace"] for result in results if result["trace"] is not None]
    summary = {
        "config": config,
        "rules": asdict(Config()),
        "estimand": "early_advantage = 0.5*((netNormal[first]-netReverse[first])+(netReverse[last]-netNormal[last])); all baseline windows included, averaged within match, then equally across independent matches",
        "interval_method": "normal approximation mean +/- 1.96*sample_sd/sqrt(independent matches); fewer than two matches gives null interval",
        "profiles": {},
    }
    for profile in config["profiles"]:
        profile_matches = [r for r in matches if r["profile"] == profile]
        profile_windows = [r for r in windows if r["profile"] == profile]
        summary["profiles"][profile] = {
            "label": LABELS[profile], "rules": asdict(profile_config(profile)),
            "economy": economy_summary(profile_matches, [r for r in profile_windows if r["scope"] == "trajectory"]),
            "paired_order": aggregate_pairs([r for r in pairs if r["profile"] == profile]),
            "paired_normal_positions_descriptive": positions_summary([r for r in profile_windows if r["scope"] == "paired_normal"]),
        }
    if "baseline" in summary["profiles"]:
        reference = summary["profiles"]["baseline"]["paired_order"]
        for item in summary["profiles"].values():
            candidate = item["paired_order"]
            item["paired_order_minus_baseline"] = {
                "all": paired_profile_difference(candidate["all"]["match_means"], reference["all"]["match_means"]),
                "by_participants": {
                    participants: paired_profile_difference(result["match_means"], reference["by_participants"][participants]["match_means"])
                    for participants, result in candidate["by_participants"].items()
                },
            }
    return summary, matches, windows, pairs, traces


def formatted_interval(result: dict) -> str:
    if result["mean"] is None:
        return "无适用窗口"
    if result["ci95"] is None:
        return f"{result['mean']:+.3f}（不足两场，无区间）"
    return f"{result['mean']:+.3f} [{result['ci95'][0]:+.3f}, {result['ci95'][1]:+.3f}]"


def percentage(value: float | None) -> str:
    return "—" if value is None else f"{value:.2%}"


def number(value: float | None) -> str:
    return "—" if value is None else f"{value:.3f}"


def report_markdown(summary: dict) -> str:
    config = summary["config"]
    lines = [
        "# 持续筹码与动态改航费实验", "",
        f"模式 `{config['mode']}`；每组 {config['games']} 个独立整场种子，每次概率估计 {config['samples']} 个样本，主种子 {config['seed']}。五名玩家采用相同局部概率启发式，aggression={config.get('aggression', 1.0)}。", "",
        "每人起始 100 枚筹码，整场最多 12 局，前六局底注 1、后六局底注 2，筹码跨局保留。27 张牌为三种花色各 1—9；两张底牌、三张公共牌，最终三张至少用一张底牌。", "",
        "## 实验口径", "",
        "- 完整轨迹实验分别让各费率运行整个 12 局，可观察筹码集中、破产、全下、边池和改航参与度。同一场编号共用初始随机种子，各费率的后续状态允许自然分化。",
        "- 配对窗口统一取自 5%–10% 基准轨迹，在报价前保存状态；同一个状态分别按每个费率续跑正序、逆序。纳入全部可比较窗口，包括无人改航的窗口，不按改航是否发生筛选。",
        "- 首位为 a、末位为 b，指标为 ½×[(正序收益a−逆序收益a)+(逆序收益b−正序收益b)]。正值表示先手收益优势，负值表示后手优势。",
        "- 每场先对其窗口指标取均值，再对独立整场等权计算均值和 95% 正态近似区间。两场以下不显示区间；不把玩家或同场各局当作独立样本。",
        "- 只反转改航窗口，后续下注顺序保持原状。指标包含动态费用与其他玩家响应，不是纯信息价值，也不是整局下注顺序差异。",
        "- 动态百分比从窗口首位线性增加到末位，基数是窗口开始时该玩家有资格争夺、且至少两人争夺的常规底池金额；向上取整且不低于本局底注。报价一次锁定，不因前人缴费而上涨；仅一名可操作玩家时使用末位百分比。",
        "- 配对优势仅适用于至少两名可操作玩家的窗口；单人窗口仍进入完整轨迹参与度和费用统计。全下者不能改航，付不起报价者保留顺位但放弃。",
        "- 每场的起始按钮为场编号模 5，避免 12 局不能整除五人造成系统性座位偏差；每场独立洗牌。", "",
        "## 整场经济表现", "",
        "| 费率 | 完整场数 | 改航率/机会 | 改航均费 | 被覆盖率/改航 | 破产率/玩家 | 边池率/局 | 平均最大筹码 | 平均桌内筹码标准差 |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for profile in config["profiles"]:
        item = summary["profiles"][profile]
        econ, dec = item["economy"], item["economy"]["decisions"]
        lines.append(f"| {item['label']} | {econ['matches']} | {percentage(dec['swap_rate'])} | {number(dec['mean_fee_per_swap'])} | {percentage(dec['overwritten_rate_per_swap'])} | {percentage(econ['bust_rate_per_player'])} | {percentage(econ['side_pot_hand_rate'])} | {number(econ['mean_largest_stack'])} | {number(econ['mean_stack_stddev'])} |")
    lines.extend(["", "筹码总量每场恒为 500，五人的平均净收益恒为零，因此不将全体平均得分当作某费率更优的证据。", "", "## 同一窗口的先手收益优势", "", "| 费率 | 全部窗口均值与 95% 区间 | 独立场数 | 窗口数 |", "| --- | --- | ---: | ---: |"])
    for profile in config["profiles"]:
        result = summary["profiles"][profile]["paired_order"]["all"]
        lines.append(f"| {LABELS[profile]} | {formatted_interval(result)} | {result['matches']} | {result['windows']} |")
    if "baseline" in summary["profiles"]:
        lines.extend(["", "以下比较各费率相对 5%–10% 基准的先手优势变化。先对齐同一场的窗口平均指标并作差，再用这些独立整场差值计算区间；不是把两个独立区间相减。正值表示相较基准更偏向先手。", "", "| 费率减基准 | 指标变化与 95% 区间 | 配对场数 |", "| --- | --- | ---: |"])
        for profile in config["profiles"]:
            result = summary["profiles"][profile]["paired_order_minus_baseline"]["all"]
            lines.append(f"| {LABELS[profile]} − 5%–10% | {formatted_interval(result)} | {result['matches']} |")
    lines.extend(["", "目标是先手略有收益优势；若均值为正但区间跨零，只能视为候选，不能认定已经达到目标。多费率筛选得到的候选应使用新种子独立验证，筛选数据的区间不构成挑选后的确认性证据。", "", "| 费率 | 窗口人数 | 优势均值与 95% 区间 | 场数 | 窗口数 |", "| --- | ---: | --- | ---: | ---: |"])
    for profile in config["profiles"]:
        for participants, result in summary["profiles"][profile]["paired_order"]["by_participants"].items():
            lines.append(f"| {LABELS[profile]} | {participants} | {formatted_interval(result)} | {result['matches']} | {result['windows']} |")
    lines.extend(["", "## 完整轨迹改航顺位描述", "", "以下每格包含该人数、该顺位的所有机会；净收益是整局净收益，受谁能进入窗口及起始牌况影响，只作描述，不能替代同状态配对比较。顺位从 1 开始。", "", "| 费率 | 人数 | 顺位 | 机会数 | 改航率 | 平均报价 | 平均净收益 |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: |"])
    for profile in config["profiles"]:
        for row in summary["profiles"][profile]["economy"]["positions_descriptive"]:
            lines.append(f"| {LABELS[profile]} | {row['participants']} | {row['rank']} | {row['opportunities']} | {percentage(row['swap_rate'])} | {number(row['mean_fee_quote'])} | {number(row['mean_net_chips_descriptive'])} |")
    lines.extend(["", "## 每局起始下注顺位描述", "", "按每局开始时仍有筹码的玩家数和下注顺位汇总，包含后来弃牌的玩家；这不是玩家身份或改航顺位。存活者、筹码和牌况筛选都会影响均值，以下数据不能作为下注顺序的因果效应。", "", "| 费率 | 开局人数 | 起始下注顺位 | 玩家局数 | 平均净收益 |", "| --- | ---: | ---: | ---: | ---: |"])
    for profile in config["profiles"]:
        for row in summary["profiles"][profile]["economy"]["start_positions_descriptive"]:
            lines.append(f"| {LABELS[profile]} | {row['participants']} | {row['rank']} | {row['hands']} | {number(row['mean_net_chips'])} |")
    lines.extend(["", "## 解释边界与复现", "",
        "- 策略只按当前可见状态进行局部概率估值，不预测后续玩家覆盖改航的风险、未来下注过程，也不根据对手动作推断其手牌范围；不会完整求解多人不完全信息博弈，结果不能证明数学平衡或真人体验。",
        "- 统一基准窗口避免不同费率各自选择出的玩家和牌况造成比较偏差，但结果只适用于基准策略产生的状态分布。窗口之外的完整轨迹效应另见经济指标。",
        "- 正逆两支使用相同窗口快照和随机状态；不同动作会改变后续抽牌/决策路径，因此并非保证每个后续事件完全相同。",
        "- `matches.csv` 保存整场筹码和经济指标；`windows.csv` 保存所有改航机会，含完整轨迹、配对正序、配对逆序；`paired-windows.csv` 保存每个窗口的两支收益与指标；`trace-0.json` 至 `trace-2.json` 保存前三场基准过程（不足三场时按实际数量）。",
        "", "```bash",
        f"python3 -m simulation.persistent_experiment --games {config['games']} --samples {config['samples']} --seed {config['seed']} --workers {config['workers']} --aggression {config.get('aggression', 1.0)} --mode {config['mode']} --profiles {','.join(config['profiles'])} --output {config['output']}",
        "```", "",
    ])
    return "\n".join(lines)


def write_csv(path: Path, rows: list[dict]) -> None:
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    with path.open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        for row in rows:
            writer.writerow({key: json.dumps(value, ensure_ascii=False, separators=(",", ":")) if isinstance(value, (dict, list, tuple)) else value for key, value in row.items()})


def write_outputs(output: Path, results: list[dict], config: dict) -> dict:
    output.mkdir(parents=True, exist_ok=True)
    summary, matches, windows, pairs, traces = aggregate(results, config)
    (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (output / "report.md").write_text(report_markdown(summary), encoding="utf-8")
    write_csv(output / "matches.csv", matches)
    write_csv(output / "windows.csv", windows)
    write_csv(output / "paired-windows.csv", pairs)
    for trace in traces:
        (output / f"trace-{trace['game']}.json").write_text(json.dumps(trace, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return summary


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--games", type=int, default=1000)
    parser.add_argument("--samples", type=int, default=64)
    parser.add_argument("--seed", type=int, default=20261008)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--aggression", type=float, default=1.0)
    parser.add_argument("--mode", choices=("full", "paired", "both"), default="both")
    parser.add_argument("--profiles", default=",".join(DEFAULT_PROFILES))
    parser.add_argument("--output", type=Path, default=Path("reports/persistent-screen"))
    args = parser.parse_args()
    profiles = tuple(part.strip() for part in args.profiles.split(",") if part.strip())
    if not profiles or len(profiles) != len(set(profiles)) or any(profile not in PROFILES for profile in profiles):
        parser.error(f"profiles must be distinct members of {','.join(PROFILES)}")
    if args.games < 1 or args.samples < 1 or args.workers < 1:
        parser.error("games, samples, and workers must be positive")
    if not math.isfinite(args.aggression) or args.aggression <= 0:
        parser.error("aggression must be finite and positive")
    rng = random.Random(args.seed)
    seeds = [rng.getrandbits(63) for _ in range(args.games)]
    if len(set(seeds)) != len(seeds):
        raise AssertionError("duplicate independent match seed")
    jobs = [(game, seed, args.samples, profiles, args.mode, args.aggression) for game, seed in enumerate(seeds)]
    results = []
    if args.workers == 1:
        for index, job in enumerate(jobs, start=1):
            results.append(run_job(job))
            if index == 1 or index % max(1, args.games // 20) == 0 or index == args.games:
                print(f"Completed {index}/{args.games} independent match seeds", flush=True)
    else:
        with ProcessPoolExecutor(max_workers=args.workers, mp_context=get_context("spawn")) as pool:
            for index, result in enumerate(pool.map(run_job, jobs, chunksize=1), start=1):
                results.append(result)
                if index == 1 or index % max(1, args.games // 20) == 0 or index == args.games:
                    print(f"Completed {index}/{args.games} independent match seeds", flush=True)
    config = {**vars(args), "profiles": list(profiles), "output": str(args.output)}
    summary = write_outputs(args.output, results, config)
    for profile in profiles:
        print(f"{LABELS[profile]} early advantage: {formatted_interval(summary['profiles'][profile]['paired_order']['all'])}")
    print(f"Saved experiment to {args.output}")


if __name__ == "__main__":
    main()
