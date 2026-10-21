using System.Globalization;
using System.Text;
using WireCabinet.Data;

namespace WireCabinet.Flows;

/// <summary>
/// YAML 驱动的流程引擎：按 node_order/outcome_routing 单步执行，解析 input_mappings → 绑定参数 →
/// 执行 SQL / 控门 / 判定 → 记录 trace，并在运行时检测流程/SQL 问题。
/// </summary>
public sealed class FlowEngine
{
    private readonly EngineServices _svc;

    public FlowEngine(EngineServices svc) => _svc = svc;

    public FlowDefinition? Flow { get; private set; }
    public FlowRunContext? Context { get; private set; }
    public string? CurrentNodeId { get; private set; }
    public bool Finished { get; private set; }
    public List<TraceEntry> Trace { get; } = new();

    private int _step;

    public FlowNode? CurrentNode => Flow is not null && CurrentNodeId is not null ? Flow.Node(CurrentNodeId) : null;

    public void Begin(FlowDefinition flow)
    {
        Flow = flow;
        Context = new FlowRunContext(flow);
        CurrentNodeId = flow.Start;
        Finished = false;
        _step = 0;
        Trace.Clear();
        _svc.Slot.Reload();
    }

    /// <summary>失败或重试时回到指定 user_input 节点，清除 Finished 以便继续推进。</summary>
    public void RewindToNode(string nodeId)
    {
        if (Flow is null || !Flow.Nodes.ContainsKey(nodeId))
            return;
        CurrentNodeId = nodeId;
        Finished = false;
    }

    /// <summary>执行当前节点并前进一步，返回该步 trace。</summary>
    public TraceEntry StepOnce()
    {
        if (Flow is null || Context is null || CurrentNodeId is null || Finished)
            throw new InvalidOperationException("引擎未开始或已结束。");

        var node = Flow.Node(CurrentNodeId);
        _step++;

        if (node is null)
        {
            var missing = new TraceEntry
            {
                Step = _step,
                NodeId = CurrentNodeId,
                NodeText = "(节点不存在)",
                NodeType = "missing",
                Status = Severity.Error,
                Outcome = "error",
                Result = $"路由指向不存在的节点 '{CurrentNodeId}'"
            };
            missing.Findings.Add(new Finding
            {
                Severity = Severity.Error, FlowId = Flow.FlowId, NodeId = CurrentNodeId,
                Category = "断链", Message = $"outcome_routing 指向不存在的节点 '{CurrentNodeId}'。"
            });
            Trace.Add(missing);
            Finished = true;
            return missing;
        }

        var entry = Execute(node);
        Trace.Add(entry);

        if (node.Type == NodeType.Terminal || entry.NextNode is null)
            Finished = true;
        else
            CurrentNodeId = entry.NextNode;

        return entry;
    }

    private TraceEntry Execute(FlowNode node) => node.Type switch
    {
        NodeType.UserInput => ExecUserInput(node),
        NodeType.Query => ExecQuery(node),
        NodeType.Decision => ExecDecision(node),
        NodeType.Write => ExecWrite(node),
        NodeType.SlotOpen => ExecSlotOpen(node),
        NodeType.SlotOpenBatch => ExecSlotOpenBatch(node),
        NodeType.SlotClose => ExecSlotClose(node),
        NodeType.Terminal => ExecTerminal(node),
        _ => ExecUnknown(node)
    };

    // ---------- 各节点类型 ----------

    private TraceEntry ExecUserInput(FlowNode node)
    {
        var ctx = Context!;
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        ctx.UserInputs.TryGetValue(node.Id, out var provided);
        var sb = new StringBuilder();
        foreach (var f in node.Outputs)
        {
            string val;
            if (f.From is not null)
                val = Resolve(f.From)?.ToString() ?? "";
            else if (provided is not null)
                val = provided.TryGetValue(f.Name, out var v) ? v : "";
            else
                val = f.Default;
            row[f.Name] = val;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append($"{f.Name}={val}");
        }
        ctx.NodeResults[node.Id] = row;

        var findings = new List<Finding>();

        // 关门确认：§3.3 把 door_close_ts 定义为「关门确认时刻」。操作员流程的关门是用户确认
        // （closeReturnSlotDoor / closeIssueSlotDoor 是 user_input，不是 slot_close 节点），
        // 所以按节点声明的 closed 输出识别，而不是靠节点名。
        if (node.Outputs.Any(o => string.Equals(o.Name, DoorClosedOutput, StringComparison.OrdinalIgnoreCase)))
        {
            var closedAt = NowUtcText();
            ctx.Runtime[DoorCloseTsKey] = closedAt;
            BackfillBusinessDoorTs(node, closedAt, findings);
        }

        var (next, routed) = Route(node, "success");
        findings.AddRange(routed);
        return Build(node, "success", next, "用户输入", sb.ToString(), findings);
    }

    private TraceEntry ExecQuery(FlowNode node)
    {
        var (parameters, display) = BuildParams(node);
        var item = node.SqlId is null ? null : ResolveSqlItem(node.SqlId);
        var findings = new List<Finding>();

        if (item is null)
        {
            findings.Add(Err(node, "缺少SQL", $"节点引用的 sql_id '{node.SqlId}' 在 sql-catalog 中不存在。"));
            var (nx2, f2) = Route(node, "error");
            findings.AddRange(f2);
            return Build(node, "error", nx2, node.SqlId ?? "", "(catalog 未找到)", findings, Severity.Error);
        }

        var db = string.Equals(node.DataSource, "mes", StringComparison.OrdinalIgnoreCase);
        var result = db ? _svc.Mes.Run(item, parameters) : _svc.AppDb.Run(item, parameters);

        var ctx = Context!;
        ctx.NodeResults[node.Id] = result.First ?? new(StringComparer.OrdinalIgnoreCase);
        ctx.RowCounts[node.Id] = result.Count;

        string key = result.HasError ? "error" : result.Count switch { 0 => "empty", 1 => "single", _ => "multiple" };
        if (result.HasError
            && string.Equals(node.Id, "queryWireQuotaCheck", StringComparison.OrdinalIgnoreCase)
            && IsQuotaReject(result.Error))
            key = "quota_reject";

        var (next, routeFindings) = Route(node, key);
        findings.AddRange(routeFindings);

        var resultText = result.HasError
            ? $"错误: {result.Error}"
            : $"{result.Count} 行" + (result.First is not null ? " | " + RowText(result.First) : "");

        var status = string.Equals(key, "quota_reject", StringComparison.OrdinalIgnoreCase)
            ? Severity.Warning
            : result.HasError ? Severity.Error : (routeFindings.Count > 0 ? Severity.Warning : Severity.Info);
        return Build(node, key, next, result.RenderedSql, resultText, findings, status, parameters, result.First);
    }

    private TraceEntry ExecDecision(FlowNode node)
    {
        var findings = new List<Finding>();
        var ctx = Context!;
        string sql = "";
        var prms = new Dictionary<string, object?>();

        if (node.SqlId is not null)
        {
            var item = ResolveSqlItem(node.SqlId);
            if (item is not null)
            {
                (prms, _) = BuildParams(node);
                var db = string.Equals(node.DataSource, "mes", StringComparison.OrdinalIgnoreCase);
                var r = db ? _svc.Mes.Run(item, prms) : _svc.AppDb.Run(item, prms);
                ctx.NodeResults[node.Id] = r.First ?? new(StringComparer.OrdinalIgnoreCase);
                sql = r.RenderedSql;
            }
        }

        var (passed, detail) = Evaluate(node.Check, node, findings);
        var key = passed ? "yes" : "no";
        if (!passed
            && string.Equals(node.Id, "checkSubmitWireReturnSuccess", StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.Check?.Kind, "submit_success", StringComparison.OrdinalIgnoreCase))
        {
            var submitResult = Context!.GetRef(node.Check!.Ref ?? "")?.ToString();
            if (IsReturnQtyReject(submitResult))
                key = "return_qty_reject";
        }

        var (next, routeFindings) = Route(node, key);
        findings.AddRange(routeFindings);
        return Build(node, key, next, sql, $"判定[{node.Check?.Kind}] => {(passed ? "是" : "否")} ({detail})", findings,
            findings.Count > 0 ? Severity.Warning : Severity.Info, prms);
    }

    private TraceEntry ExecWrite(FlowNode node)
    {
        var findings = new List<Finding>();

        if (string.Equals(node.Batch, "open_slots", StringComparison.OrdinalIgnoreCase))
        {
            var item0 = node.SqlId is null ? null : ResolveSqlItem(node.SqlId);
            var ids = _svc.Slot.OpenDoorIds();
            var (baseParams, _) = BuildParams(node);
            var errors = 0;
            foreach (var id in ids)
            {
                if (item0 is null) break;
                var p = new Dictionary<string, object?>(baseParams) { ["slot_id"] = id };
                var rr = _svc.AppDb.Run(item0, p);
                if (rr.HasError) errors++;
            }
            var (nx, rf) = Route(node, errors == 0 ? "success" : "error");
            findings.AddRange(rf);
            return Build(node, errors == 0 ? "success" : "error", nx,
                item0?.Sql ?? "", $"批量更新 {ids.Count} 个开门格口" + (errors > 0 ? $"，{errors} 个失败" : ""),
                findings, errors == 0 ? Severity.Info : Severity.Error);
        }

        var (parameters, display) = BuildParams(node);
        var item = node.SqlId is null ? null : ResolveSqlItem(node.SqlId);
        if (item is null)
        {
            findings.Add(Err(node, "缺少SQL", $"节点引用的 sql_id '{node.SqlId}' 在 sql-catalog 中不存在。"));
            var (nx2, f2) = Route(node, "error");
            findings.AddRange(f2);
            return Build(node, "error", nx2, node.SqlId ?? "", "(catalog 未找到)", findings, Severity.Error);
        }

        string? clearLotBeforePickup = null;
        if (string.Equals(node.SqlId, "app.slot.complete_issue_pickup", StringComparison.OrdinalIgnoreCase))
            clearLotBeforePickup = _svc.DiscoSync?.TryGetAvailableWireLot(parameters.GetValueOrDefault("slot_id"));

        var isIssueSubmit = string.Equals(node.Id, "submitWireIssue", StringComparison.OrdinalIgnoreCase);
        var issueSlotId = isIssueSubmit ? ToLong(Field("queryMatchedAvailableWire", "slot_id")) : 0;
        var issueOriginalState = isIssueSubmit
            ? Field("queryMatchedAvailableWire", "wire_state") ?? WireAvailableState
            : WireAvailableState;

        // 先占位再提交 MES：即使提交过程中程序崩溃/断电，格口也不会继续被当成可用。
        if (isIssueSubmit && issueSlotId > 0)
            TryMarkIssueSlot(node, issueSlotId, IssueInProgressWireState, findings);

        var isMes = string.Equals(node.DataSource, "mes", StringComparison.OrdinalIgnoreCase);
        var result = isMes ? _svc.Mes.Run(item, parameters) : _svc.AppDb.Run(item, parameters);

        if (isIssueSubmit && issueSlotId > 0)
        {
            if (!result.HasError)
                TryMarkIssueSlot(node, issueSlotId, IssuePendingWireState, findings);
            else if (IsNoStockRecordError(result.Error))
                TryMarkIssueSlot(node, issueSlotId, MesNoStockWireState, findings);
            else
                TryMarkIssueSlot(node, issueSlotId, issueOriginalState, findings);
        }

        Context!.NodeResults[node.Id] = result.First ?? new(StringComparer.OrdinalIgnoreCase);

        var key = result.HasError ? "error" : "success";
        if (string.Equals(node.Id, "queryWireQuotaCheck", StringComparison.OrdinalIgnoreCase))
        {
            if (result.HasError && IsQuotaReject(result.Error))
                key = "quota_reject";
            else if (!result.HasError)
                key = result.Count == 0 ? "empty" : "single";
        }
        else if (string.Equals(node.Id, "submitWireReturn", StringComparison.OrdinalIgnoreCase)
                 && result.HasError && IsReturnQtyReject(result.Error))
        {
            key = "return_qty_reject";
        }

        if (key == "success" && _svc.DiscoSync is { } discoSync)
        {
            if (string.Equals(node.SqlId, "app.slot.bind_wire", StringComparison.OrdinalIgnoreCase))
            {
                var lot = parameters.GetValueOrDefault("wire_lot_no")?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(lot))
                {
                    var sync = discoSync.TryMarkInCabinet(lot, WireMesDiscoFailureKind.Inline);
                    if (!sync.Success)
                    {
                        key = "error";
                        findings.Add(Err(node, WireMesDiscoMessages.FindingCategory, sync.UserMessage));
                    }
                }
            }
            else if (string.Equals(node.SqlId, "app.slot.complete_issue_pickup", StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrEmpty(clearLotBeforePickup))
            {
                var sync = discoSync.TryClearFromCabinet(clearLotBeforePickup, WireMesDiscoFailureKind.Inline);
                if (!sync.Success)
                {
                    key = "error";
                    findings.Add(Err(node, WireMesDiscoMessages.FindingCategory, sync.UserMessage));
                }
            }
        }

        // 存取记录（0.2）挂接点（§3 覆盖矩阵第 1–4、9 项：MH 存料 / 操作员归还 / 领用 / 取出）。
        // 放在 DISCOEQPNO 同步之后，这样 MES 同步失败也会以 result=失败 被记下来；
        // 失败不单独成动作类型，仍是同一动作的一条记录。
        LogFlowAction(node, parameters, key, findings, result);
        var (next, routeFindings) = Route(node, key);
        findings.AddRange(routeFindings);

        var failed = key is "error" or "empty";
        var resultText = result.HasError
            ? $"错误: {result.Error}"
            : failed
                ? findings.LastOrDefault(f => string.Equals(f.Category, WireMesDiscoMessages.FindingCategory, StringComparison.Ordinal))?.Message
                  ?? WireMesDiscoMessages.InlineFailUser
                : (result.First is not null ? RowText(result.First) : $"影响 {result.RowsAffected} 行");

        var status = key is "quota_reject" or "return_qty_reject"
            ? Severity.Warning
            : failed ? Severity.Error : Severity.Info;

        return Build(node, key, next, result.RenderedSql, resultText, findings,
            status, parameters, result.First);
    }

    private TraceEntry ExecSlotOpen(FlowNode node)
    {
        var findings = new List<Finding>();
        var (parameters, _) = BuildParams(node);
        long slotId = ToLong(parameters.GetValueOrDefault("slot_id"));

        string sql = "";
        var ok = slotId > 0;
        if (ok)
        {
            ok = _svc.Slot.OpenDoorAsync(slotId).GetAwaiter().GetResult();
            if (!ok)
                findings.Add(Err(node, "开门", $"格口 {slotId} 开锁失败（已禁用、硬件无响应或格口不存在）。"));
            else if (node.SqlId is not null && ResolveSqlItem(node.SqlId) is { } item)
            {
                var r = _svc.AppDb.Run(item, new Dictionary<string, object?> { ["slot_id"] = slotId });
                sql = r.RenderedSql;
                if (r.HasError) { ok = false; findings.Add(Err(node, "开门SQL", r.Error!)); }
            }

            if (ok)
                Context!.NodeResults[node.Id] = new(StringComparer.OrdinalIgnoreCase) { ["opened_slot_id"] = slotId };
            if (ok)
            {
                var openedAt = NowUtcText();
                Context!.Runtime[DoorOpenTsKey] = openedAt;
            }
        }
        else
        {
            findings.Add(Err(node, "开门", "未解析出有效的格口 id。"));
        }

        var key = ok ? "success" : "error";
        var (next, rf) = Route(node, key);
        findings.AddRange(rf);
        return Build(node, key, next, sql, ok ? $"已打开格口 {slotId}" : "开门失败", findings,
            ok ? Severity.Info : Severity.Error, parameters);
    }

    private TraceEntry ExecSlotOpenBatch(FlowNode node)
    {
        var findings = new List<Finding>();
        var (parameters, _) = BuildParams(node);
        var listItem = node.SqlId is null ? null : ResolveSqlItem(node.SqlId);
        var openItem = node.OpenSqlId is null ? null : ResolveSqlItem(node.OpenSqlId);

        if (listItem is null)
        {
            findings.Add(Err(node, "缺少SQL", $"批量列举 sql_id '{node.SqlId}' 未找到。"));
            var (nx, f) = Route(node, "error");
            findings.AddRange(f);
            return Build(node, "error", nx, "", "(catalog 未找到)", findings, Severity.Error);
        }

        var list = _svc.AppDb.Run(listItem, parameters);
        var ids = list.Rows.Select(r => ToLong(r.GetValueOrDefault("slot_id"))).Where(x => x > 0).ToList();
        var opened = 0;
        var batchOk = true;
        foreach (var id in ids)
        {
            if (!_svc.Slot.OpenDoorAsync(id).GetAwaiter().GetResult())
            {
                batchOk = false;
                findings.Add(Err(node, "批量开门", $"格口 {id} 开锁失败，中止批量。"));
                break;
            }

            opened++;
            if (openItem is not null)
                _svc.AppDb.Run(openItem, new Dictionary<string, object?> { ["slot_id"] = id });
        }

        if (ids.Count == 0)
            findings.Add(Info(node, "空集合", "过滤条件未匹配到任何格口，直接进入关门判定。"));

        var key = batchOk ? "success" : "error";
        var (next, rf) = Route(node, key);
        findings.AddRange(rf);
        return Build(node, key, next, list.RenderedSql,
            batchOk ? $"打开 {opened} 个格口: [{string.Join(", ", ids.Take(opened))}]" : $"批量开门中止（已成功 {opened}/{ids.Count}）",
            findings, batchOk ? Severity.Info : Severity.Error, parameters);
    }

    private TraceEntry ExecSlotClose(FlowNode node)
    {
        var findings = new List<Finding>();
        var closed = _svc.Slot.CloseNextOpenDoor();
        if (closed is not null)
            Context!.Runtime["last_closed_slot_id"] = closed.Value.ToString(CultureInfo.InvariantCulture);

        // 关门时刻不能只在 CloseNextOpenDoor() 返回格口时才记。现场是人工/硬件**先把门关掉**，
        // 流程推进到本节点时 app_slot.door_state 已是 closed，该调用返回 null——本流程既然开过门
        // （Runtime 里有 door_open_ts），这次关门就是它的关门时刻，漏记会让合并后的业务记录缺
        // door_close_ts（MH 存料现场实测 10/10 条只有开门时刻）。
        if (closed is not null || RuntimeValue(DoorOpenTsKey) is { Length: > 0 })
            Context!.Runtime[DoorCloseTsKey] = NowUtcText();

        var (next, rf) = Route(node, "success");
        findings.AddRange(rf);
        var text = closed is not null ? $"关闭格口 {closed}" : "当前无打开的门（空操作）";
        return Build(node, "success", next, "", text, findings);
    }

    private TraceEntry ExecTerminal(FlowNode node)
    {
        var kind = node.TerminalKind ?? "end";
        var sev = kind == "fail" ? Severity.Warning : Severity.Info;
        return Build(node, kind, null, "", $"流程结束 ({kind})", new(), sev);
    }

    private TraceEntry ExecUnknown(FlowNode node)
    {
        var findings = new List<Finding> { Err(node, "未知节点类型", $"无法识别的节点类型 '{node.RawType}'。") };
        return Build(node, "error", null, "", "未知节点类型", findings, Severity.Error);
    }

    // ---------- 存取记录（0.2） ----------

    /// <summary>MH 存入可用焊丝的记录动作类型。</summary>
    public const string MhLoadActionType = "mh_load_available_wire";

    /// <summary>
    /// MH「存入可用焊丝」完成后写一条存取记录（0.2 需求范围 §3.1 第 1 项、§3 矩阵第 9 项）。
    /// 成功记 result=成功，失败记 result=失败 且 error_info 写明原因——失败不单独成动作类型。
    ///
    /// **契约**：写入失败只追加一条告警，**绝不改变本节点的 outcome / 路由**，
    /// 也不与业务写入共用事务（0.2 需求范围 §1.4 第 1 条，验收 §9 C1）。
    /// 上方 DISCOEQPNO 同步挂接位于同一位置，但它在失败时会把 key 改成 "error"——
    /// 记录写入不得沿用该写法。
    /// </summary>
    private void LogMhLoadAvailableWire(
        FlowNode node, IDictionary<string, object?> parameters, string key, List<Finding> findings, DbResult result)
    {
        if (_svc.OperationLog is not { } sink) return;

        var success = string.Equals(key, "success", StringComparison.Ordinal);

        // MES 同步挂接失败时会把原因写成同节点上的「MES 同步」finding，据此区分
        // 「MES 调用失败」与「应用库写入失败」，前者才计入 mes_result。
        var syncError = findings
            .LastOrDefault(f => string.Equals(f.Category, WireMesDiscoMessages.FindingCategory, StringComparison.Ordinal))
            ?.Message;

        // 格位号复用 checkAvailableSlot 已有的查询结果（app.slot.find_available 已返回 slot_no），
        // 不新增查询（验收 §9 C6：不得为记录功能新增 MES / 库调用）。
        var slot = Context!.NodeResults.TryGetValue("checkAvailableSlot", out var s) ? s : null;

        WriteEntry(sink, node, findings, "存料", new OperationLogEntry
        {
            ActionType = MhLoadActionType,
            FlowId = Flow!.FlowId,
            NodeId = node.Id,
            // 操作人由界面在开跑前经 Runtime 传入（MH 流程无操作人输入节点）；
            // 没传就至少留下角色，不留空动作类型
            OperatorRole = RuntimeValue(OperatorRoleKey) ?? "MH",
            OperatorId = RuntimeValue(OperatorIdKey),
            OperatorName = RuntimeValue(OperatorNameKey),
            OperatorTeam = RuntimeValue(OperatorTeamKey),
            SlotNo = slot?.GetValueOrDefault("slot_no")?.ToString(),
            LotNo = ParamText(parameters, "wire_lot_no"),
            WireSpec = ParamText(parameters, "wire_spec"),
            Qty = ParamNumber(parameters, "qty"),
            Result = success ? "成功" : "失败",
            Message = success ? "存料完成" : "存料失败",
            ErrorInfo = success ? null : result.Error ?? syncError ?? "存料失败",
            MesResult = success ? "ok" : syncError,
            // 能走到 updateDB 说明格口已经开过并关过，两种结果下都成立
            LockResult = "ok",
            // 门时刻并入业务记录（口径优化 §2）：开门/关门不再单独成条，两个时刻挂在这条上。
            // updateDB 在 closeAvailableSlotDoor 之后执行，Runtime 里两个时刻都已就绪。
            DoorOpenTs = RuntimeValue(DoorOpenTsKey),
            DoorCloseTs = RuntimeValue(DoorCloseTsKey),
        });
    }

    // ---------- 存取记录：操作员流程（0.2 需求范围 §3.1 第 2/3/4 项） ----------

    public const string OpReturnActionType = "op_return_wire";
    public const string OpIssueActionType = "op_issue_available_wire";
    public const string OpTakeActionType = "op_take_wire";

    /// <summary>
    /// OP 领用期间格口 wire_state 的本地占用/隔离标记（不是 MES 返回的焊丝状态）。
    /// 这些值会被 app.wire_inventory.find_matched_available 排除，防止「MES 已发料、
    /// 操作员没关门」的格口继续发给下一个人。
    /// </summary>
    public const string IssueInProgressWireState = "领用中";
    public const string IssuePendingWireState = "领用待取";
    public const string MesNoStockWireState = "MES无库存";
    private const string WireAvailableState = "可用";
    private const string MarkIssueStateSqlId = "app.slot.mark_issue_state";

    private const string MhBindWireSqlId = "app.slot.bind_wire";

    /// <summary>取出格口库存（关门后）——取出记录的落点，§3.1 第 4 项要求单独成条。</summary>
    private const string TakePickupSqlId = "app.slot.complete_issue_pickup";

    private const string DoorOpenTsKey = "door_open_ts";
    private const string DoorCloseTsKey = "door_close_ts";
    private const string DoorClosedOutput = "closed";


    /// <summary>
    /// 操作人信息在运行期上下文（FlowRunContext.Runtime）里的键。
    ///
    /// MH 存料流程**没有**操作人输入节点，而 §1.3 不允许改 0.1 流程定义，
    /// 所以由界面在开跑前把这些值写进 Runtime，引擎在挂接记录时读取。
    /// 读不到就只记 operator_role，不阻断业务（§1.4）。
    /// </summary>
    public const string OperatorIdKey = "operator_id";
    public const string OperatorNameKey = "operator_name";
    public const string OperatorTeamKey = "operator_team";
    public const string OperatorRoleKey = "operator_role";

    /// <summary>
    /// 提交类动作按**节点 id** 识别：归还与领用两个节点共用 mes.mat_trans.submit_return，
    /// 按 sql_id 分不开。
    /// </summary>
    private static readonly Dictionary<string, string> OpActionByNodeId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["submitWireReturn"] = OpReturnActionType,
        ["submitWireIssue"] = OpIssueActionType,
    };

    /// <summary>归还 / 领用业务记录的定位键（记录写入时用的 ts），供关门后回填门时刻。</summary>
    private const string OpReturnRecordTsKey = "op_return_record_ts";
    private const string OpIssueRecordTsKey = "op_issue_record_ts";

    /// <summary>
    /// 同上，但存的是那条记录的 rowid。回填按 rowid 定位：ts 只精确到秒，
    /// 同一种动作在同一秒出现两条时按 ts 回填会把两条都填成同一个关门时刻。
    /// </summary>
    private const string OpReturnRecordIdKey = "op_return_record_id";
    private const string OpIssueRecordIdKey = "op_issue_record_id";

    /// <summary>关门确认节点 → 该半程业务记录的定位键。见 BackfillBusinessDoorTs。</summary>
    private static readonly Dictionary<string, string> OpBackfillKeyByNodeId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["closeReturnSlotDoor"] = OpReturnRecordTsKey,
        ["closeIssueSlotDoor"] = OpIssueRecordTsKey,
    };


    /// <summary>本节点是否对应一个需要记录的存取动作；是则写一条（成功与失败都写）。</summary>
    private void LogFlowAction(
        FlowNode node, IDictionary<string, object?> parameters, string key, List<Finding> findings, DbResult result)
    {
        if (_svc.OperationLog is null) return;

        if (string.Equals(node.SqlId, MhBindWireSqlId, StringComparison.OrdinalIgnoreCase))
            LogMhLoadAvailableWire(node, parameters, key, findings, result);
        else if (OpActionByNodeId.TryGetValue(node.Id, out var opAction))
            LogOperatorSubmit(node, opAction, key, findings, result);
        else if (string.Equals(node.SqlId, TakePickupSqlId, StringComparison.OrdinalIgnoreCase))
            LogOperatorTake(node, key, findings, result);
    }

    /// <summary>
    /// 归还（§3.1 第 2 项）/ 领用（第 3 项）提交成功后的记录；失败同样记一条。
    ///
    /// 口径优化（2026-10-09）后：领用这条同时就是「取出」那条（两者合并，见 <see cref="LogOperatorTake"/>），
    /// 且归还 / 领用都是在开门**之前**提交的，此刻还没有关门时刻——记录先落库（门时刻为 NULL），
    /// 关门确认时由 <see cref="BackfillBusinessDoorTs"/> 把门时刻补上。
    /// 定位键（本次写入的 ts）存进 Runtime 供回填使用。
    /// </summary>
    private void LogOperatorSubmit(FlowNode node, string actionType, string key, List<Finding> findings, DbResult result)
    {
        if (_svc.OperationLog is not { } sink) return;

        var success = string.Equals(key, "success", StringComparison.Ordinal);
        var isReturn = string.Equals(actionType, OpReturnActionType, StringComparison.Ordinal);

        // ts 由这里生成并原样写入：关门后要按它回填门时刻，必须与落库值完全一致。
        var recordTs = NowUtcText();

        var write = WriteEntry(sink, node, findings, "操作", new OperationLogEntry
        {
            Ts = recordTs,
            ActionType = actionType,
            FlowId = Flow!.FlowId,
            NodeId = node.Id,
            OperatorRole = "OP",
            OperatorId = Field("inputOperatorWorkInfo", "operator_id"),
            OperatorTeam = Field("inputOperatorWorkInfo", "work_group"),
            OperatorName = Field("queryOPById", "operator_name"),
            EqpNo = Field("inputEqpNo", "eqp_no"),
            SlotNo = isReturn
                ? Field("findReturnSlot", "return_slot_no")
                : Field("queryMatchedAvailableWire", "slot_no"),
            LotNo = isReturn
                ? Field("inputReturnedWireLotNo", "returned_wire_lot_no")
                : Field("queryMatchedAvailableWire", "matched_available_wire_lot_no"),
            WireSpec = isReturn
                ? Field("queryWireSpecByLotNo", "spec")
                : Field("queryWireByLotNo", "spec"),
            ProductLotNo = isReturn
                ? Field("queryLastProductLotNo", "lot")
                : Field("inputIssueProductLotNo", "issue_product_lot_no"),
            Qty = isReturn
                ? NumField("queryWireReturnedWeightBySpec", "return_weight")
                : NumField("queryWireByLotNo", "qty"),
            RemainQty = isReturn ? NumField("inputRemainingQty", "remaining_qty") : null,
            Result = success ? "成功" : "失败",
            Message = success ? (isReturn ? "归还提交完成" : "领用提交完成") : "提交失败",
            ErrorInfo = success ? null : result.Error,
            MesResult = success ? "ok" : result.Error,
        });

        // 只有成功写入才可能被回填（失败时没落库，回填会命中 0 行）。
        if (success)
        {
            Context!.Runtime[isReturn ? OpReturnRecordTsKey : OpIssueRecordTsKey] = recordTs;
            if (write.RowId is { } id)
                Context.Runtime[isReturn ? OpReturnRecordIdKey : OpIssueRecordIdKey] =
                    id.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 取出焊丝的库存更新（节点 <c>updateIssueSlotAfterUnload</c>）。
    ///
    /// 口径优化（2026-10-09）：领用与取出**合并成一条**——成功时不再单独记，那条
    /// <c>op_issue_available_wire</c> 已在提交时写好、门时刻也在关门确认时补上了。
    /// 只有这次库存更新**失败**才留一条：这是异常，必须看得见失败原因
    /// （验收 §9 要求失败记录带 error_info）。
    /// </summary>
    private void LogOperatorTake(FlowNode node, string key, List<Finding> findings, DbResult result)
    {
        if (_svc.OperationLog is not { } sink) return;
        if (string.Equals(key, "success", StringComparison.Ordinal)) return;

        WriteEntry(sink, node, findings, "取出", new OperationLogEntry
        {
            ActionType = OpTakeActionType,
            FlowId = Flow!.FlowId,
            NodeId = node.Id,
            OperatorRole = "OP",
            OperatorId = Field("inputOperatorWorkInfo", "operator_id"),
            OperatorTeam = Field("inputOperatorWorkInfo", "work_group"),
            OperatorName = Field("queryOPById", "operator_name"),
            EqpNo = Field("inputEqpNo", "eqp_no"),
            SlotNo = Field("queryMatchedAvailableWire", "slot_no"),
            LotNo = Field("queryMatchedAvailableWire", "matched_available_wire_lot_no"),
            WireSpec = Field("queryMatchedAvailableWire", "wire_spec"),
            ProductLotNo = Field("inputIssueProductLotNo", "issue_product_lot_no"),
            DoorOpenTs = RuntimeValue(DoorOpenTsKey),
            DoorCloseTs = RuntimeValue(DoorCloseTsKey),
            Result = "失败",
            Message = "取出后库存更新失败",
            ErrorInfo = result.Error,
        });
    }

    /// <summary>
    /// MES 明确报「找不到物料…库存记录」：本地账上还有这盘料，MES 已经没有库存记录。
    /// 这不是可重试的业务拒绝，必须把格口隔离，否则同一盘料会被每个操作员反复选中。
    /// </summary>
    private static bool IsNoStockRecordError(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && error.Contains("找不到物料", StringComparison.Ordinal)
        && error.Contains("库存记录", StringComparison.Ordinal);

    /// <summary>
    /// 标记格口的本地领用占用/隔离状态。失败只告警，不改流程 outcome（与存取记录同一契约）。
    /// </summary>
    private void TryMarkIssueSlot(FlowNode node, long slotId, string wireState, List<Finding> findings)
    {
        var item = _svc.Catalog.Find(MarkIssueStateSqlId);
        if (item is null)
        {
            findings.Add(Warn(node, "领用隔离", $"缺少 SQL 条目 {MarkIssueStateSqlId}，格口 {slotId} 未隔离。"));
            return;
        }

        var result = _svc.AppDb.Run(item, new Dictionary<string, object?>
        {
            ["slot_id"] = slotId,
            ["wire_state"] = wireState
        });
        if (result.HasError)
            findings.Add(Warn(node, "领用隔离", $"格口 {slotId} 标记「{wireState}」失败：{result.Error}"));
    }

    /// <summary>
    /// 关门确认后，把这一对门时刻补进该半程已经写好的业务记录（口径优化 §5 路线②）。
    ///
    /// 归还 / 领用是在开门之前提交并写记录的，写记录时关门时刻还不存在，只能事后补。
    /// 回填只补 NULL、绝不改写已有值（库层触发器兜底）；**绝不改变业务 outcome 或路由**（§1.4）。
    ///
    /// 失败要**在界面上看得见**（不只是 Trace）：门时刻为空会被当成"这次操作没完成"，
    /// 看着像功能坏了。记录还在待补偿队列时由 sink 直接把门时刻补进队列，不算失败。
    /// </summary>
    private void BackfillBusinessDoorTs(FlowNode node, string closedAt, List<Finding> findings)
    {
        if (_svc.OperationLog is not { } sink) return;
        if (!OpBackfillKeyByNodeId.TryGetValue(node.Id, out var key)) return;
        if (RuntimeValue(key) is not { Length: > 0 } recordTs) return;

        var isReturn = string.Equals(key, OpReturnRecordTsKey, StringComparison.Ordinal);
        var actionType = isReturn ? OpReturnActionType : OpIssueActionType;

        var idText = RuntimeValue(isReturn ? OpReturnRecordIdKey : OpIssueRecordIdKey);
        var rowId = long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (long?)null;

        var result = sink.BackfillDoorTs(rowId, actionType, recordTs, RuntimeValue(DoorOpenTsKey), closedAt);
        if (!result.Success)
        {
            System.Diagnostics.Trace.TraceWarning($"补齐 {actionType} 门时刻失败：{result.Error}");
            findings.Add(Warn(node, "存取记录", $"补齐门时刻失败（门时刻会留空，请在记录页核对）：{result.Error}"));
        }
    }

    /// <summary>
    /// 写一条记录。**契约**：写入失败只追加一条告警，绝不改变本节点的 outcome / 路由
    /// （0.2 需求范围 §1.4 第 1 条，验收 §9 C1）。
    /// </summary>
    private OperationLogWriteResult WriteEntry(IOperationLogSink sink, FlowNode node, List<Finding> findings, string what, OperationLogEntry entry)
    {
        var write = sink.TryWrite(entry);
        if (!write.Success)
            findings.Add(Warn(node, "存取记录", $"存取记录写入失败（不影响本次{what}{(write.Queued ? "，已转待补偿" : "")}）：{write.Error}"));
        return write;
    }

    /// <summary>取某节点结果里的字段（节点未执行或字段缺失时为 null）。</summary>
    private string? Field(string nodeId, string field) =>
        Context!.NodeResults.TryGetValue(nodeId, out var row) ? row.GetValueOrDefault(field)?.ToString() : null;

    private double? NumField(string nodeId, string field) =>
        double.TryParse(Field(nodeId, field), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private string? RuntimeValue(string key) =>
        Context!.Runtime.TryGetValue(key, out var value) ? value : null;

    private static string NowUtcText() =>
        OperationLogTime.UtcNowText();

    private static string? ParamText(IDictionary<string, object?> parameters, string name) =>
        parameters.TryGetValue(name, out var v) ? v?.ToString() : null;

    private static double? ParamNumber(IDictionary<string, object?> parameters, string name) =>
        parameters.TryGetValue(name, out var v)
        && double.TryParse(v?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    // ---------- 判定 ----------

    private (bool passed, string detail) Evaluate(DecisionCheck? check, FlowNode node, List<Finding> findings)
    {
        if (check is null)
        {
            findings.Add(Warn(node, "判定缺失", "decision 节点缺少 check 定义，默认走 yes。"));
            return (true, "无 check");
        }

        switch (check.Kind)
        {
            case "exists":
            {
                var count = check.Node is not null && Context!.RowCounts.TryGetValue(check.Node, out var c) ? c : 0;
                return (count > 0, $"{check.Node} 行数={count}");
            }
            case "field_not_empty":
            {
                var v = Context!.GetRef(check.Ref ?? "");
                var ok = !IsEmpty(v);
                return (ok, $"{check.Ref}={Display(v)}");
            }
            case "field_not_empty_and_not_equals":
            {
                var v = Context!.GetRef(check.Ref ?? "");
                if (IsEmpty(v))
                    return (false, $"{check.Ref}=(空)");
                var s = v!.ToString() ?? "";
                var reject = string.Equals(s, check.CompareValue ?? "", StringComparison.Ordinal);
                return (!reject, $"{check.Ref}={Display(v)} != {check.CompareValue}");
            }
            case "all_fields_not_empty":
            {
                var ok = check.Refs.All(r => !IsEmpty(Context!.GetRef(r)));
                var detail = string.Join(", ", check.Refs.Select(r => $"{r}={Display(Context!.GetRef(r))}"));
                return (ok, detail);
            }
            case "numeric_le":
            {
                var v = Context!.GetRef(check.Ref ?? "");
                var num = ToDouble(v);
                return (num <= check.Value, $"{check.Ref}={Display(v)} <= {check.Value}");
            }
            case "submit_success":
            {
                var v = Context!.GetRef(check.Ref ?? "")?.ToString();
                var ok = IsSubmitSuccess(v);
                return (ok, $"{check.Ref}='{v}'");
            }
            case "all_doors_closed":
            {
                object? v = Context!.NodeResults.TryGetValue(node.Id, out var row) && row.TryGetValue("all_closed", out var av) ? av : null;
                bool ok = v is not null ? ToLong(v) == 1 : _svc.Slot.AreAllDoorsClosed();
                return (ok, $"all_closed={Display(v)}");
            }
            default:
                findings.Add(Warn(node, "未知判定", $"未知 check.kind '{check.Kind}'，默认走 yes。"));
                return (true, check.Kind);
        }
    }

    private static bool IsSubmitSuccess(string? v) => MatTransResult.IsSuccess(v);

    /// <summary>
    /// MES 配额数量错误（可重输）：精确文案，或同时含「剩余产量」与「待完工」的等价表述。
    /// </summary>
    public static bool IsQuotaReject(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return false;
        if (error.Contains("剩余产量不能大于待完工产量", StringComparison.OrdinalIgnoreCase))
            return true;
        return error.Contains("剩余产量", StringComparison.OrdinalIgnoreCase)
               && error.Contains("待完工", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsReturnQtyReject(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Contains("归还数量", StringComparison.OrdinalIgnoreCase)
        && text.Contains("不能大于产品待完工数量", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按当前 MES 模式解析 sql_id（逻辑引用 → mock 或正式条目）。
    ///
    /// 此前只做了 mock→formal（Oracle 模式）这一向，于是引用正式 <c>mes.</c> id 的节点
    /// 在 mock 模式下会直接执行 Oracle SQL：操作员流程的 9 个 MES 节点全部引用正式 id，
    /// 其中「最近产品批号」用到 NVL，在 SQLite 下报 <c>no such function: NVL</c>，整条流程走不下去。
    /// 两个方向的解析现已收敛到 <see cref="SqlCatalog.FindForMode"/>。
    /// </summary>
    private SqlCatalogItem? ResolveSqlItem(string sqlId) =>
        _svc.Catalog.FindForMode(sqlId, _svc.Mes.Mode);

    // ---------- 参数与取值 ----------

    private (Dictionary<string, object?>, string) BuildParams(FlowNode node)
    {
        var item = node.SqlId is null ? null : ResolveSqlItem(node.SqlId);
        var isMatTrans = item is not null && MatTransResult.IsMatTransItem(item);

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var im in node.Inputs)
        {
            var val = Resolve(im.Source);
            if (string.Equals(node.Id, "queryWireQuotaCheck", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(im.Name, "V_thzl", StringComparison.OrdinalIgnoreCase)
                    && val is not null and not string)
                    val = Convert.ToString(val, CultureInfo.InvariantCulture);
                else if (string.Equals(im.Name, "V_sycl", StringComparison.OrdinalIgnoreCase)
                         && val is not null
                         && decimal.TryParse(val.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var qty))
                    val = qty;
            }
            else if (isMatTrans
                     && string.Equals(im.Name, "V_ShelfLife", StringComparison.OrdinalIgnoreCase))
            {
                val = MesDateTimeFormat.ToOracleString(val);
            }
            dict[im.Name] = val;
        }

        var isMes = string.Equals(node.DataSource, "mes", StringComparison.OrdinalIgnoreCase);
        var isOracle = isMes && _svc.Mes.Mode == MesMode.Oracle;
        var display = SqlBindTrace.FormatCompact(item, isOracle, dict);
        return (dict, display);
    }

    private object? Resolve(ValueSource src)
    {
        switch (src.SourceKind)
        {
            case ValueSource.Kind.Const:
                return src.Literal;
            case ValueSource.Kind.System:
                return src.Field switch
                {
                    "configured_agv_no" => _svc.SystemAgvNo,
                    "configured_mes_writer" => _svc.SystemMesWriter,
                    _ => ""
                };
            case ValueSource.Kind.Context:
                return Context!.Runtime.TryGetValue(src.Field ?? "", out var cv) ? cv : null;
            case ValueSource.Kind.Node:
                if (src.Field is null) return null;
                return Context!.NodeResults.TryGetValue(src.Node ?? "", out var row) && row.TryGetValue(src.Field, out var v) ? v : null;
            default:
                return null;
        }
    }

    // ---------- 路由 ----------

    private (string? next, List<Finding> findings) Route(FlowNode node, string key)
    {
        var findings = new List<Finding>();
        if (node.Routing.TryGetValue(key, out var next))
            return (next, findings);

        // 终止节点没有路由是正常的
        if (node.Type == NodeType.Terminal) return (null, findings);

        // 执行失败：不回落到 single/yes，由界面统一提示并结束本流程（见 flow-sql-map README outcome_routing）
        if (string.Equals(key, "error", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Err(node, "执行失败", "SQL/连接/catalog 异常，未配置流程图 error 出边；流程终止。"));
            return (null, findings);
        }

        findings.Add(Warn(node, "未路由", $"结果 '{key}' 未在 outcome_routing 中定义。"));

        // 回退：优先 single/success，否则取第一个
        foreach (var fk in new[] { "single", "success", "yes" })
            if (node.Routing.TryGetValue(fk, out var fb)) return (fb, findings);
        var first = node.Routing.Values.FirstOrDefault();
        return (first, findings);
    }

    // ---------- 工具 ----------

    private TraceEntry Build(FlowNode node, string outcome, string? next, string sql, string result,
        List<Finding> findings, Severity status = Severity.Info, IDictionary<string, object?>? prms = null,
        Dictionary<string, object?>? outRow = null)
    {
        var catalogItem = node.SqlId is not null ? ResolveSqlItem(node.SqlId) : null;
        var isMes = string.Equals(node.DataSource, "mes", StringComparison.OrdinalIgnoreCase);
        var isOracle = isMes && _svc.Mes.Mode == MesMode.Oracle;
        var (compact, detail) = prms is null || prms.Count == 0
            ? ("", "")
            : SqlBindTrace.Format(catalogItem, isOracle, prms, outRow);

        return new TraceEntry
        {
            Step = _step,
            NodeId = node.Id,
            NodeText = node.Text,
            NodeType = node.RawType,
            DataSource = node.DataSource ?? "",
            SqlId = node.SqlId,
            RenderedSql = sql,
            Parameters = compact,
            ParametersDetail = detail,
            Result = result,
            Outcome = outcome,
            NextNode = next,
            Status = status,
            Findings = findings
        };
    }

    private Finding Err(FlowNode n, string cat, string msg) => new() { Severity = Severity.Error, FlowId = Flow!.FlowId, NodeId = n.Id, Category = cat, Message = msg };
    private Finding Warn(FlowNode n, string cat, string msg) => new() { Severity = Severity.Warning, FlowId = Flow!.FlowId, NodeId = n.Id, Category = cat, Message = msg };
    private Finding Info(FlowNode n, string cat, string msg) => new() { Severity = Severity.Info, FlowId = Flow!.FlowId, NodeId = n.Id, Category = cat, Message = msg };

    private static bool IsEmpty(object? v) => v is null || string.IsNullOrWhiteSpace(v.ToString());
    private static string Display(object? v)
    {
        if (v is null or DBNull) return "NULL";
        if (v is string s && s.Length == 0) return "\"\"";
        return v.ToString() ?? "\"\"";
    }
    private static string RowText(Dictionary<string, object?> row) => string.Join(", ", row.Select(kv => $"{kv.Key}={Display(kv.Value)}"));

    private static long ToLong(object? v)
    {
        if (v is null) return 0;
        if (v is long l) return l;
        if (v is int i) return i;
        return long.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0;
    }

    private static double ToDouble(object? v)
    {
        if (v is null) return double.NaN;
        if (v is double d) return d;
        return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : double.NaN;
    }
}
