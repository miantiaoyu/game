# Steam 多人牌局：协议、并发与恢复设计

版本：技术设计 v0.2 · 2026-10-09。状态：待实现、待压测。本轮只定义设计，不创建服务、协议生成代码、数据库迁移或运行环境。网页方案已归档，本文以独立 3D 客户端和 Steam 多人房间为目标。

规则以 [标准牌局 v1.0](../standard-rules.md) 为准。可靠传输、行动期限、故障补时和资源限额是服务运行设计，不能改变短额全下、加价权、冻结改航报价或各池资格。

## 1. 服务端权威与进程边界

建议采用 C#、.NET 10 LTS、ASP.NET Core/Kestrel、Npgsql 和 PostgreSQL 17。[微软支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)列明 .NET 10 LTS 支持至 2028-11-14。正式实现固定补丁版本与依赖锁定；这里没有宣称已完成移植或部署。牌局服务为无图形、无 GPU、无 3D 引擎场景循环的 headless 服务。

独立客户端通过引擎适配层接收服务器视图，把合法输入翻译为协议命令。发牌、牌型、下注、改航、随机牌库、主池/边池、退款与结算全部由服务器决定；3D 卡牌落点、抓牌动画、粒子与摄像机仅作表现，不构成牌局事实。动画慢、掉帧或跳过动画不影响其他人的 turn。

每个房间由一个 actor 顺序处理，跨节点也只能有一个合法 owner。生产设计从本版起包含至少一个可接管的备用服务节点、持久租约和写入 fencing；单机开发可以只有一个节点，但不据此宣称具有高可用。Steam Lobby 只用于邀请、发现和房间引用，不是座位、筹码、牌库或房间归属的权威数据库。

机器人在受限计算工作进程中估值，只返回建议。它不能写数据库、推进 turn 或持有完整真实牌库。服务器端规则扩展只能是服务方审核并部署的代码/数据包，不接收客户端上传后立即执行的 Mod DLL。官方标准房固定 `ruleset_id=dark-tide-standard-1.0`、`ruleset_version=1.0`；扩展房必须明确显示并固定独立规则版本与服务端内容摘要，不能在进行中的标准房替换。

## 2. Steam 身份、连接与房间权限

| 步骤 | 信任边界 | 服务端要求 |
| --- | --- | --- |
| Steam 登录换会话 | 客户端提供目标后端使用的 Steam 身份票据 | 本方案选 `GetAuthTicketForWebApi` → 后端 `AuthenticateUserTicket` 流程，检查 AppID、目标 identity 与验证结果；不与 GameServer `BeginAuthSession` 回调流程混用 |
| 绑定身份 | 使用 Steam 验证响应中的 SteamID | 不相信客户端自报 SteamID、昵称、房主或 Lobby 成员列表；必要的应用资格限制也由后端核验 |
| 创建服务会话 | 票据验证成功后签发服务令牌 | 返回短期访问令牌与可轮换重连凭据；票据、令牌与密钥不进入日志、回放或公开错误消息 |
| 建房/加入 | 已认证 principal 与房间策略 | 服务端分配/确认席位和角色；Steam 邀请不允许绕过房间满员、规则版本或封禁限制 |
| 控制权认领 | 同一 Steam 身份可有旧进程或重连连接 | 每个座位只允许一个控制连接；新的 epoch 原子取代旧连接 |

认证流程依据 [Steam 官方身份文档](https://partner.steamgames.com/doc/features/auth)。Steam publisher key 或其他服务端验证密钥只放在后端密钥管理中，不能打进客户端、Mod、Steam Lobby 元数据或 Protobuf 消息。启用票据有效期、目标用途、会话数量、重复交换与撤销检查；不把拿到一个字符串就当作验证通过。Steam 认证暂时不可用时，新认证明确失败或稍后重试；已建立服务会话按其有效期与撤销策略处理，不降级成“相信自报 SteamID”。

建议访问令牌有效期 10 分钟，重连凭据最长 24 小时并逐次轮换，数据库只存其摘要、principal_id、用途、到期与撤销状态。丢失新重连响应可以通过重新取得 Steam 票据恢复同一 SteamID，不能因为响应丢失创建另一个玩家。应用重启可恢复身份，但不能复活已失效的控制权和旧动作期限。

HTTPS 控制接口负责 Steam 票据交换、令牌轮换、建房、加入和历史分页；WSS 负责订阅、房间命令与状态。使用 TLS、证书验证、最小日志和令牌撤销；认证头或受限首条认证消息承载令牌，不把长期凭据放在 URL。加入和建房使用命令 ID 去重；建房前的回执作用域为 principal 级，房间存在后的回执作用域见第 5 节。

## 3. 可靠有序传输与协议版本

牌局频率低、每次决策必须可靠、有序。首版选择 **WSS 二进制消息＋版本化 Protobuf**；WebSocket 可以由 Unity、Unreal 或其他原生引擎使用，不意味着改做网页。HTTP/Steam 认证 SDK 与 WebSocket 收发由客户端适配层封装，不侵入牌型与 3D 表现代码。

没有必要为下注加入 UDP、帧同步、回滚网络或“客户端预测筹码”。SteamNetworkingSockets 可作为未来传输适配器；WSS 不会自动经过 [Steam Datagram Relay](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay)，使用 SDR 需要另行集成和验证；如果以后加入与牌局无关的高频角色动作，可以另用不可靠动作流。任何不可靠表现消息都不能代替可靠牌局命令或账本提交。

一个 WebSocket 二进制消息承载一个协议 Envelope；分片在传输适配层重组，按**整个消息**限制大小，不能每个 fragment 各获一份额度。协议按 major/minor 协商；不兼容 major 拒绝入房，可选 minor 能力显式协商。Protobuf 字段编号永久稳定，删除后保留编号，未知枚举或未知动作不能落入默认 check/call 分支。

| 字段 | 类型与语义 |
| --- | --- |
| protocol_version | 明确 major/minor；消息格式版本 |
| room_id / match_id / command_id / turn_id | UUID 的规定字节顺序，线上统一 16 字节，调试才格式化为文本 |
| state_revision / event_seq / owner_epoch / controller_epoch | 非负 `long`，数据库 BIGINT、Protobuf int64；值域 0…2^63−1，拒绝负值与溢出，不再沿用网页 JSON 字符串表示 |
| ruleset_id / ruleset_version | 固定的规则身份与版本；官方标准值见第 1 节 |
| rule_binding | 引用领域规格 §2.3 的完整冻结身份：rule_capability_version、rule_package_hash、rule_profile_hash、rule_lock_hash、engine_build、state_schema_version、event_schema_version、rng_version；整场、机器人任务与恢复核对同一绑定 |
| state_schema_version | 持久快照格式版本，不等于规则或网络版本 |
| expected_revision / applied_revision | 均引用 RoomSnapshot 唯一的 state_revision |
| SteamID | Steam 验证过的 UInt64；Protobuf fixed64，PostgreSQL numeric(20,0) 并限制 0…2^64−1，或验证后的十进制文本；不能强塞进有符号 BIGINT |
| server_time / deadline_at | UTC 时间戳；客户端只画倒计时，不能作为最终裁决时钟 |
| stream_id | 每次订阅流的 UUID，拒绝旧连接的迟到状态 |

state_revision 在房间生命周期内递增，跨 match 不重置；准备、开局、动作、控制权交接、持久暂停/恢复都会增加。presence 和心跳通知不增加游戏版本。turn_id 在建立一次行动机会时生成并持久化；同一机会接管控制权或故障恢复不换 turn_id，不从 revision 计算它。

### 命令与结果的类型约束

| 命令 | 有效载荷 | 权威校验 |
| --- | --- | --- |
| BetCheck / BetCall / BetFold | 无金额 | 本人当前机会、待跟注额和标准规则；不能让无跟注额主动弃牌 |
| BetRaise | add_chips：整数 | **本次新增筹码，包含跟注额**；加价权、最低完整加价、短额全下、底池上限和余额 |
| SwapReplace | slot：0 或 1 | 最新公共牌位置、当前改航机会、窗口冻结报价、足额付款 |
| SwapPass | 无 | 当前机会，推进后不能补做 |
| ClaimController | expected_controller_epoch | 当前身份、成员和 epoch；成功更换控制连接，不重置 turn/deadline |
| Ready / StartMatch 等大厅命令 | 各自明确类型 | 房主/成员权限、房间阶段与完整五席条件 |
| Resync / QueryCommand | 版本或原 command_id | 只读；回执只允许本人查询，不能代其他 viewer 看底牌 |

网络 Envelope 不允许客户端任意指定 actor_seat；席位由验证后的 principal 与成员绑定得出。Protobuf `oneof` 表达动作联合，解析成功仍须检查字段范围、重复字段策略、未知字段和整个消息的资源限额。协商版本内的游戏命令拒绝未知字段和重复 singular/oneof 成员，避免 hash 或执行出现两种解释；下发快照只允许忽略已约定可扩展的非关键字段。金额拒绝负数、超额与非法枚举；Protobuf 是编码格式，不是反作弊规则。

成功回执仅含原 command_id、Applied、applied_revision 和必要动作摘要；业务拒绝使用稳定类型码，如 RevisionMismatch、TurnMismatch、NotController、IllegalAction、TurnExpired、IdempotencyConflict。Busy/Recovering 是可重试的受理前状态，不把原 command_id 记为最终业务拒绝；认证失败/格式错误也不声称已经接受动作。客户端收到 ACK 不自行结算，最终画面由权威快照驱动。

## 4. 按接收者投影与 Mod 边界

服务器从白名单构造 ViewerSnapshot；禁止通用序列化 RoomSnapshot、领域事件或模拟 trace。真人、旁观者和机器人使用不同受限视图。

| 信息 | 接收规则 |
| --- | --- |
| 自己的两张底牌 | 仅本人连接和该座位机器人可见 |
| 其他人的底牌 | 按标准规则正式摊牌才公开仍未弃牌者；弃牌者的底牌不会在整场结束时自动公开 |
| 公共牌、公开弃牌、下注、改航或放弃、冻结价格、退款、奖池资格 | 作为公开事实可见 |
| 完整牌库、未发牌、随机源、内部账户令牌 | 永不发给客户端、观众或机器人 |
| 合法输入与金额范围 | 当前有权控制该座位者；观众不能通过视图获得执行权限 |
| 结算摘要 | 只含按规则可公开的牌和分配；无须摊牌时不为动画额外翻底牌 |

客户端 Mod 可以改变允许的外观、声音和表现，但它看到的网络数据仍只是一份合法 viewer 视图。把未发牌先下发再“靠 UI 隐藏”不可接受；客户端完整性校验也不能补救此类泄露。服务端每条命令独立检查规则，因此修改客户端按钮、内存或本地脚本不能改变金额和发牌。

这套设计能保护服务器秘密与账本，不能声称阻止玩家共享自己合法看到的底牌、场外串通或录屏。相关检测与社区治理另设策略。官方规则房不接受客户端 Mod 声明的规则、赔率、随机种子或脚本执行结果；扩展房的规则包、版本和摘要在开局时绑定，恢复时必须加载同一受审核服务端实现。

首版每次提交发送完整小快照，包含状态版本、订阅流、房间/服务状态、公开桌面、自身私牌、turn/deadline 和合法动作。新完整快照可以跨 revision 直接替换；只在未来增量消息缺失时要求补发。3D 动画消费有界事件摘要，可跳到最新状态，不要求追播所有视觉事件才允许操作。

## 5. 命令幂等与多连接控制

房间命令回执唯一键为 **(room_id, principal_id, command_id)**。语义 hash 覆盖协议语义版本、match_id、命令类型及 payload、expected_revision、turn_id、controller_epoch；不覆盖连接 ID、网络重试次数或到达时刻。Protobuf 原始字节可能有等价编码，不能直接用原始帧 hash 代替语义规范化；未知字段处理和 hash 版本须固定。

处理顺序为：认证与房间访问 → 查询原回执 → 同 ID 同语义返回原结果/不同语义拒绝 → 当前控制权、版本、turn、期限和合法动作检查 → 候选状态 → 原子提交 → ACK/广播。**查重复先于 stale 和当前 controller 检查**：已成功加注后 ACK 丢失，重连或控制权更新不能把成功重试误报成失败。

同一 Steam 身份默认最多两个连接，只有一个 controller。ClaimController 在 actor 内递增 controller_epoch、绑定新连接并提高 state_revision；旧连接变为只读。旧连接可查询自己的历史回执，不能发新的动作。两个同时基于同一 epoch 的接管最多一个成功。

客户端一次用户意图重试保持同一 command_id；收到确定业务拒绝后，基于新快照发起新意图才换 ID。回执缓存可以淘汰，数据库去重记录不可在活跃房间提前删除。关闭房间和 match ID 不复用，历史清理后也不重新执行旧命令。

## 6. 房间 actor 与有界等待

每个 owner 节点有房间注册表，同一房间并发加载共享一个加载任务。房间拥有一个有界 `Channel<RoomMessage>` 和一个读取循环；网络、定时器、机器人结果只入队，不直接修改状态。一个 actor 内逐条验证、推导、提交，不持有多人共享的可写领域对象。

客户端命令采用非阻塞 TryWrite，成功时记录节点的单调时钟 admission 时间与房间入队序号；满则立即 Busy，没有“已接受”。房间外部命令最多占 56 项，64 项总容量保留 8 项给超时、恢复等内部事件。保留额度不改变单一 FIFO 顺序，不让系统 timer 插队越过已入队动作。

当前 turn 最多一个计时任务、一个有效机器人任务。内部任务在保留空间也满时异步等待，不忙轮询、不丢关键 timeout；外部动作即使先被消费仍独立检查期限，不能钻 timer 调度延迟空子。整个消息大小、队列数、数据库等待、计算任务都有上限。

在健康服务期间，动作以成功入队时间是否早于当前 deadline 判定；刚好等于视为过期。已按时入队的动作可能因数据库排队在期限之后提交，但仍可有效。只在同一健康 owner 内使用这项规则，单调时间不跨节点比较。内存入队不是持久成功，owner 崩溃后尚未提交动作由原 ID 重试，不能声称已实现持久 inbox。

actor 空闲时等待 Channel 和具体定时任务，不做每房每秒扫描，不运行 60 FPS 服务循环。一次纯规则推进在独立候选状态上前进到下一个外部输入或展示边界；下注重新响应、退款、形成改航窗口等不是彼此竞争的协程。

## 7. 数据库租约、fencing 与备用接管

租约使用 **PostgreSQL 当前主库的时钟**，不依据应用节点墙钟。建议初始 lease 为 15 秒、每 5 秒续租，均为未压测提案。节点同时维护保守的本地单调失效时间；无法确认续租或数据库写入时停止接收房间动作并进入本地冻结，不能“先继续玩，数据库恢复后再补”。

每次状态事务都锁定或原子比较该房间归属行，验证 owner_id、owner_epoch、租约有效期与 expected state_revision。取得新租约必须在同一原子操作中提高 owner_epoch；旧 owner 不能沿用旧 epoch 重获写权限。进程恢复也必须重新确认租约，不能仅凭内存缓存声明拥有房间。

接管与状态提交在同一数据库序列化边界上竞争：新 epoch 提交之后，任何旧 epoch 的余额、事件、回执或 deadline 写入都必须失败。即使旧节点由于长 GC、网络隔离或暂停后继续运行，也不能晚写。事务短而有截止时间；不能持有归属行锁等待机器人、Socket 或玩家。若旧事务持锁未结束，新 owner 等待数据库解决，而不是绕开锁重建同房间。

路由只用于把连接送到可能的 owner，不能替代数据库 fence。网关转发的命令仍校验身份与当前 owner；过期路由返回重连/重路由。owner 变化时流与连接重建，客户端以 stream_id 和 state_revision 丢弃旧广播。已提交旧版本在新 owner 接管后迟到的广播不能回滚画面。

生产计划至少部署两个房间服务节点，备用节点留有接管容量；自动探活、流量切换、数据库故障转移和恢复演练仍需实现及验证。这不是已经达到自动 HA 的声明。Redis 不是正确性的前提，后续可作发现/缓存；它丢失不能允许两节点同时写房间。

## 8. 单次提交与未知 commit

处理动作时复制小领域状态生成 candidate；已提交的筹码、牌库游标、阶段和 deadline 不原地改动。一个事务验证 owner fence 与 revision，更新快照、追加领域事件、保存命令回执和必要成员变化；提交成功才替换 actor 内存状态、ACK 和排入广播。

数据库明确回滚：丢弃 candidate，保持原状态。数据库连接在提交时中断：结果为**未知**，不能返回确定失败、重新洗牌或再次扣款。房间停止后续写入，用新连接查询原回执与权威 revision：存在则恢复原成功结果；暂时不存在也不等于原事务已经失败，须确认旧事务结束，或通过相同幂等键、fence 和 revision 让数据库裁决重试。

恢复/接管也先解决未知事务，再建立 RECOVERING 记录。若旧事务最终提交，其新状态与回执进入恢复基线；若未提交，原命令可在恢复后重试。成功 ACK 永不早于 commit；commit 后 ACK 丢失只是客户端未知，不是数据库失败。

读取、事务与连接池等待均有上限；不在事务内等待 WebSocket、Steam API、机器人、其他房间 actor 或用户输入。访问锁次序统一为 ownership/room → members → events/receipts，避免两个路径倒序锁行。发送队列由独立 sender 消费，不让一个慢连接阻塞本房间提交。

## 9. 服务故障暂停与恢复计时

个人断线和服务故障必须分开：**个人断线不暂停全桌**，当前期限继续，到期从标准合法动作选择免费过牌/面对下注时弃牌/放弃改航；全下玩家本就没有普通输入 turn。玩家刷新、Steam 会话重连或控制权认领不能延长期限。

服务器或数据库不可用时，房间不能继续执行这种普通个人超时。设置独立的持久 `ServiceState=ACTIVE/RECOVERING`，不混入游戏 `RoomStatus=WAITING/STARTING/PLAYING/RESULTS/CLOSED`。本地一发现无法可靠服务就冻结输入与 timer；数据库不可达时尚不能声称 RECOVERING 已持久化，数据库恢复后先落恢复记录，再允许游戏推进。

### 持久计时字段与步骤

| 记录 | 含义 |
| --- | --- |
| last_service_checkpoint_at | 当前 owner 最近一次确认能服务该房间的数据库时间，由短续租/状态事务写入；不能只用“进程还活着”续租 |
| deadline_at / turn_id | ACTIVE 期间的输入截止时间与同一行动机会 |
| recovery_id / recovery_cause | 一次故障恢复的稳定标识及原因，接管后持续沿用，不按每次重启新建 |
| frozen_remaining_ms / frozen_turn_id | 从最后可确认服务点计算并冻结的剩余行动预算，RECOVERING 中不消耗 |
| resumed_at / resume_revision | 标识该 recovery_id 已成功恢复，防止重复补时 |
| compensation_used_ms / recovery_count | 当前 turn 的已累计计时返还与自动恢复次数，跨节点持久化 |

1. 健康运行时，以数据库 checkpoint 和本地单调时钟共同维护期限；每次成功转移和定期续租更新可确认服务点。
2. 接管先用数据库 fence 解决旧写者，加载最后提交状态并解析未知 commit，然后原子进入 RECOVERING。当前 turn 的剩余预算取 `max(0, deadline_at − last_service_checkpoint_at)`，仅第一次进入该 recovery_id 时冻结；如果已经 RECOVERING，直接沿用已冻结预算。
3. 剩余时间的起点不是“新进程启动”，不重新给完整 30/20 秒。实际崩溃时间不可完全观测；健康 checkpoint 确实按约定间隔工作时，此方法约返还最多一个 checkpoint 间隔，建议 5 秒。调度长暂停、时钟异常或续租链不完整时，不能把 5 秒当作已证明的硬界限；这项不确定性和补偿必须记录并可审计。
4. 恢复 owner 加载原牌库、规则包、回执和成员控制代数，确认新路由可用后，单事务执行 RESUME：同一 turn_id 的新 deadline 为数据库当前时间加 frozen_remaining_ms，写 resumed_at、revision 和事件，然后广播完整快照。重复 RESUME 或再次启动不能重复添加时间。
5. 恢复之后继续消耗同一剩余预算。建议同 turn 最多自动恢复两次、累计额外返还不超过 10 秒；正常 checkpoint 链下，每次按一个 5 秒间隔保守记入补偿预算。若故障前健康 checkpoint 链已有异常空档、无法用该预算覆盖不确定性，则不自动恢复；停机本身持续很久不等于额外赠送思考时间。需要更多补偿时继续冻结等待运维裁决，不偷偷刷新全额时长，也不自动把故障变成弃牌。具体阈值是服务运行提案，需在原型故障演练中确认。

RECOVERING 期间不扣筹码、不发牌、不执行普通 timeout、不派发新的有效机器人动作。若 frozen_remaining_ms 为零，区分“健康期间已经到期且有可靠证据”和“服务覆盖存在不确定性”：前者恢复后可以执行原合法 timeout；后者继续冻结或人工处理，不能凭重启时墙钟很晚就判玩家超时。已在故障前**提交**的动作和 timeout 不回滚。

这一方案保证服务故障不会**仅因为停机期间时间经过**而让玩家自动弃牌；它不承诺精准还原不可观测的每一毫秒，也不允许个人网络断线触发服务级补时。补偿账本、recovery_id 和恢复次数不因重新连接、更换 owner 或新进程而清零。若无法可靠恢复牌局，应明确暂停并处理受影响整场，不凭客户端提供的牌、金额或记忆重建。

## 10. 逻辑存储字段与唯一来源

只定义字段，不生成 DDL。所有持久化布局由同一个序列化/仓储边界负责，以下派生列与 JSON/Protobuf 载荷不能各自成为可写账本。

| 逻辑记录 | 关键字段与约束 |
| --- | --- |
| RoomSnapshot / rooms | room_id；RoomStatus；state_revision；state_schema_version；current_match_id；ruleset_id/version；服务端规则内容摘要；当前 MatchState；last_event_seq |
| room_ownership | room_id 唯一；owner_id；owner_epoch；lease_until；last_service_checkpoint_at；每次游戏写入都必须验证本记录 |
| room_recovery | room_id；ServiceState；recovery_id；cause；frozen_turn_id；frozen_remaining_ms；resumed_at；compensation_used_ms；recovery_count；当前恢复结果与快照同事务更新 |
| 当前输入机会 | turn_id；acting_seat；deadline_at；阶段类型；属于 RoomSnapshot 的唯一运行元数据，不在 MatchState 里另存一份可改期限 |
| room_members | room_id+principal_id 唯一；SteamID/principal 映射；固定 seat_id；role；ready；controller_epoch；当前控制连接；撤销状态 |
| room_events | room_id+event_seq 唯一；state_revision；owner_epoch；match_id；event_type；command_id；public/private payload；数据库时间 |
| command_receipts | room_id+principal_id+command_id 唯一；semantic_hash 及版本；最终结果；applied_revision；owner_epoch；原可返回结果；保留期 |
| sessions | principal_id；验证过的 SteamID；令牌/重连凭据摘要；用途；到期；撤销；轮换代数；不保存 publisher key 或明文凭据 |
| matches | match_id；room_id；不可变规则包身份/摘要；事件范围；开始/结束；最终公开结果索引 |

RoomSnapshot 外层负责房间版本、状态、服务恢复、成员引用和输入机会，嵌入的 MatchState 负责牌局事实。类型列是索引/CAS 所需的确定映射；领域载荷里的冗余引用只由同一序列化器生成，加载不一致立即停止恢复，不任选一份覆盖另一份。presence、Socket、Channel、计时器和机器人任务句柄不持久化。

state_public/private 物理分离或一份带字段级加密的载荷均可，但未公开底牌、完整牌库和未来顺序只能在服务端受限存储。密钥通过服务端密钥管理提供；备份也遵守权限与加密策略。数据库事件不是客户端广播 DTO。

每局随机洗牌使用服务器安全随机源，**完整排列、发牌事实与游标在对任何人展示之前持久化**。恢复使用原排列，不能重新随机；仅保存依赖运行库实现的 seed 不足以保证跨版本还原。规则包/schema 不支持时继续 RECOVERING，不猜测兼容。

## 11. PostgreSQL 主库故障与数据承诺

应用备用节点不能替代数据库的高可用。生产部署需明确主库、备用库、复制模式、提升条件和旧主库隔离，防止两个数据库主库各自接受一套有效 owner_epoch。应用 fence 只在单一权威数据库历史下成立，不能修复数据库 split-brain。

若数据库使用异步复制，已向玩家 ACK 的 commit 仍可能在主库永久丢失且备用未追上时丢失，因此不能声称 RPO=0。若业务要求在限定单故障模型下不丢已确认动作，必须配置相应同步提交/同步备用，并只提升包含全部已确认提交的节点；这会牺牲部分可用性与延迟，仍受备份、双节点同时损坏和运维错误等边界限制。

复制/故障转移模式、恢复点目标与演练结果未确定前，产品只承诺“出现无法确认的状态时冻结并核查”，不能承诺已实现无损自动切换。通过受信的服务端审计或提交记录确认数据库回退、已确认回执缺失等异常时，不重复执行旧动作拼凑结果、不重新洗牌继续；冻结受影响房间并通过备份与事件核查。客户端自报一个较高版本仅是待核查线索，不能让恶意客户端凭空伪造版本冻结全桌。跨区域灾难与数据丢失处理需要单独运营流程。

## 12. 背压、任务预算与机器人

| 资源 | 初始提案 | 到达上限 |
| --- | --- | --- |
| 每房间 Channel | 64 项，客户端最多 56 | TryWrite 失败立即 Busy，内部保留 8 项 |
| 单条命令消息 | 16 KiB，未压缩 | 在解码前拒绝超限；另限 Protobuf 嵌套深度、重复字段数量、字符串/bytes 长度 |
| 默认输入结构界限 | 嵌套深度 8；牌/席位列表不超过规则上限；普通文本 256 字符 | 解码/语义验证拒绝；不是靠总字节数唯一防护 |
| 连接入站缓冲 | 最多 4 条完整消息 | 网络背压与每身份/连接限流，不藏无界队列 |
| 单连接出站 | 16 条或 64 KiB，任一先到 | 关闭慢连接，重连取快照；不阻塞 actor |
| 单连接写入 | 最长 5 秒 | 断开，不撤销已经提交的动作 |
| 同身份同房间 | 最多两个连接、一个 controller | 超额拒绝，切换控制使用 epoch |
| 机器人任务 | 每节点两项执行、八项等待；每房间一个有效任务 | 队列满不积压；有服务能力但计算不足时使用合法保守策略 |
| 数据库短事务 | 目标上限 3 秒 | 超时进入提交结果确认，不能无限占租约行锁 |
| 公开历史/回执缓存 | 每房间 128 条或 128 KiB 公开事件；256 条回执 | 淘汰缓存，必要记录回查数据库 |

默认关闭消息压缩，避免无必要的解压资源风险；未来启用时同时限制压缩前后大小。服务端和客户端 Protobuf 解析器必须有可落实的大小与递归限制，并限制具体重复字段数量。服务令牌或票据交换使用独立且有限的 HTTP 消息额度，不绕开入站保护。

机器人工作进程仅接收本人的合法观察、当前规则摘要、room_id、match_id、state_revision、turn_id、owner_epoch 与任务 ID。抽样随机源与真实洗牌分离，进程不加载数据库凭据和未公开牌。结果回到 actor 后重新检查身份、所有权、版本、turn、期限与动作合法性；RECOVERING、旧 owner 或旧 turn 的结果直接失效。

计算取消只保证结果不再有效，不假定能立刻停止正在运行的代码。超预算或崩溃工作进程由监督器终止/重建；不会在 actor 持锁时等待。客户端 Mod 不得指定服务器加载任意策略类型或访问文件/网络；服务端策略必须在已审核集合中选择。

每条连接单独 sender；订阅建立在 actor 顺序点排入当前完整快照，再接收后续提交，避免“读快照到开始订阅”的缺口。重连认证、路由至当前 owner、认领控制、查询待确认 command_id，然后接受新的 stream 快照。旧 stream、较低 state_revision 和不匹配 match 的消息不能回滚状态。

## 13. 故障与协议验收

下表是后续开发、测试与部署演练的要求，不表示测试已经存在或已经通过。

| 场景 | 必须满足 |
| --- | --- |
| SteamID/房主/SeatId 被客户端伪造 | 使用验证后的身份与服务器成员关系，拒绝越权 |
| 将 publisher key 或他人底牌搜索客户端包/流量/Mod 视图 | 不存在这些服务端秘密；正式摊牌范围除外 |
| 同命令 ID 连发或断线后重发 | 最多一次筹码/牌库效果，返回原回执；换 payload 明确冲突 |
| 加注 ACK 丢失后 revision/控制权已变 | 先查原回执，返回原成功，不重复扣钱或误报新失败 |
| 不足完整幅度全下、失去加价权、附加池不能购边池资格 | 服务端逐动作验证，客户端表示与金额建议不能绕过标准规则 |
| 旧 owner 暂停后恢复，新 owner 已接管 | 数据库拒绝旧 epoch 的状态、回执、计时写入 |
| 旧 owner 提交结果未知时接管 | 先确认事务/回执，恢复同一历史，不并行发两套牌 |
| DB 暂时不可达 | 本地冻结，恢复后持久 RECOVERING/RESUME；不因故障时长自动弃牌 |
| 同 recovery_id 重启或重复 RESUME | 同一冻结预算仅恢复一次；没有重新获得完整行动时长 |
| 持续服务抖动 | 补偿和自动恢复次数有持久上限，必要时保持冻结，不把抖动变成无限行动时间 |
| 仅一位玩家断网 | 全桌不暂停，该玩家原期限继续；重连不刷新 deadline |
| 已过期客户端动作先于延迟 timer 被消费 | 仍过期；健康期间已按时入队动作不被 timer 插队剥夺 |
| 3D 动画未播完、低帧率、修改本地牌面 | 只影响该客户端表现，不改变权威牌和行动轮次 |
| Protobuf 超大/深嵌套/重复字段洪峰 | 受限解析与准入，不能挤垮所有房间 |
| 慢接收者、机器人挂死 | 单连接断开或工作进程回收，其他玩家与房间持续可响应 |
| 当前局已改航后 owner 崩溃 | 原公共牌、弃牌、费用、顺位、私牌及未来牌库一致恢复 |
| 异步主库复制未追上就失效 | 不宣称无损；可疑房间冻结，按既定数据恢复流程处理 |
| 扩展房恢复时服务器缺少原规则包 | 不改用标准规则继续，不执行客户端提交的替代程序集 |

评审通过后才进入实现：先协议契约和纯领域端口，再持久化 actor、fencing/恢复故障测试、Steam 认证集成，最后连接原生 3D 表现。不能以能看到五张 3D 卡牌代替对重复扣款、隐藏信息或故障接管的验证。
