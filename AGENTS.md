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

- `main` 已含 0.2 全部实现（约 27 个提交）。测试 156/156，构建 0 警告。
- 覆盖矩阵 12 项全部落地；其中 7 项已实机核对，5 项因**无硬件/内网**只有测试覆盖。
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
