"""Run full-match normal/reversed swap-order experiments.

Betting positions always come from each hand's original start_order, including
players who later fold. Match-level averages, not seats or hands, are the
independent units in both intervals and paired treatment contrasts.
"""

from __future__ import annotations

import argparse
from collections import defaultdict
from concurrent.futures import ProcessPoolExecutor
from dataclasses import asdict
import json
import math
from multiprocessing import get_context
from pathlib import Path
import random
from statistics import mean

from .persistent_experiment import (
    decision_rows,
    economy_summary,
    formatted_interval,
    interval,
    number,
    percentage,
    profile_config,
    summarize_match,
    write_csv,
)
from .persistent_model import Config

VARIANTS = ("baseline_normal", "baseline_reverse", "steep_normal", "steep_reverse")
LABELS = {
    "baseline_normal": "5%–10%，改航正序",
    "baseline_reverse": "5%–10%，改航逆序",
    "steep_normal": "3%–15%，改航正序",
    "steep_reverse": "3%–15%，改航逆序",
}
METRICS = ("first_net", "last_net", "net_first_minus_last", "first_profit", "last_profit", "profit_first_minus_last",
           "first_in_window", "last_in_window", "first_fee_paid", "last_fee_paid")


def variant_config(variant: str) -> Config:
    regime, direction = variant.rsplit("_", 1)
    if variant not in VARIANTS:
        raise ValueError(f"Unknown variant: {variant}")
    return profile_config(regime, reverse=direction == "reverse")


def hand_positions(hand: dict) -> dict:
    order = list(hand["start_order"])
    if len(order) < 2:
        raise AssertionError("a played hand needs two original betting positions")
    net = [end - start for start, end in zip(hand["start_stacks"], hand["end_stacks"])]
    profit = [int(value > 0) for value in net]
    first, last = order[0], order[-1]
    payments = {d["player"]: d["fee"] for d in hand.get("decisions", []) if d["slot"] is not None}
    return {
        "hand_index": hand["hand_index"], "participants": len(order),
        "start_order": order, "window_order": list(hand["window_order"]),
        "first_player": first, "last_player": last,
        "first_in_window": int(first in hand["window_order"]),
        "last_in_window": int(last in hand["window_order"]),
        "first_fee_paid": payments.get(first, 0), "last_fee_paid": payments.get(last, 0),
        "start_stacks": list(hand["start_stacks"]), "end_stacks": list(hand["end_stacks"]),
        "net_by_player": net, "profit_by_player": profit,
        "first_net": net[first], "last_net": net[last],
        "net_first_minus_last": net[first] - net[last],
        "first_profit": profit[first], "last_profit": profit[last],
        "profit_first_minus_last": profit[first] - profit[last],
    }


def match_position_means(hands: list[dict], participants: int | None = None) -> dict | None:
    selected = [hand for hand in hands if participants is None or hand["participants"] == participants]
    if not selected:
        return None
    return {"hands": len(selected), **{metric: mean(hand[metric] for hand in selected) for metric in METRICS}}


def positional_summary(matches: list[dict], scope: str) -> dict:
    selected = [row for row in matches if row["position_means"][scope] is not None]
    return {
        "matches": len(selected),
        "hands": sum(row["position_means"][scope]["hands"] for row in selected),
        **{metric: interval([row["position_means"][scope][metric] for row in selected]) for metric in METRICS},
    }


def paired_position_change(normal: list[dict], reverse: list[dict], scope: str) -> dict:
    left = {row["game"]: row for row in normal}
    right = {row["game"]: row for row in reverse}
    if len(left) != len(normal) or len(right) != len(reverse) or left.keys() != right.keys():
        raise AssertionError("paired trajectory match IDs differ or repeat")
    differences = []
    for game in sorted(left):
        a, b = left[game], right[game]
        if (a["seed"], a["initial_button"]) != (b["seed"], b["initial_button"]):
            raise AssertionError("paired trajectories have different seed/button")
        x, y = a["position_means"][scope], b["position_means"][scope]
        if x is None or y is None:
            continue
        differences.append({"game": game, **{metric: y[metric] - x[metric] for metric in METRICS}})
    return {
        "matches": len(differences),
        **{metric: interval([row[metric] for row in differences]) for metric in METRICS},
        "match_differences": differences,
    }


def per_position_summary(hands: list[dict], participants: int = 5) -> list[dict]:
    """Each position receives one observation per match after within-match mean."""
    strata = defaultdict(lambda: defaultdict(list))
    for hand in hands:
        if hand["participants"] != participants:
            continue
        for rank, player in enumerate(hand["start_order"], 1):
            strata[rank][hand["game"]].append((hand["net_by_player"][player], hand["profit_by_player"][player]))
    result = []
    for rank, games in sorted(strata.items()):
        net = [mean(pair[0] for pair in values) for values in games.values()]
        profit = [mean(pair[1] for pair in values) for values in games.values()]
        result.append({"participants": participants, "rank": rank, "hands": sum(len(values) for values in games.values()),
                       "net_chips": interval(net), "profit_rate": interval(profit)})
    return result


def run_job(job: tuple[int, int, int, float]) -> dict:
    from .persistent_game import run_match
    from .persistent_policy import EquityPolicy

    game, seed, samples, aggression = job
    button = game % Config().players
    policy = EquityPolicy(samples=samples, aggression=aggression)
    matches, hands, decisions, traces = [], [], [], []
    for variant in VARIANTS:
        regime, direction = variant.rsplit("_", 1)
        tracing = game < 3 and direction == "reverse"
        record = run_match(seed, policy, variant_config(variant), initial_button=button, trace=tracing)
        match = summarize_match(record, game, seed, regime, button)
        match["profile"] = variant
        positions = [{"variant": variant, "game": game, "seed": seed, **hand_positions(hand)} for hand in record["hands"]]
        match["position_means"] = {
            "all": match_position_means(positions),
            "five_players": match_position_means(positions, participants=5),
        }
        matches.append(match)
        hands.extend(positions)
        for hand in record["hands"]:
            decisions.extend(decision_rows(hand, game, seed, variant, "trajectory"))
        if tracing:
            traces.append({"variant": variant, "game": game, "seed": seed, "initial_button": button,
                           "final_stacks": record["final_stacks"], "hands": record["hands"]})
    return {"matches": matches, "hands": hands, "decisions": decisions, "traces": traces}


def aggregate(results: list[dict], config: dict) -> tuple[dict, list[dict], list[dict], list[dict], list[dict]]:
    matches = [row for result in results for row in result["matches"]]
    hands = [row for result in results for row in result["hands"]]
    decisions = [row for result in results for row in result["decisions"]]
    traces = [row for result in results for row in result["traces"]]
    summary = {
        "config": config,
        "estimand": "For each hand take original start_order first and last, including later folders; net is end-start, profit is 1(net>0). Average each metric within match, then equally across independent matches. Paired change is reverse minus normal for the same match seed/button.",
        "interval_method": "mean +/- 1.96*sample_sd/sqrt(independent matches); fewer than two matches returns null CI",
        "variants": {}, "reverse_minus_normal": {},
    }
    for variant in VARIANTS:
        selected = [row for row in matches if row["profile"] == variant]
        selected_hands = [row for row in hands if row["variant"] == variant]
        summary["variants"][variant] = {
            "label": LABELS[variant], "rules": asdict(variant_config(variant)),
            "economy": economy_summary(selected, [row for row in decisions if row["profile"] == variant]),
            "positions": {scope: positional_summary(selected, scope) for scope in ("all", "five_players")},
            "five_player_ranks": per_position_summary(selected_hands),
        }
    for regime in ("baseline", "steep"):
        normal = [row for row in matches if row["profile"] == f"{regime}_normal"]
        reverse = [row for row in matches if row["profile"] == f"{regime}_reverse"]
        summary["reverse_minus_normal"][regime] = {
            scope: paired_position_change(normal, reverse, scope) for scope in ("all", "five_players")
        }
    return summary, matches, hands, decisions, traces


def percent_interval(result: dict) -> str:
    if result["mean"] is None:
        return "无数据"
    if result["ci95"] is None:
        return f"{100 * result['mean']:+.2f} pp（无区间）"
    return f"{100 * result['mean']:+.2f} pp [{100 * result['ci95'][0]:+.2f}, {100 * result['ci95'][1]:+.2f}]"


def report_markdown(summary: dict) -> str:
    c = summary["config"]
    lines = ["# 改航顺序反转：完整持续筹码实验", "",
             f"四组各 {c['games']} 场，samples={c['samples']}，seed={c['seed']}，aggression={c['aggression']}。每人初始 100 筹码，最多 12 局，前六局底注 1、后六局底注 2。只剩一人有筹码时提前结束。", "",
             "## 顺序与统计定义", "",
             "- 对照沿每局原始下注顺序改航；实验组只将改航窗口顺序反转。三轮下注顺序保持原规则；费用按实际改航顺位从低到高收取，所以仍能改航的原下注后手更早改航、原下注先手更晚改航并面临较高费率。个人可争夺底池和底注下限也会影响实际报价。",
             "- 使用原有 5%–10% 和 3%–15% 费率；报价按窗口开始时有资格争夺的常规底池金额计算，向上取整且不低于底注，窗口内锁定。",
             "- 每局先手与后手严格取该局 `start_order[0]` 和 `start_order[-1]`，包括后来弃牌、全下、未能改航的玩家。不是按改航窗口重新挑首末，也不是把某个固定身份的最终筹码当成位置收益。",
             "- 净收益为该局结束筹码减开局筹码；盈利指净收益严格大于零。盈利率不是摊牌胜率，分到奖池但净亏损仍不算盈利。",
             "- 每场先对各局的“原下注先手减后手”取均值，再跨独立整场等权平均并给出 95% 正态近似区间。盈利率差以百分点表示；不足两场不显示区间。",
             "- 全人数指标混合每场实际经历的二至五人开局：每局在场内等权、各场再等权。另报告五人开局子组。提前出局会影响后续人数和位置成员，这属于完整规则轨迹的一部分。",
             "- 正逆组使用同一场种子和初始按钮（场编号模 5）；筹码、存活人数和后续行动允许自然分化。整场配对差先按场编号计算逆序减正序，再对差值给出区间；不直接相减两个区间。", "",
             "## 完整轨迹经济表现", "",
             "| 组别 | 实际局数 | 改航率/机会 | 平均改航费 | 覆盖率/改航 | 破产率/玩家 | 边池率/局 | 平均最大筹码 |",
             "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for variant in VARIANTS:
        e = summary["variants"][variant]["economy"]
        d = e["decisions"]
        lines.append(f"| {LABELS[variant]} | {e['hands']} | {percentage(d['swap_rate'])} | {number(d['mean_fee_per_swap'])} | {percentage(d['overwritten_rate_per_swap'])} | {percentage(e['bust_rate_per_player'])} | {percentage(e['side_pot_hand_rate'])} | {number(e['mean_largest_stack'])} |")
    for scope, label in (("all", "全部开局人数"), ("five_players", "仅五人开局")):
        lines.extend(["", f"## 原下注先手减后手：{label}", "", "正值偏向原下注先手，负值偏向原下注后手。", "",
                      "| 组别 | 净收益差及 95% 区间 | 盈利率差及 95% 区间 | 独立场数 | 局数 |",
                      "| --- | --- | --- | ---: | ---: |"])
        for variant in VARIANTS:
            p = summary["variants"][variant]["positions"][scope]
            lines.append(f"| {LABELS[variant]} | {formatted_interval(p['net_first_minus_last'])} | {percent_interval(p['profit_first_minus_last'])} | {p['matches']} | {p['hands']} |")
        lines.extend(["", "同场逆序减正序：正值表示反转改航后，原下注先手相对后手的收益差向上移动。此表同时分别列出两端的变化，避免把总差解释为先手单独获益。", "",
                      "| 费率 | 首末净收益差变化 | 先手自身净收益变化 | 后手自身净收益变化 | 首末盈利率差变化 | 配对场数 |",
                      "| --- | --- | --- | --- | --- | ---: |"])
        for regime in ("baseline", "steep"):
            p = summary["reverse_minus_normal"][regime][scope]
            lines.append(f"| {'5%–10%' if regime == 'baseline' else '3%–15%'} | {formatted_interval(p['net_first_minus_last'])} | {formatted_interval(p['first_net'])} | {formatted_interval(p['last_net'])} | {percent_interval(p['profit_first_minus_last'])} | {p['matches']} |")
    lines.extend(["", "## 原下注首末的改航机会与实际支出", "",
                  "覆盖全部开局人数，先场内均值再跨场均值。进入名单不代表实际改航；实际支出将未进入、放弃、付不起费用均记为零。高费率可能减少发动次数，所以平均实际支出未必更高。", "",
                  "| 组别 | 首位进入改航名单率 | 末位进入改航名单率 | 首位每局平均实际改航支出 | 末位每局平均实际改航支出 |",
                  "| --- | ---: | ---: | --- | --- |"])
    for variant in VARIANTS:
        p = summary["variants"][variant]["positions"]["all"]
        lines.append(f"| {LABELS[variant]} | {percentage(p['first_in_window']['mean'])} | {percentage(p['last_in_window']['mean'])} | {formatted_interval(p['first_fee_paid'])} | {formatted_interval(p['last_fee_paid'])} |")
    lines.extend(["", "## 五人开局各下注顺位", "", "每个顺位先在场内平均，再跨场平均；包含随后弃牌者。", "",
                  "| 组别 | 原下注顺位 | 平均净收益及 95% 区间 | 盈利率及 95% 区间 | 独立场数 | 玩家局数 |",
                  "| --- | ---: | --- | --- | ---: | ---: |"])
    for variant in VARIANTS:
        for rank in summary["variants"][variant]["five_player_ranks"]:
            p = rank["profit_rate"]
            ci = p["ci95"]
            profit = f"{p['mean']:.2%}" + (f" [{ci[0]:.2%}, {ci[1]:.2%}]" if ci is not None else "（无区间）")
            lines.append(f"| {LABELS[variant]} | {rank['rank']} | {formatted_interval(rank['net_chips'])} | {profit} | {rank['net_chips']['matches']} | {rank['hands']} |")
    lines.extend(["", "## 边界与复现", "",
                  "- 策略实际响应其他玩家改航后的新公共牌，但没有根据“谁改了哪张牌、谁放弃”推断对手手牌范围。改航作为心理信号的价值尚未建模。",
                  "- 策略也不预测后续改航覆盖或未来下注，仍是局部概率启发式。结论适用于这组机器人，不能证明真人博弈平衡。",
                  "- 反转顺序同时交换信息先后、被覆盖机会及费用顺位，测量的是这套规则变化的共同效果。后续筹码和存活者分化，因此整场比较不是固定同一窗口或固定同一玩家的反事实。",
                  "- 本轮没有重复局部窗口快照实验。五人子组和各顺位是进一步观察；不能从多张表挑一个显著结果，声称所有人数或所有顺位都有相同效果。",
                  "- 区间跨零不证明没有差异或已经公平。判断“先手略占优”需要同时检查逆序组的绝对首末差和逆序相对正序的变化。",
                  "- 数据：`matches.csv` 保存每场指标；`hands.csv` 保存原下注顺序及每局净收益；`decisions.csv` 保存所有改航机会；`traces/` 包含两种费率各前三场逆序的完整 JSON 及可阅读 Markdown 示例。", "",
                  "```bash",
                  f"python3 -m simulation.reverse_order_experiment --games {c['games']} --samples {c['samples']} --seed {c['seed']} --workers {c['workers']} --aggression {c['aggression']} --output {c['output']}",
                  "```", ""])
    return "\n".join(lines)


def card_name(card: int) -> str:
    return f"{'红蓝绿'[card // 9]}{card % 9 + 1}"


def trace_markdown(trace: dict) -> str:
    lines = [f"# {LABELS[trace['variant']]}：第 {trace['game'] + 1} 场", "",
             f"seed={trace['seed']}；玩家编号从 0 开始；初始按钮={trace['initial_button']}；每人初始 100。", "",
             "## 逐局筹码与顺序", "", "| 局 | 底注 | 原下注顺序 | 实际改航顺序 | 开局筹码 | 结束筹码 | 改航动作 |", "| ---: | ---: | --- | --- | --- | --- | --- |"]
    for hand in trace["hands"]:
        swaps = [f"P{d['player']}付{d['fee']}换位置{d['slot'] + 1}" for d in hand["decisions"] if d["slot"] is not None]
        lines.append(f"| {hand['hand_index']} | {hand['ante']} | {hand['start_order']} | {hand['window_order']} | {hand['start_stacks']} | {hand['end_stacks']} | {'；'.join(swaps) or '无人改航'} |")
    lines.extend(["", f"最终筹码：{trace['final_stacks']}。", "", "## 详细行动", ""])
    for hand in trace["hands"]:
        private = "；".join(f"P{p}：{'、'.join(card_name(c) for c in cards)}" for p, cards in enumerate(hand["hands"]) if cards)
        lines.extend([f"### 第 {hand['hand_index']} 局", "", f"底牌：{private}。", ""])
        for event in hand["actions"]:
            kind = event["kind"]
            if kind == "ante":
                description = f"P{event['player']} 支付底注 {event['amount']}"
            elif kind == "reveal":
                description = f"揭第 {event['number']} 张公共牌：{card_name(event['card'])}"
            elif kind == "swap":
                description = f"P{event['player']} 支付 {event['fee']} 改航（实际顺位 {event['rank']}），位置 {event['slot'] + 1}：{card_name(event['removed'])} → {card_name(event['added'])}"
            elif kind == "action":
                description = f"第 {event['street'] + 1} 轮下注：P{event['player']} {event['action']}，新增投入 {event['amount']}，面对跟注额 {event['to_call']}"
            elif kind == "refund":
                description = f"退还 P{event['player']} 未被跟注筹码 {event['amount']}"
            else:
                description = json.dumps(event, ensure_ascii=False)
            lines.append(f"- {description}。")
        lines.extend(["", f"最终公共牌：{'、'.join(card_name(c) for c in hand['board'])}；派彩：{hand['awards']}；结束筹码：{hand['end_stacks']}。", ""])
    return "\n".join(lines)


def write_outputs(output: Path, results: list[dict], config: dict) -> dict:
    output.mkdir(parents=True, exist_ok=True)
    summary, matches, hands, decisions, traces = aggregate(results, config)
    (output / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (output / "report.md").write_text(report_markdown(summary), encoding="utf-8")
    write_csv(output / "matches.csv", matches)
    write_csv(output / "hands.csv", hands)
    write_csv(output / "decisions.csv", decisions)
    if traces:
        (output / "traces").mkdir(exist_ok=True)
    for trace in traces:
        stem = f"{trace['variant']}-{trace['game']}"
        (output / "traces" / f"{stem}.json").write_text(json.dumps(trace, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        (output / "traces" / f"{stem}.md").write_text(trace_markdown(trace), encoding="utf-8")
    return summary


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--games", type=int, default=1000)
    parser.add_argument("--samples", type=int, default=128)
    parser.add_argument("--seed", type=int, default=2026100803)
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--aggression", type=float, default=1.0)
    parser.add_argument("--output", type=Path, default=Path("reports/reverse-order"))
    args = parser.parse_args()
    if min(args.games, args.samples, args.workers) < 1:
        parser.error("games, samples, and workers must be positive")
    if not math.isfinite(args.aggression) or args.aggression <= 0:
        parser.error("aggression must be finite and positive")
    rng = random.Random(args.seed)
    seeds = [rng.getrandbits(63) for _ in range(args.games)]
    if len(seeds) != len(set(seeds)):
        raise AssertionError("duplicate match seed")
    jobs = [(game, seed, args.samples, args.aggression) for game, seed in enumerate(seeds)]
    results = []
    if args.workers == 1:
        for index, job in enumerate(jobs, 1):
            results.append(run_job(job))
            if index == 1 or index % max(1, args.games // 20) == 0 or index == args.games:
                print(f"Completed {index}/{args.games} independent seeds (four full matches each)", flush=True)
    else:
        with ProcessPoolExecutor(max_workers=args.workers, mp_context=get_context("spawn")) as pool:
            for index, result in enumerate(pool.map(run_job, jobs, chunksize=1), 1):
                results.append(result)
                if index == 1 or index % max(1, args.games // 20) == 0 or index == args.games:
                    print(f"Completed {index}/{args.games} independent seeds (four full matches each)", flush=True)
    config = {**vars(args), "output": str(args.output)}
    summary = write_outputs(args.output, results, config)
    for regime in ("baseline", "steep"):
        change = summary["reverse_minus_normal"][regime]["all"]
        print(f"{regime}, reverse-normal change in original betting first-last net: {formatted_interval(change['net_first_minus_last'])}")
    print(f"Saved full trajectory experiment to {args.output}")


if __name__ == "__main__":
    main()
