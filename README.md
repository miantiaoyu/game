# 暗潮航线

一个借鉴德州扑克隐藏信息、逐步揭牌和下注博弈的卡牌游戏。核心机制是支付筹码，替换所有人共享的公共牌。

**当前规则以 [《暗潮航线》标准牌局规则 v1.0](docs/standard-rules.md) 为准，确认于 2026-10-09。** 五人各 100 枚，最多 12 局，前六局底注 1／后六局底注 2，底池限注，反向改航，实际改航顺位费率 5%→10%；全下按主池、边池结算，局后筹码为零才淘汰。

**产品方向：独立 3D 客户端、多人在线、面向 Steam 发行，支持受控 Mod。** 当前聚焦牌局后端设计，推荐 C#／.NET 权威服务器与 PostgreSQL；Python 继续用于离线规则与概率研究。

**技术设计入口：[在线牌局后端设计 v0.2](docs/technical-design.md)。** 包含领域数据、规则状态机、Steam 身份、并发与故障接管、Mod 与防作弊、容量及发行前验证。目前 C# 已完成牌与牌库、洗牌抽牌、底牌视图、身份与座位绑定和有界房间操作队列；完整在线牌局仍处于设计阶段。[网页方向的 v0.1](docs/archive/technical-v0.1/technical-design.md)作为历史稿归档。

## C# 后端

需要 .NET 10 SDK。在项目根目录运行：

```bash
dotnet build server/DarkTide.slnx --configuration Release
dotnet test server/DarkTide.slnx --configuration Release
```

| 项目 | 职责 |
| --- | --- |
| `server/src/DarkTide.Rules` | 牌、洗牌、抽牌、单局、下注轮、奖池划分、筹码与整场；纯计算，不涉及网络与存储 |
| `server/src/DarkTide.Room` | 身份与座位绑定、房间阶段与版本、命令对象与去重、单房间有界操作队列 |
| `server/src/DarkTide.Persistence` | PostgreSQL 命令日志与结构迁移；设置 `ConnectionStrings__DarkTide` 启用 |
| `server/src/DarkTide.Server` | ASP.NET Core 宿主：配置、房间目录、健康检查、JSON 日志、OTLP 指标；`dotnet run --project server/src/DarkTide.Server` |
| `server/tests/` | 对应两个项目的 xUnit 测试 |

> 代码标识符使用英文，**注释保留中文**；外部边界（协议、数据库、配置键）一律英文。

已完成内容、设计约定和下一步见[开发进度](docs/开发进度.md)。

## 规则与研究文档

- [C# 开发进度](docs/开发进度.md)
- [架构总览：连接、会话、房间与牌局的数据流](docs/technical/架构总览-连接房间与牌局.md)
- [请求防重与流量控制：重复点击、网络积压和恶意请求](docs/technical/请求防重与流量控制.md)
- [标准规则与桌边速查](docs/standard-rules.md)
- [历史三花色提案：两底三公、九组选最强与牌型概率](docs/three-suit-rules.md)
- [历史持续筹码与费用草案](docs/persistent-chips-draft.md)
- [12 局动态费用：四档筛选](reports/persistent-screen/report.md)
- [12 局动态费用：独立种子验证](reports/persistent-validation/report.md)
- [本轮结论、报价取整问题与三场对局](reports/persistent-validation/findings.md)
- [反向改航的规则与实验口径](docs/reverse-order-trial.md)
- [反向改航：四组完整 12 局对照](reports/reverse-order/report.md)
- [反向改航结论：盈利频率与净筹码的区别](reports/reverse-order/findings.md)
- [27 张牌的精确概率报告](reports/three-suit-exact/report.md)
- [历史初始概念 v0.1](docs/initial-concept.md)
- [1000 场主实验及配对对照报告](reports/1000-games/report.md)
- [完整统计与参数](reports/1000-games/summary.json)
- [逐场原始记录](reports/1000-games/games.csv)
- [第二轮：五人都会改航的模拟报告](reports/multi-swap-1000/report.md)
- [第二轮完整统计](reports/multi-swap-1000/summary.json)
- [第三轮：顺位费用 2–6 的对照报告](reports/cost-ladder-1000/report.md)
- [特殊牌型候选规则](docs/hand-types-proposal.md)
- [牌型频率探针](reports/hand-types/report.md)

当前版本使用三种花色、数字 1—9、两张底牌和三张公共牌，最终三张至少含一张底牌。独立持续筹码引擎已接入这些牌型，以及 12 局、前六局底注 1／后六局底注 2、底池限注、主边池和动态改航费；旧版脚本仍保留用于复现历史结果。

```bash
python3 -m simulation.three_suit_analysis --output reports/three-suit-exact
```

## 当前 12 局版本

每人起始 100 枚，筹码跨局保留，不补充；结算后筹码为零者出局。下注采用底池限注，改航费进入独立附加池，不购买边池资格。所有可操作玩家按下注的反向顺序改航，同一公共位置允许再次替换；实际顺位费率为 5%→10%，报价在窗口开始时冻结。

为复现历史对照，引擎的 `Config()` 仍默认正向改航。运行标准规则须显式使用 `Config(reverse_swaps=True, low_bps=500, high_bps=1000)`；反向实验中的 `baseline_reverse` 对应这套标准。单场示例：

```python
from simulation.persistent_game import run_match
from simulation.persistent_model import Config
from simulation.persistent_policy import EquityPolicy

rules = Config(reverse_swaps=True, low_bps=500, high_bps=1000)
result = run_match(20261009, EquityPolicy(samples=128), rules, trace=True)
print(result["final_stacks"])
```

以下命令用于复现定稿前的费率和顺序对照，会同时运行不同候选规则：

```bash
python3 -m simulation.persistent_experiment --games 200 --samples 96 --seed 2026100801 --workers 6 --mode both --profiles flat,baseline,steep,very_steep --output reports/persistent-screen
python3 -m simulation.persistent_experiment --games 1000 --samples 128 --seed 2026100802 --workers 6 --mode both --profiles baseline,steep --output reports/persistent-validation
```

`flat/baseline/steep/very_steep` 的首末费率分别为 5%–5%、5%–10%、3%–15%、2%–20%，中间位置线性插值；费用乘以本人可争夺的普通池，向上取整，最低为本局底注。支持 `no_swap` 对照与 `--aggression` 策略敏感性参数。

完整轨迹衡量淘汰、筹码分布与改航参与度；同一基准局面的改航正逆序对照衡量先后手净收益差。区间按独立整场计算，零改航窗口也计入。机器人使用公开信息的局部概率估值，不预测后续覆盖、未来下注或对手动作透露的手牌范围；结果不等于最优策略下的数学平衡证明。

每份报告附有逐场筹码、全部窗口机会及配对结果 CSV，以及三场基准对局的完整 JSON 过程。

反向改航实验保持三轮下注按原顺序，将每局改航名单整体反转。两套已有费率各比较正序、逆序，费用仍跟随实际改航顺位。首末收益按本局开局时的下注位置计算，包含后来弃牌或全下者。

```bash
python3 -m simulation.reverse_order_experiment --games 1000 --samples 128 --seed 2026100803 --workers 6 --output reports/reverse-order
```

## 历史版本模拟

使用 Python 3.10 或以上版本，仅依赖标准库。在项目根目录运行：

```bash
python3 -m simulation.run --games 1000 --samples 256 --seed 20261008 --workers 4 --output reports/1000-games
```

默认比较五种策略：跟注到底、谨慎保守、概率决策、主动诈唬、概率改航。每场五名玩家各代表一种策略，共十趟航行。主实验为 1000 场完整游戏，即 10000 趟；另跑 1000 场关闭改航的配对对照。

`--no-ablation` 可关闭额外对照；`--games` 至少为 10，建议为 5 的倍数以完整平衡座位。`--samples` 控制每次决策的概率估计精度。相同 Python 版本、种子、场数与抽样次数可复现结果；工作进程数不影响结果。输出文件会覆盖指定目录内同名报告。

程序会生成 Markdown 报告、JSON 汇总和 CSV 逐场记录。整场及每趟胜率均对并列胜利按人数分摊。置信区间按每五场共享相对座次的平衡块聚类，不把一场内的多趟航行视为独立整场样本。

## 第二轮：多人改航

```bash
python3 -m simulation.multi_swap --games 1000 --samples 256 --seed 20261008 --workers 4 --output reports/multi-swap-1000
```

五人均使用相同的概率策略，可撤退、加码、改航。保留每人一次机会、每次费用 2、同一公共位置允许重复替换的规则。主实验跑 1000 场，再分别运行三个配对对照，每组各 1000 场：

- 仅反转改航窗口顺序，下注顺序不变，用于观察整个改航顺序反转的影响。
- 全员禁止改航，用于比较奖池、摊牌率与得分波动。
- 只禁止固定身份 P1 改航，其余四人仍可改航，用于估计四名对手均会改航时 P1 开放改航选项的效果。

各组共用相同的初始牌库、座位和决策随机流种子。报告区分窗口实际人数与行动顺位，统计同一位置反复改航、已换入的牌被覆盖，以及覆盖后的条件收益。输出除报告和 JSON 外，还包括整场、航行与窗口决策三个层次的 CSV 数据。

这轮机器人会针对当前最新牌面决策，但不会预判后面玩家再次覆盖的风险。覆盖后的条件收益不等于覆盖造成的损失；顺序反转对照也会同时改变其他玩家的顺序。五人能力相同，不能把身份之间的胜率排名理解为策略强弱。

## 验证规则

```bash
python3 -m unittest discover -s tests -v
```

测试覆盖下注重新响应、平局分池、提前结束、最大支付、筹码守恒、公开与隐藏信息边界，以及配对实验的随机流隔离。

## 第三轮：费用与牌型分别研究

```bash
python3 -m simulation.cost_ladder --games 1000 --samples 256 --seed 20261008 --workers 4 --output reports/cost-ladder-1000
python3 -m simulation.hand_type_probe --samples 100000 --seed 20261008 --output reports/hand-types
```

费用实验包含统一收 2、按实际改航窗口顺位收 2/3/4/5/6，以及各自的窗口逆序对照，每组 1000 场。前人放弃不改变后人的费用；只有三人到达窗口时收 2/3/4。所有组每趟统一给 14 枚补给，以覆盖最多 13 枚支出；旧默认规则仍使用 10 枚。此实验继续使用原来的同色求和计分。

牌型探针单独记录候选方案：恰好选一张私牌、两张公牌，六组选最强，按同花顺、三条、同花、顺子、对子、散牌比较。它包含基础组合精确枚举及十万次无操作发牌抽样，不包含下注或改航，不是实战胜率。候选牌型尚未接入对战引擎。

## 代码位置

- `simulation/model.py`：牌、航线强度与策略可见的信息。
- `simulation/game.py`：发牌、下注、改航、结算与随机流。
- `simulation/strategies.py`：五种策略及蒙特卡洛概率估计。
- `simulation/run.py`：完整比赛、座位平衡、配对实验与报告生成。
- `simulation/multi_swap.py`：多人改航、覆盖追踪、行动顺序与个人禁改的配对实验。
- `simulation/cost_ladder.py`：递增改航费用及首尾收益差异的配对实验。
- `simulation/hand_type_probe.py`：三张和五张牌型候选的枚举与随机发牌探针。
- `simulation/three_suit_analysis.py`：三花色、至少一张底牌、九组选最强的精确枚举。
- `simulation/persistent_model.py`：当前规则参数、公开观察与动作接口。
- `simulation/persistent_game.py`：持续筹码、全下、主边池、动态改航和可复用的窗口快照。
- `simulation/persistent_policy.py`：当前牌型的概率下注与改航估值。
- `simulation/persistent_experiment.py`：完整赛制、同状态费用与顺序对照、按整场统计及对局记录。
- `simulation/persistent_diagnostics.py`：从原始 CSV 重算费用差价消失比例及单局盈利概率指标。
- `simulation/reverse_order_experiment.py`：整场反向改航、原下注位置收益、同场配对区间及可阅读对局过程。

这些策略尚未训练或寻优，也不会通过对手行动推断其手牌。结果用于探索当前数值与策略差异；初始概念保持独立记录，真人心理博弈和趣味性仍需试玩验证。
