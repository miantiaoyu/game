"""Compare flat and increasing swap prices with paired window reversals."""

from __future__ import annotations

import argparse
from collections import defaultdict
from concurrent.futures import ProcessPoolExecutor
import csv
import json
from multiprocessing import get_context
from pathlib import Path
from statistics import mean
import sys

from .multi_swap import aggregate, nullable_interval, paired_order_comparison, run_game

VARIANTS = ("flat", "flat_reverse", "ladder", "ladder_reverse")
PRICES = {"flat": (2, 2, 2, 2, 2), "ladder": (2, 3, 4, 5, 6)}
LABELS = {"flat": "统一 2", "flat_reverse": "统一 2，逆序", "ladder": "顺位 2–6", "ladder_reverse": "顺位 2–6，逆序"}
INITIAL_CHIPS = 14


def run_pricing_game(job: tuple[int, int, int, str]) -> dict:
    seed, game, samples, variant = job
    regime = "ladder" if variant.startswith("ladder") else "flat"
    record = run_game(
        (seed, game, samples, "reversed" if variant.endswith("reverse") else "normal"),
        swap_costs=PRICES[regime], initial_chips=INITIAL_CHIPS,
    )
    record["variant"] = variant
    for key in ("voyages", "decisions", "players"):
        for row in record[key]:
            row["variant"] = variant
    return record


def cost_statistics(records: list[dict]) -> dict:
    rows = [row for record in records for row in record["decisions"]]
    strata = defaultdict(list)
    for row in rows:
        strata[row["participants"], row["rank"]].append(row)

    def stats(group: list[dict]) -> dict:
        paid = [row["cost"] for row in group if row["swap_index"] is not None]
        return {
            "opportunities": len(group), "swaps": len(paid),
            "total_paid": sum(paid), "mean_paid_per_opportunity": sum(paid) / len(group) if group else 0,
            "mean_paid_per_swap": mean(paid) if paid else None,
            "overwritten_cost": sum(row["cost"] for row in group if row["overwritten"]),
            "mean_pot_before": mean(row["pot_before"] for row in group) if group else None,
        }

    return {**stats(rows), "strata": [{"participants": k, "rank": r, **stats(group)} for (k, r), group in sorted(strata.items())]}


def order_game_effects(normal: list[dict], reverse: list[dict]) -> list[tuple[int, float]]:
    """Symmetric first/last contrast; keep original game IDs for clustering."""
    assert len(normal) == len(reverse)
    result = []
    for a, b in zip(normal, reverse):
        assert a["game"] == b["game"] and a["seating"] == b["seating"]
        av = {(row["voyage"], row["seat"]): row["net_points"] for row in a["players"]}
        bv = {(row["voyage"], row["seat"]): row["net_points"] for row in b["players"]}
        effects = []
        assert len(a["voyages"]) == len(b["voyages"])
        for va, vb in zip(a["voyages"], b["voyages"]):
            order = va["window_order"]
            assert order == vb["window_order"][::-1]
            if not order:
                continue
            first = va["voyage"], order[0]
            last = va["voyage"], order[-1]
            effects.append(((bv[first] - av[first]) - (bv[last] - av[last])) / 2)
        if effects:
            result.append((a["game"], mean(effects)))
    return result


def compare_pricing(records: dict[str, list[dict]]) -> dict:
    flat = order_game_effects(records["flat"], records["flat_reverse"])
    ladder = order_game_effects(records["ladder"], records["ladder_reverse"])
    assert [game for game, _ in flat] == [game for game, _ in ladder]
    positions = defaultdict(list)
    for a, b in zip(records["flat"], records["ladder"]):
        assert a["game"] == b["game"] and a["seating"] == b["seating"]
        aa = {(row["voyage"], row["seat"]): row for row in a["decisions"]}
        bb = {(row["voyage"], row["seat"]): row for row in b["decisions"]}
        assert aa.keys() == bb.keys()
        game_values = defaultdict(list)
        for key, row in aa.items():
            other = bb[key]
            assert (row["participants"], row["rank"]) == (other["participants"], other["rank"])
            game_values[row["participants"], row["rank"]].append(other["net_points"] - row["net_points"])
        for key, values in game_values.items():
            positions[key].append((a["game"], mean(values)))
    return {
        "order_balance_metric": "0.5 * [(reverse-normal for original first) - (reverse-normal for original last)]; average eligible voyages inside each game, then games",
        "flat_order_balance": nullable_interval(flat),
        "ladder_order_balance": nullable_interval(ladder),
        "change_in_order_balance": nullable_interval([(game, b-a) for (game, a), (_, b) in zip(flat, ladder)]),
        "ladder_minus_flat_by_original_window_position": [{"participants": k, "rank": r, **nullable_interval(values)} for (k, r), values in sorted(positions.items())],
    }


def format_interval(value: dict) -> str:
    if value["mean"] is None:
        return "无适用数据"
    if value["ci95"] is None:
        return f"{value['mean']:+.3f}（簇数不足）"
    return f"{value['mean']:+.3f}（{value['ci95'][0]:+.3f}，{value['ci95'][1]:+.3f}）"


def report_markdown(summary: dict) -> str:
    config, variants = summary["config"], summary["variants"]
    lines = [
        "# 顺位改航费用：统一 2 与递增 2–6", "",
        f"每组 {config['games']} 场、每场 10 趟，四组共 {config['games'] * 40} 趟。五名相同 navigator 策略，每次概率估计 {config['samples']} 样本，种子 {config['seed']}。牌力仍沿用 v0.1 同色求和，特殊牌型未加入本轮对战。", "",
        "## 规则与实验口径", "",
        "- 固定费用为 2、2、2、2、2；递增费用为 2、3、4、5、6。费用按实际到达窗口的存活玩家顺位确定，前人放弃不降价，三人窗口只收 2、3、4。逆序组按逆序后的新顺位收费。",
        "- 两组每趟均发 14 枚补给：底注 1、三轮下注最多 6、最高改航费 6，最多花 13，保证无需全下或边池。每趟净收益扣 14，整场基准为 140。增加初始补给本身不改变旧规则的下注能力。",
        "- 四组共用初始牌库、身份座位及决策随机流种子；仅改航计费和窗口顺序发生变化。策略估值同时使用真实费用：q_after × (pot + fee) − fee。",
        "- 主比较固定和递增两种费用；每种费用再做窗口逆序对照，下注顺序不变。逆序在递增组同时改变本人费用和其他人的收费环境，衡量整套顺位计费机制效果。",
        "- 95% 区间按五场座位平衡块聚类；窗口比较先按每场适用航行取均值，再对场次等权。缺失窗口保留原始簇编号。", "",
        "## 行为与费用", "",
        "| 组别 | 改航次数 | 改航率/机会 | 重复同位置/全部趟 | 改航被覆盖率 | 改航费总计 | 平均奖池 |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for key in VARIANTS:
        row = variants[key]
        lines.append(f"| {LABELS[key]} | {row['swaps']} | {row['swap_rate']:.2%} | {row['repeated_slot_rate_all_voyages']:.2%} | {row['overwritten_rate_per_swap']:.2%} | {summary['costs'][key]['total_paid']} | {row['mean_pot']:.2f} |")
    lines.extend(["", "## 首尾顺位差异的配对比较", "",
        "构造对称指标：½ ×〔原首位逆序后收益变化 − 原末位逆序后收益变化〕。正值表示末位在本实验的顺序变换中有收益优势，负值表示相反。它包含本人费用、其他玩家付费增加底池及后续响应，不能称为纯粹信息优势。", "",
        "| 指标 | 净收益差及 95% 区间 |", "| --- | --- |",
        f"| 统一 2 的首尾指标 | {format_interval(summary['pricing_comparison']['flat_order_balance'])} |",
        f"| 递增 2–6 的首尾指标 | {format_interval(summary['pricing_comparison']['ladder_order_balance'])} |",
        f"| 递增减统一：同场差中差 | {format_interval(summary['pricing_comparison']['change_in_order_balance'])} |", "",
        "## 正序窗口：人数与顺位", "",
        "每格覆盖该顺位所有机会；覆盖率另以实际改航次数为分母。窗口人数不同，牌况筛选也不同，以下描述性均值不能代替配对对照。", "",
        "| 人数 | 顺位 | 统一2改航率 | 递增改航率 | 递增费用 | 递增平均支付/机会 | 递增覆盖率 | 递增减统一的净收益（95% 区间） |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |",
    ])
    flat_strata = {(row['participants'], row['rank']): row for row in variants['flat']['window_strata']}
    paid_strata = {(row['participants'], row['rank']): row for row in summary['costs']['ladder']['strata']}
    differences = {(row['participants'], row['rank']): row for row in summary['pricing_comparison']['ladder_minus_flat_by_original_window_position']}
    for row in sorted(variants['ladder']['window_strata'], key=lambda row: (-row['participants'], row['rank'])):
        key = row['participants'], row['rank']
        overwrite = f"{row['overwritten_rate_per_swap']:.2%}" if row['swaps'] else "—"
        lines.append(f"| {key[0]} | {key[1]} | {flat_strata[key]['swap_rate']:.2%} | {row['swap_rate']:.2%} | {key[1]+1} | {paid_strata[key]['mean_paid_per_opportunity']:.3f} | {overwrite} | {format_interval(differences[key])} |")
    lines.extend(["", "## 局限与数据", "",
        "- 五人都是同一局部概率策略，没有根据顺位预判后续覆盖，也不从对手动作推断手牌。费用通过实际支出与局部期望值生效。",
        "- 高费用全部进入公共奖池，后来玩家可能不改航也受益。后手少改不等于后手总收益优势已消除。",
        "- 全桌总分近似守恒，不能用所有人平均分判断费用方案好坏；应同时看首尾配对指标、改航参与度和覆盖频率。",
        "- 本轮未更换牌型，避免费用与计分效果混合。牌型探针只反映组合分布，不代表对战胜率。",
        "- summary.json 含分层收益、费用、覆盖、两种费用各自的逆序首末配对；CSV 保存整场、航行、窗口决策及全部玩家逐趟记录。", "",
        "```bash",
        f"python3 -m simulation.cost_ladder --games {config['games']} --samples {config['samples']} --seed {config['seed']} --workers {config['workers']} --output {config['output']}",
        "```", "",
    ])
    return "\n".join(lines)


def write_csv(path: Path, rows: list[dict]) -> None:
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        for row in rows:
            writer.writerow({key: json.dumps(value, separators=(",", ":")) if isinstance(value, (list, dict)) else value for key, value in row.items()})


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--games", type=int, default=1000)
    parser.add_argument("--samples", type=int, default=256)
    parser.add_argument("--seed", type=int, default=20261008)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--output", type=Path, default=Path("reports/cost-ladder-1000"))
    args = parser.parse_args()
    if args.games < 10 or args.samples < 1 or args.workers < 1:
        parser.error("games >= 10; samples and workers must be positive")
    records = {key: [] for key in VARIANTS}
    jobs = [(args.seed, game, args.samples, variant) for variant in VARIANTS for game in range(args.games)]
    with ProcessPoolExecutor(max_workers=args.workers, mp_context=get_context("spawn")) as pool:
        for record in pool.map(run_pricing_game, jobs, chunksize=1):
            key = record["variant"]
            records[key].append(record)
            count = len(records[key])
            if count == 1 or count % 100 == 0:
                print(f"{key}: {count}/{args.games}", file=sys.stderr, flush=True)
    summary = {
        "config": {"games": args.games, "samples": args.samples, "seed": args.seed, "workers": args.workers, "output": str(args.output), "initial_chips": INITIAL_CHIPS, "prices": PRICES, "python_version": sys.version},
        "variants": {key: aggregate(records[key]) for key in VARIANTS},
        "costs": {key: cost_statistics(records[key]) for key in VARIANTS},
        "order_comparisons": {key: paired_order_comparison(records[key], records[key + '_reverse']) for key in PRICES},
        "pricing_comparison": compare_pricing(records),
    }
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    (args.output / 'report.md').write_text(report_markdown(summary), encoding='utf-8')
    all_records = [record for key in VARIANTS for record in records[key]]
    write_csv(args.output / 'games.csv', [{key: record[key] for key in ('variant', 'game', 'seating', 'scores', 'winners', 'win_share', 'burned', 'initial_chips')} for record in all_records])
    for filename, key in (('voyages.csv', 'voyages'), ('decisions.csv', 'decisions'), ('players.csv', 'players')):
        write_csv(args.output / filename, [row for record in all_records for row in record[key]])
    print(f"Saved {args.output / 'report.md'}")


if __name__ == '__main__':
    main()
