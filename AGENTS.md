# AGENTS.md — 焊丝发放系统（本文件每次新对话自动加载，请保持简短）

## 这是什么

工业一体机上的**焊丝发放柜** HMI（WPF，固定 1024×768 触控屏）。当前版本 0.2，唯一新增需求是
**物料存取记录与追溯**（记录每一次存取动作、可查询/追溯/导出）。

## 代码在哪（**反直觉，务必先读这两条**）

| 内容 | 位置 |
|---|---|
| 应用本体（WPF，含 0.2 的功能） | `requirements/versions/0.1/wire-cabinet/` ← **不在仓库根目录** |
| 解决方案 | `requirements/versions/0.1/wire-cabinet/WireCabinet.sln` |
| 分层 | `src/WireCabinet.{Core,Data,Slots,Slots.Hardware,Rcs,Flow,Hmi}` + `Tests` |
| 应用库 SQL（唯一合法位置） | `requirements/data-access-sql/sql-catalog/items/`（**禁止内联 SQL**） |
| **运行时流程定义** | `requirements/validation/wire-flow-lab/data-access-sql/flow-sql-map/flows/` ← 产品实际读这里，不是 `requirements/flows/`（后者是规格） |
| 0.2 文档 | `requirements/versions/0.2/{需求范围,验收清单,变更说明,现场自测清单,运维说明-归档与备份}.md` |

## 怎么跑

```bash
cd requirements/versions/0.1/wire-cabinet
dotnet build WireCabinet.sln                        # 期望 0 警告 0 错误
dotnet test src/WireCabinet.Tests/WireCabinet.Tests.csproj   # 期望 156 全通过
src/WireCabinet.Hmi/bin/Debug/net8.0-windows/WireCabinet.Hmi.exe   # 启动（单一实例）
```

本机是 mock 模式：`src/WireCabinet.Hmi/appsettings.Local.json`（**已 gitignore**）把 MES 连接串清空。
该文件还有 `Debug:FlowTraceWindow:Enabled = true` → 会多开一个「流程调试 Trace」窗口，可看每步实际 SQL。
（共享的 `appsettings.json` 里它是 false，现场不会弹。）

## 硬性约定（改动前必读）

1. **记录表只增不删**：`app_operation_log` 不得出现 `UPDATE` / `DELETE`。有用例扫描全部 catalog 与源码强制。
2. **不修改 0.1 的流程定义**（0.2 需求 §1.3）；已有一处例外见 `变更说明.md` §3。
3. **记录写入不得影响业务**：写入失败只告警 + 转待补偿，绝不改流程 outcome（§1.4）。
4. **记录时间存 UTC、显示与筛选按本地时区**（`OperationLogTime`，两个方向都要转）。
5. **改界面必须实机截图核对**（1024×768、禁止滚动、触控尺寸 ≥44px）。顶栏 6 个页签是实测定过尺寸的。
6. 保存密钥类文件已从版本控制移除（`appsettings.Local.json` / `io-modules.yaml` / `data/app.db*`）；
   **`io-modules.yaml` 需现场机器从 `io-modules.template.yaml` 复制**。

## 当前状态

- `main` 已含 0.2 全部实现（相对 0.1 基线约 30 个提交）。测试 156/156，构建 0 警告。
- 覆盖矩阵 12 项全部落地。**已实机核对**（截图或查库）：记录页渲染、按批号追溯、
  批量开门的记录条数、待补偿人工重试、MH 操作人姓名解析、时区显示、静置不刷记录。
  **只有测试覆盖**：维护试开、无指令开门、开门即清空、AGV 六条命令的正向路径、MES 真库。
- **现场联调未做**；MES Oracle 真库路径从未执行过（全部验证基于 SQLite mock）—— 这是最大风险。
- 按 `requirements/versions/0.2/现场自测清单.md` 的顺序验。

## 未决 / 已知限制

| 事项 | 状态 |
|---|---|
| MH 存料的 `eqp_no` 恒为空 | 未决：存料流程无此信息，需确认是否认可「该场景不适用」 |
| 查看/导出留痕无操作人 | 已知限制：记录页门禁只有密码、无工号（§2） |
| 导出 Excel 的落盘 | 数据层有 9 个用例覆盖，界面只差人工点一次 |
| 归档/备份的周期与存放 | 做法已定，参数待现场填实（`运维说明-归档与备份.md`） |
| 两套 FlowEngine | 产品一份、`validation/wire-flow-lab` 一份 fork（已漂移），长期需收拢 |

## 与用户协作

- 用户偏好**直接动手**而不是反复确认（原话：「你不能自己看着做吗」）。
- 提交习惯：**每个提交独立可构建、可过测试**；界面类改动要实机截图核对后再提交。
- 涉及**真实硬件/车辆**的动作（如 `--execute` 控车）必须先与用户确认 —— 参考
  `AgvDispatch.Sample` 的约定：**默认干跑，写接口需显式 `--execute`**。

## 收尾时同步文档（常驻要求）

**每完成一个工作阶段，检查文档是否与实际不符**，重点是这三个易漂移处：

1. `requirements/versions/0.2/变更说明.md` —— 「未做」是否还成立、新增的约定是否写进去了
   （例：开了 WAL 却仍写着「未开 WAL」；时间存 UTC 这种口径没写）
2. `requirements/versions/0.2/` 下各文档里的**数字与状态**（用例数、提交数、已验/未验）
3. 本文件（`AGENTS.md`）的「当前状态」与「未决 / 已知限制」

要求：**只改说错了话的地方**，不要为每条改动补一段（文档一膨胀，下个对话读它就占了本该留给代码的上下文）。
改动随该阶段一起提交，提交信息里说明「同步文档」的原因。

> 为什么写在这里而不是做成 hook：hook 只能跑 shell 命令，能更新数字与 git log，
> 但判断不了「这句话已经过时了」——那需要读代码与文档做比对，只能由 AI 在收尾时做。
