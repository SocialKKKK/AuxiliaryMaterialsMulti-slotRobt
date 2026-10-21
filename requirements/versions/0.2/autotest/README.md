# 0.2 现场自动化测试

把 [`../现场自测清单.md`](../现场自测清单.md) 里的手工步骤变成可脚本执行的检查：
**界面用 UI Automation 驱动 + 截图留证，结果用 `app.db`/日志断言**。目标是「AI 能自己跑一遍并给出结论」，
人能腾出手来做只有人能做的事（物理世界、放行真实动作）。

---

## 0. 下次到现场：就这五步

1. 笔记本连**车间网 Wi-Fi**（地址 `172.19.162.x`），保留 Clash TUN，在 TUN 设置中排除 `172.19.162.0/24` 与 `192.168.71.0/24`（2026-10-09 实测 SSH 与四模块正常，Codex 保持联网）。端口验证仍以真实协议响应为准。
2. 起隧道并自检（**本机没有常驻自动连接**，2026-09-29 已移除那个登录任务）：

   ```bash
   cd /d/code/_agv-probe && ./connect.sh
   ```
   最后一行出现 `SSH : OK  SSH-2.0-OpenSSH_for_Windows...` 即为通了。

3. 连柜体机：`ssh agv-cabinet`（别名在 `%USERPROFILE%\.ssh\config`，走 `127.0.0.1:122` 隧道）。
4. 要跑界面场景（细节见 §4.2）：

   ```bash
   scp -O -i D:/code/_agv-probe/keys/site_key -P 122 -r requirements/versions/0.2/autotest ht@127.0.0.1:C:/autotest/
   ssh agv-cabinet 'schtasks /Run /TN WireCabinet-AutoTest'
   ```
   **`scp` 必须带 `-O`**：柜体机没配 sftp 子系统，默认方式会 `Connection closed`。

   只想**看界面**（渲染对不对、有没有弹窗、卡没卡）不必写脚本：走 VNC，
   **`172.19.162.55:5900`**，客户端必须 RealVNC Viewer，口令见 `_agv-probe/现场连接手册.md` §2。
   跑 UIA 场景期间 VNC **只连不点**。分工与标准循环见 §1.1。

5. 不通时：`./connect.sh --find`（车载路由换了 IP）；若它提示 `.10` 连不上，
   **照它打印出来的那段 PowerShell 到柜体机上跑一次**即可恢复。

凭据：RCS `admin`/`密码见本机 _agv-probe/snap/site-appsettings.json（不入库）`；车载路由 `172.19.162.55` = `admin`/`admin`；HMI 门禁 = 维护 `S0020310`、物料员 `1`。

---

目录内容：

| 文件 | 作用 |
|---|---|
| `ui/uidriver.ps1` | 步骤驱动的 UIA 驱动：读 JSON 步骤 → 操作界面 → 输出 `result.json` + PNG 截图 |
| `ui/steps/*.json` | 场景步骤（`oplog-smoke` 冒烟、`dump-current` 界面探测） |
| `run-on-cabinet.cmd` | 在柜体机上执行一个场景的入口（计划任务调它，或人到机器上直接跑） |
| `dbcheck.py` | `app.db` 断言：按动作类型计数、门开/关配对、待补偿队列、UTC 口径 |
| `expect/example.json` | `dbcheck.py verify` 的期望文件模板 |

---

## 0.1 记录口径优化：部署与复核（2026-10-09 实现，10-10 12:55 部署、10-11 现场复核通过）

一个格口的一次存/取操作现在只写**一条**记录，批量操作逐格一条，开门 / 关门 / 清空是这条上的字段。这次部署除了 DLL，
**还必须同步 `sql-catalog/items/`** —— 新增了两个写条目，缺了它们回填门时刻、更新 detail 会报
「缺少 SQL 条目」（业务不受影响，但记录会缺门时刻；OP 回填失败已进入流程告警）。

**部署前先看 [部署前复查](../verification/2026-10-09/部署前复查.md)**：`db438eb` Release 264/264 通过，但同秒记录回填与补偿故障仍有可复现问题，不能据此直接判定可部署。

**必须同步到柜体机的两样**

1. Release 产物（`WireCabinet.Hmi.exe` / `WireCabinet.{Data,Slots,Flow,Rcs}.dll` 等）→ 原地覆盖
   `...\bin\Release\net8.0-windows\`。
2. **`requirements/data-access-sql/sql-catalog/items/`**：新增 `app.operation_log.backfill_door_ts.yaml`、
   `app.operation_log.update_detail.yaml`，改过 `app.operation_log.insert.yaml` 与 3 个读条目。
   应用按 `CabinetPaths` 从 exe 目录**向上找** `requirements/flows/flow-sql-map/index.yaml` 定位仓库根，
   再从 `requirements/data-access-sql/sql-catalog/items` 读 SQL —— 所以这个目录必须在。

数据库不用手工动：启动时 `OperationLogSchema.Ensure` 幂等补 `detail` / `corrects_id`
两列并建 `trg_app_operation_log_append_only` 触发器；先前本地预演已存在的 `slot_count` 列不会删除。已在**现场库副本**（10-09 17:12 拉的 9624 条）上
预演过：真数据原样保留、加列与建触发器正常、触发器能挡住改事实且放行 detail、记录页正常渲染。

**回滚只换 DLL 就够**：新增列对旧代码是可空且被忽略的（旧 INSERT 显式列出列名），触发器只拦 UPDATE，
而旧代码从不 UPDATE 记录表。不需要恢复库。

**现场怎么验（每条只要一条记录）**

| 动作 | 期望 |
|---|---|
| MH 存一盘焊丝 | `mh_load_available_wire` **1 条**，`door_open_ts` / `door_close_ts` 都非空 |
| OP 一轮存废取新 | `op_return_wire` 1 条 + `op_issue_available_wire` 1 条（领用与取出已合并），两条都有门时刻 |
| MH 开门取旧 | `mh_take_wire` **逐格一条**（打开 N 格 = N 条），每条带格口号与取走的批号 |
| 开门后再关门 | 不新增记录，那条开门记录的 `door_close_ts` 被回填 |
| 自动返航连续失败 | `agv_move` **1 条**，`detail.count` 记次数 |

**断言必须带时间窗**，否则会被历史数据误伤：

```bash
python dbcheck.py counts <app.db> --since "<动作前的 UTC 时刻>"   # 一眼看每种动作几条
python dbcheck.py pairs  <app.db> --since "<动作前的 UTC 时刻>"
python dbcheck.py verify <app.db> --expect expect/mh-load.json   # since_utc 已写进 expect
```

`counts` 是现场核对口径最省事的命令：动作前 `snapshot` 记下 `ts_max_utc`，做完动作跑一次，
输出里每种动作各几条一目了然，不用在界面上数。一次「人的操作」应当恰好 1 条
（一轮存废取新是 2 条：归还 + 领用并取出）。

`pairs` 不带 `--since` 会扫全表，把口径优化前那 **854 条「无来源关门」**（`door_close_ts` 有值、
`door_open_ts` 为空；10-09 已修掉，最晚一条 `2026-10-09 08:51:28`）报成不一致；
`verify` 的两条门时刻检查已按 `expect.since_utc` 限定。

**无指令开门**：开一格再关，确认不冒出 `is_uncommanded = 1`。
10-09 的误报修复已随部署生效，10-11 现场 201 条记录（含 40+ 轮存废取新）中一次未出现。

---

## 1. 通道分工（沿用上次现场结论）

| 通道 | 用途 | 备注 |
|---|---|---|
| **UI Automation（本目录）** | 点界面、填表单、切页、截图 | 必须在**柜体机的交互会话**里跑；比 VNC 点坐标稳 |
| **VNC**（2026-10-21 起纳入 runbook） | 人眼确认：渲染、弹窗、卡死；或手动点一次触发业务 | 服务态镜像 **session 1 控制台**；只给眼睛、不给证据，用法见 §1.1 |
| **SSH / scp** | 传包、重启、读日志、取 `app.db` | 确定性输出，可脚本化 |
| **计划任务** | 远程触发 GUI 会话内的脚本 | SSH 在 session 0，起不了界面 |
| **外部只读裁判** | 柜体 DI/DO（`_agv-probe\cabinet_state.py`）、MES 只读探针（`_mes-probe`）、RCS 干跑（`AgvDispatch.Sample`） | 「门真的开了」「MES 真被写了」只能这么验 |

**优先级：能用数据/日志判定的就不用点界面。** UI 驱动只用在「必须看像素」的步骤
（记录页渲染、导出弹框、错误提示、格口状态颜色）。

### 1.1 双通道：VNC 看 + SSH 验（以后就这么用）

两条通道的盲区正好互补，所以合起来比单用任何一条都快：

```
qwinsta:  >services  0  断开        ← ssh 落在这里：没有桌面，窗口句柄为 0，点不了按钮
           console  HT  1  运行中    ← VNC 看到的是这里，HMI 就在这个会话
```

- **SSH 的盲区**：session 0 里看不到界面，也点不动。以前为了「看一眼 + 点一下」得靠计划任务桥把
  UIA 脚本投到 session 1，一轮几十秒起、还挑控件。
- **VNC 的盲区**：只有眼睛。它不给记录条数、门态配对、待补偿、DLL 时间戳；你点过的每一步也不可复现、
  不留痕。**VNC 的点击不算证据。**

| | SSH | VNC |
|---|---|---|
| 传什么 | 命令 / 文件（文本流） | 屏幕像素 + 鼠标键盘 |
| 看得到界面 | ❌ session 0 | ✅ session 1 控制台 |
| 给得出证据 | ✅ 库、日志、时间戳 | ❌ 不可复现、无留痕 |
| 带宽 | 几十 KB，Wi-Fi 抖动无感 | 几 Mbps，抖动会拖影 |
| 走哪条路 | `127.0.0.1:122` 隧道（dropbear 空闲 5 分钟踢） | 车间网直达 `172.19.162.55:5900`（一条 DNAT） |

**标准一轮：VNC 里看/点 → 等 1~2 秒（WAL 异步写）→ SSH 拉库断言。** VNC 负责触发与确认，
SSH 负责作证。

| 场景 | SSH 干 | VNC 干 |
|---|---|---|
| 部署/重启后确认 | 覆盖 DLL、`schtasks /End` 再 `/Run /TN WireCabinet-StartHmi`、查 PID 与启动时间 | 看 HMI 是否真起来、顶栏是否 **6 个页签** |
| 功能验证（导出、追溯） | 拉库跑 `dbcheck.py`、核导出文件字节数 | 点一次导出、填一次路径 |
| 排障 | 进程 / 端口 / `WireCabinet.*.dll` 时间戳 / 库状态 | 只看人眼才判断得出的：花屏、卡死、按钮禁用、弹窗原文 |
| 演示/验收 | 旁证记录条数、留痕行 | 走完整业务流程 |

**纪律**

1. 能用数据判的绝不手点（沿用 §1 优先级）；手点只为「必须看像素」服务。
2. **同一时刻只有一个人持有鼠标**：VNC 服务态共享物理控制台，操作工在柜子前时你的点击会抢他的；
   同时开两个 Viewer 也会互抢。
3. **跑 UIA 场景时 VNC 只连不点**：驱动正在发输入，你再点会打乱焦点与坐标。
4. **别用 VNC 拖文件覆盖 DLL**：`scp` 才能校验。判断部署版本看 `WireCabinet.*.dll` 时间戳，不是 `exe`
   （apphost 可能还是旧的）。
5. **重启 HMI 会中断柜前作业**：`/End` + `/Run` 前先确认没人在操作、且属于允许中断的窗口。

**参数（2026-10-21 实测）**：`.10` 上是 **RealVNC VNC Server 5.3.2 Enterprise**，Windows 服务
`vncserver`（LocalSystem、自动启动、`-service`），监听 `0.0.0.0:5900`，握手 `RFB 005.000`，
只报 `13,133,5,129` —— **必须用 RealVNC Viewer**（TightVNC / UltraVNC / TigerVNC 只认标准 type 2，
会报不支持的认证类型）。车间网 `172.19.162.55:5900` 已验证可达；隧道另留 `127.0.0.1:15900` 备用。
口令见仓库外 `_agv-probe/现场连接手册.md` §2（含现场口令，不入库）。

**AI 侧抓屏（只抓不点，2026-10-21 建）**：柜体机上有计划任务 `WireCabinet-Shot`（`ht` 交互登录时运行）
→ `wscript` 隐藏启动 `C:\autotest\shot-once.ps1` → 存 `C:\autotest\runs\shot\console.png`（1024×768，session 1）。

```bash
ssh agv-cabinet 'powershell -NoProfile -Command "Start-ScheduledTask -TaskName WireCabinet-Shot"'
# 等 2 秒
scp -O -i D:/code/_agv-probe/keys/site_key -P 122 ht@127.0.0.1:C:/autotest/runs/shot/console.png ./shot.png
```

它**不动鼠标键盘**，所以可以在你 VNC 连着、甚至人正站在柜前时用；要判断「界面渲染对不对」优先用它，
不必为一个像素再写 UIA 场景（UIA 才会抢焦点）。

### 1.2 三方分工：AI / SSH / VNC（2026-10-21 定）

| 角色 | 只做 | 不做 |
|---|---|---|
| **AI** | 构建、部署、跑 UIA 场景、抓屏、拉库断言、只读侦察（DI/DO、MES、RCS 干跑）、写结论 | 不点真实开门/控车/写 MES；不盯 VNC 直播（做不到，也没必要） |
| **SSH** | 命令与文件：传包、重启、查进程/端口/`*.dll` 时间戳、取库、触发计划任务 | 看不到界面（session 0），起不了 GUI |
| **VNC**（人） | 看像素、点按钮、过模态密码框/保存对话框、按坐标点 54 格瓦片、判断「手感对不对」 | 不作证据；不与人抢控制台（服务态共享物理控制台） |

**决策顺序（能停在前一步就别往下走）**

1. 能用库/日志/时间戳判定的 → **SSH**。最便宜、可复现、可批量。
2. 必须看像素 → **我抓屏**（3.7 s 一轮、无损、不占输入）。不要为这个再写 UIA 场景。
3. 要看过程/手感，或必须过模态框、按坐标点控件 → **人在 VNC 里操作**，我同时抓屏 + 留证。
4. **结论只认 SSH 侧**（`app_operation_log`、文件字节数、DLL 时间戳）；VNC 与抓屏只用于**定性**与**触发**。

**2026-10-21 首次实况使用（记录）**

- 用户在本机 RealVNC Viewer 7.15.1 连 `172.19.162.55:5900`；柜体机侧 `netstat` 见
  `192.168.71.10:5900 ← 172.19.162.127:54598 ESTABLISHED`（`vncserver` PID 6392）。
- 我建 `WireCabinet-Shot`，实测端到端 **3.7 s**（触发 2.0 / 出图 3.1 / 含回传 3.7），抓屏不占鼠标键盘。
- 抓屏确认 0.2 在跑：6 个页签、停 OP 页、状态行「已到站 线边焊丝柜（OP），已切换工作站界面。」、`IO C4 67 24 BE`。
- 顺手发现：桌面右下有「激活 Windows」水印（Win10 Pro 19045 未激活）。不影响 HMI，但演示/投屏会被带出去。
- VNC「慢」的实测：Wi-Fi 802.11ac 173 Mbps / 信号 99% / RTT 5 ms；空闲 `vncserver` 0.2%、HMI 0.4%（单核）
  → 瓶颈是 5.3.2 + Win10 无镜像驱动 + WPF 全窗重绘，不是网络。Viewer 侧降画质/编码优先 ZRLE 可缓解。
- 未做：**维护试开**（用户 2026-10-21 暂缓），仍是覆盖矩阵唯一空白项。

---

## 2. 已经验证到什么程度（本机 mock 实例）

- `ui/uidriver.ps1`：**24 步冒烟场景全通过**——切 6 个页签、填工号/批号、
  按中文名点「查询/重置」、按 AutomationId 断言、截图、导出界面树。
  并用 `asserttext` 实证了界面状态：「按不存在的批号查询」→ 摘要变「无匹配记录」→ 点重置 → 回到「第 1 页 · 每页 20 条」。
- `dbcheck.py`：`snapshot` / `tail` / `pairs` / `verify` 均在本机库上跑通，
  UTC→本地换算正确（库里 `04:40:34` ↔ 界面 `12:40:34`），失败项能返回退出码 1。
- **现场已验证（2026-09-28）**：经车载路由隧道 SSH 登录 `.10`；通过 `WireCabinet-AutoTest` 计划任务在交互会话运行只读 `dump-current`，`result.json` 返回 `ok=true`，生成 1024×728 截图和 103 项控件树。当时本机还挂着一个 `AGV-Cabinet-AutoConnect` 登录任务自动起隧道，**已于 2026-09-29 移除**（常驻后台脚本，且会弹空白窗）；现在隧道靠 `connect.sh` 手动拉起，现场 Wi-Fi 下 `ssh agv-cabinet whoami` 已验证可用。
- **本目录自动化脚本未直接执行**：真实开门/控车、写 MES；工程现场主链已于 9 月 29 日人工实跑，见 §8。

## 3. 现场前提（缺一不可）

1. **柜体机有 .NET 8 Desktop Runtime x64**（HMI 发布是非自包含的）；PowerShell 5.1 自带的
   `UIAutomationClient` 已够用，不需要装任何第三方库。
2. **现场有效配置已备份且迁入 `appsettings.Local.json`**：2026-10-09 升级已备份两份原文件，将原现场共享配置合并迁入 Local，并逐项确认有效配置未变。MES 连接串、RCS 地址账号。注意这次提交把共享
   `appsettings.json` 里的凭据清空了，**同时** `MaintAccess/MhAccess` 的密码也来自它——
   Local 丢了不仅 MES/RCS 失配，维护/物料员/记录页的密码门禁会**静默失效**。
3. **`slot-config/io-modules.yaml` 在位**（4 模块真实 IP）。
4. **只跑一个实例**；升级前先备份车上 `app.db`。

## 4. 怎么跑

### 4.1 人在机器上（最简单，今天就能用）

把 `requirements/versions/0.2/autotest/` 整个目录拷到柜体机（例如 `C:\autotest\`），然后：

```bat
C:\autotest\run-on-cabinet.cmd C:\autotest\ui\steps\oplog-smoke.json C:\autotest\runs\001
```

输出：`C:\autotest\runs\001\result.json` + 各步 PNG。`result.json` 里 `ok=false` 时看 `steps[].detail` 定位失败步。

### 4.2 远程/AI 驱动（计划任务桥）

GUI 脚本必须落在已登录用户的会话里，所以远程执行要过一次计划任务：

```bat
REM 在柜体机上建一次任务（需要管理员权限；/IT /NP = 仅在 ht 登录时运行、不存密码）
schtasks /Create /TN WireCabinet-AutoTest ^
  /TR "C:\autotest\run-on-cabinet.cmd C:\autotest\ui\steps\oplog-smoke.json C:\autotest\runs\001" ^
  /SC ONCE /ST 00:00 /RU ht /IT /NP /F

REM 以后每次触发
schtasks /Run /TN WireCabinet-AutoTest
```

> 现场 SSH 已通过 `ssh agv-cabinet` 验证；该别名连接本机 `127.0.0.1:122` 的车载路由隧道。
> 柜体机已安装 `WireCabinet-AutoTest` 交互计划任务，可由 SSH 写入任务文件后触发。

### 4.3 拉回结果并做数据断言

```bash
# 1) 动作前记一个时间下界（UTC）
python dbcheck.py snapshot  app.db            # 记下 ts_max_utc

# 2) 执行场景（4.1 或 4.2），期间业务动作发生

# 3) 取库：三个文件必须一起取（HMI 在用 WAL）。柜体机不能直连，一切走 127.0.0.1:122
DB="C:/Users/HT/Desktop/AuxiliaryMaterialsMulti-slotRobt/requirements/versions/0.1/wire-cabinet/data"
scp -O -i D:/code/_agv-probe/keys/site_key -P 122 \
  ht@127.0.0.1:"$DB/app.db" ht@127.0.0.1:"$DB/app.db-wal" ht@127.0.0.1:"$DB/app.db-shm" ./snap/

# 4) 断言
python dbcheck.py verify ./snap/app.db --expect expect/放料一次.json
python dbcheck.py pairs  ./snap/app.db        # 门事件配对
python dbcheck.py tail   ./snap/app.db -n 20  # 本地时间，肉眼扫一眼
```

## 5. 写断言前先探测界面

多数控件有 `x:Name`（→ UIA 的 AutomationId，可直接用）：6 个页签 `RbOp/RbMh/RbMaint/RbMesRecon/RbAgv/RbOpLog`，
记录页 `LotNoBox/SearchBtn/ResetBtn/ExportBtn/RecordList/SummaryText`，MH 页 `OperatorIdBox/WireLotBox`，等等。
**没有名字、只能按文本或坐标处理的**：

- **54 个格口瓦片**：运行时代码生成的 `Border`，无 UIA peer、无 InvokePattern → 用 `clickpoint`
  （窗口相对物理像素，可从截图量）。
- 记录/待补偿列表的**行项**：无名字 → 按行文本匹配或坐标。
- 弹窗按钮（密码框的「确定/取消」、对账页 9 个按钮、Trace 窗）：无名字 → 用 `controlType + name` 匹配。

不确定就先用探测场景把当前页的树导出来：

```bat
run-on-cabinet.cmd C:\autotest\ui\steps\dump-current.json C:\autotest\runs\dump
```
`tree.json` 里每条是 `{type, id, name}`，照着写 `target`。

## 6. 动作分级（与 AGENTS.md 一致）

| 级别 | 范围 |
|---|---|
| **完全自主** | 构建、跑用例、读日志、取库断言、柜体 DI/DO 只读、RCS 干跑、查 MES 只读、VNC/截图 |
| **需人工放行** | 部署到柜体机（可回滚）、**真实开门**、**控车/回充**、**写 MES 真库** |
| **必须人在现场** | 物理碰撞风险、门前有人、上下料、清场、急停 |

## 7. 现在还不能自动化的部分（别指望它）

- 物理动作本身：放料/取料、拔插 DI 制造「无指令开门」、真实关门。
- 需要改机器配置的故障注入（临时改名 `app_operation_log`）：脚本可以生成命令，
  但改库名属于「需人工放行」，请人执行。
- 导出 Excel 的**落盘校验**：可以点按钮 + 截图，但「文件能被 Excel 打开」仍需人到
  U 盘/本地目录确认一次（清单里唯一没验过的界面路径）。
- 首次现场联调（MES Oracle 真库路径）：第 1 步报错时改的是 SQL catalog，
  需要人看 Trace 窗口的实际 SQL 与报错原文，AI 依据截图/日志给修改建议。

---

## 8. 场景清单与验证状态

| 场景 | 对应自测清单 | 风险 | 本机 mock | **2026-09-29 现场（柜体机）** |
|---|---|---|---|---|
| `ui/steps/dump-pages.json` | 摸底 | 只读 | ✅ 20 步，5 个页面控件树全导出 | ✅ 部署后 6 个页签渲染正常、布局无截断 |
| `ui/steps/oplog-query-export.json` | 第 3 步 | 只读 | ✅ 45 步，真导出落盘 7071 B | ✅ 用真实记录跑通筛选/组合/重置/追溯 |
| `ui/steps/mh-load.json` | 第 2 步 | **真实存料** | ⚠️ 机制 ✅ 18 步 | ✅ **由工程师手工实跑 ×3**（F-A01/F-C02/F-F02），记录链「开门→关门→存料完成」齐全、MES 标记同步 |
| `ui/steps/door-open.json` | 第 4 步 | **真实开门** | ⚠️ 机制 ✅ 25 步；IO 不可达 → 应用正确记 `失败 / 开锁失败：硬件未响应。` | 未单独跑（真实开门已被存料/领用覆盖）|
| `ui/steps/op-return-issue.json` | 第 2 步 | **真实业务** | ❌ 本机跑不了（`OpView` 要求 `MesReady`） | 未用脚本跑；**存废取新由工程师手工实跑并完整通过，零报错** |
| `ui/steps/site-oplog-read.json` | 第 2/3 步 | 只读 | — | ✅ 11/11 步（含门禁密码、`RecordList`/`导出 Excel` 断言）|
| `ui/steps/site-oplog-real.json` | 第 3 步 | 只读 | — | ⚠️ 除最后一条 `StatusText` 断言外全过（导出本身成功，见 §8.2「导出目标目录」）|
| `ui/steps/mesrecon-read.json` | 第 2 步 | 只读 | — | ✅ 8 步；对账页漂移 **0 条** → MES 真库**读**路径通 |
| `ui/steps/agv-dispatch-now.json` / `agv-dispatch-mh.json` | 控车 | **真实车辆** | — | ⚠️ 点击成功但**发不出去**：`sys=CHARGING` 时发车按钮禁用（见 §8.2）。自动返航由应用自己完成 |
| `ui/steps/close-gate.json` / `unlock-gate.json` | 工具 | 只读 | — | ✅ 用于收拾残留的密码弹窗 |

**2026-09-29 现场整轮真实作业（不是脚本，是工程师实操作业）**：MH 存料 ×3、开门即清空 ×3、
操作员存废取新（归还 R-I03 + 领用取出 F-A01）、充满电自动返航、记录页查询/按批号追溯/导出落盘。
库里的 12 种动作类型全部出现过，**待补偿队列全程 0**；MES `DISCOEQPNO` 47→50→49 与格口状态逐条对应。

**本机已验证到的范围**（不只是"脚本没报错"）：界面控件定位、门禁弹窗跳过、页面切换、
真实截图、**导出到原生保存对话框并落盘**、以及**记录通道**——本地库确实多了应用写的
`export_operation_log`（导出留痕）与 `mh_take_wire 失败 / 取出失败：…`（失败也留一条带原因）。

### 8.1 跑一个场景

```bash
# 本机（mock 实例在跑）
cd requirements/versions/0.2/autotest
run-on-cabinet.cmd ui\steps\mh-load.json %TEMP%\autotest\run-001

# 现场（先推包，再经计划任务触发）
scp -O -i D:/code/_agv-probe/keys/site_key -P 122 -r ./autotest ht@127.0.0.1:C:/autotest/
# 写 job.txt（<steps.json>|<outdir>）后：ssh agv-cabinet 'schtasks /Run /TN WireCabinet-AutoTest'
```

数据侧断言（在**跑之前**记下时间下界，跑完取库再验）：

```bash
python dbcheck.py snapshot <app.db>                 # 记 ts_max_utc
python dbcheck.py verify   <app.db> --expect expect/mh-load.json
python dbcheck.py pairs    <app.db>                 # 门事件配对
```

### 8.2 场景写法要避开的坑（都是今天踩出来的）

| 坑 | 现象 | 写法 |
|---|---|---|
| **中文输入法吃掉反斜杠** | 路径里的 `\` 被输入成 `、` | 含路径/反斜杠的文本用 `"useClipboard": true`（剪贴板粘贴） |
| **原生保存对话框不认 ValuePattern** | 文件名框设了值但保存为空 | 同上：剪贴板 + `{"action":"key","value":"{ENTER}"}` 提交 |
| **`key` 会抢焦点**（已修） | 回车送给了主窗口，模态框没提交 | 驱动已改为不抢焦点；对话框内的按键直接发 |
| **不同页面同名控件** | MH 与 OP 都有 `WireLotBox`；每页都有 `StatusText` | 驱动已改为**优先取可见元素**；需要时用 `index` |
| **空列表导出** | 点导出没反应、无文件 | 导出前先 `ResetBtn`，让列表有数据 |
| **窗口最小化** | 截图只有 160×28、坐标全错 | 驱动已改为先 `SW_RESTORE` |
| **会话 0 看不到界面** | SSH 里 `Get-Process` 窗口句柄为 0、UIA 找不到窗口 | 一律经 `WireCabinet-AutoTest` 计划任务跑（`sessionid=1`） |

**2026-09-29 现场新增的坑**：

| 坑 | 现象 | 写法 |
|---|---|---|
| **导出目标目录必须已存在** | 对话框填了路径、回车也提交了，但**文件没落盘、库里也没有 `export_operation_log`** | 导出前先在柜体机建好目录；核对时以「文件 + 留痕」两条都为真 |
| **门禁弹窗是模态的，`InvokePattern` 会阻塞** | 点带门禁的页签（维护/物料员/记录页/对账）时驱动卡住数分钟 | 先取消或解锁当前密码框，再切换页签；不能按选中状态认定页面已加载，须断言目标内容。10/9 驱动已拦截 owner 禁用时切页，以及输入框未找到后的 Enter |
| **`GetClickablePoint` 会超时但点击其实已生效** | 日志报 `操作超时` ／`element not found`，可界面已经跳转/弹窗了 | **别只看日志**，用紧随其后的 `dump`／截图判断真实状态 |
| **`sys=CHARGING` 时发车按钮是禁用的** | 「前往所选作业站」点了没反应，RCS 侧 `order=` 仍为空 | 此为 9/29 旧版行为；10/9 修复已部署，实机 CHARGING 发车按钮可用，实际离站/90% 返航待验 |
| **柜体机上没有 python、也没有 git** | `dbcheck.py` 跑不起来；`git` 命令不存在 | 库拉到本机再断言；构建也必须在本机做再推包（柜体机只有 .NET 8 运行时）|

