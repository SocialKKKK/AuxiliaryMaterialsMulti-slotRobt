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

        // 关门确认：§3.3 把 door_close_ts 定义为「关门确认时刻」。操作员流程的关门是用户确认
        // （closeReturnSlotDoor / closeIssueSlotDoor 是 user_input，不是 slot_close 节点），
        // 所以按节点声明的 closed 输出识别，而不是靠节点名。
        if (node.Outputs.Any(o => string.Equals(o.Name, DoorClosedOutput, StringComparison.OrdinalIgnoreCase)))
            ctx.Runtime[DoorCloseTsKey] = NowIso();

        var (next, findings) = Route(node, "success");
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

        var isMes = string.Equals(node.DataSource, "mes", StringComparison.OrdinalIgnoreCase);
        var result = isMes ? _svc.Mes.Run(item, parameters) : _svc.AppDb.Run(item, parameters);

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
                Context!.Runtime[DoorOpenTsKey] = NowIso();
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
            OperatorRole = "MH",
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
        });
    }

    // ---------- 存取记录：操作员流程（0.2 需求范围 §3.1 第 2/3/4 项） ----------

    public const string OpReturnActionType = "op_return_wire";
    public const string OpIssueActionType = "op_issue_available_wire";
    public const string OpTakeActionType = "op_take_wire";

    private const string MhBindWireSqlId = "app.slot.bind_wire";

    /// <summary>取出格口库存（关门后）——取出记录的落点，§3.1 第 4 项要求单独成条。</summary>
    private const string TakePickupSqlId = "app.slot.complete_issue_pickup";

    private const string DoorOpenTsKey = "door_open_ts";
    private const string DoorCloseTsKey = "door_close_ts";
    private const string DoorClosedOutput = "closed";

    /// <summary>
    /// 提交类动作按**节点 id** 识别：归还与领用两个节点共用 mes.mat_trans.submit_return，
    /// 按 sql_id 分不开。
    /// </summary>
    private static readonly Dictionary<string, string> OpActionByNodeId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["submitWireReturn"] = OpReturnActionType,
        ["submitWireIssue"] = OpIssueActionType,
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

    /// <summary>归还（§3.1 第 2 项）/ 领用（第 3 项）提交成功后的记录；失败同样记一条。</summary>
    private void LogOperatorSubmit(FlowNode node, string actionType, string key, List<Finding> findings, DbResult result)
    {
        if (_svc.OperationLog is not { } sink) return;

        var success = string.Equals(key, "success", StringComparison.Ordinal);
        var isReturn = string.Equals(actionType, OpReturnActionType, StringComparison.Ordinal);

        WriteEntry(sink, node, findings, "操作", new OperationLogEntry
        {
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
    }

    /// <summary>
    /// 取出焊丝（§3.1 第 4 项）：关门后单独一条，含该格的开门 / 关门确认时刻。
    /// 关门在操作员流程里是用户确认，故 door_close_ts 取确认时刻（§3.3 的定义）。
    /// </summary>
    private void LogOperatorTake(FlowNode node, string key, List<Finding> findings, DbResult result)
    {
        if (_svc.OperationLog is not { } sink) return;

        var success = string.Equals(key, "success", StringComparison.Ordinal);

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
            Result = success ? "成功" : "失败",
            Message = success ? "取出完成" : "取出失败",
            ErrorInfo = success ? null : result.Error,
        });
    }

    /// <summary>
    /// 写一条记录。**契约**：写入失败只追加一条告警，绝不改变本节点的 outcome / 路由
    /// （0.2 需求范围 §1.4 第 1 条，验收 §9 C1）。
    /// </summary>
    private void WriteEntry(IOperationLogSink sink, FlowNode node, List<Finding> findings, string what, OperationLogEntry entry)
    {
        var write = sink.TryWrite(entry);
        if (!write.Success)
            findings.Add(Warn(node, "存取记录", $"存取记录写入失败（不影响本次{what}，已转待补偿）：{write.Error}"));
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

    private static string NowIso() =>
        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

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
    /// 按当前 MES 模式解析 sql_id。流程定义里的 id 是**逻辑**引用：mock 条目与正式条目
    /// 互为镜像（catalog 用 <c>mirrors</c> 标注），由引擎按模式决定实际执行哪一份。
    ///
    /// 两个方向都要重定向。此前只做了 mock→formal（Oracle 模式）这一向，于是引用正式
    /// <c>mes.</c> id 的节点在 mock 模式下会直接执行 Oracle SQL：操作员流程的 9 个 MES 节点
    /// 全部引用正式 id，其中「最近产品批号」用到 NVL，在 SQLite 下直接报
    /// <c>no such function: NVL</c>，整条流程走不下去。
    /// （同构做法见 WireDiscoEqpSyncService.ResolveMesItem。）
    /// </summary>
    private SqlCatalogItem? ResolveSqlItem(string sqlId)
    {
        if (_svc.Mes.Mode == MesMode.Oracle
            && sqlId.StartsWith("mes_mock.", StringComparison.OrdinalIgnoreCase))
        {
            var formalId = "mes." + sqlId["mes_mock.".Length..];
            var formal = _svc.Catalog.Find(formalId);
            if (formal is not null)
                return formal;
        }
        else if (_svc.Mes.Mode != MesMode.Oracle
                 && sqlId.StartsWith("mes.", StringComparison.OrdinalIgnoreCase))
        {
            var mockId = "mes_mock." + sqlId["mes.".Length..];
            var mock = _svc.Catalog.Find(mockId);
            if (mock is not null)
                return mock;
        }

        return _svc.Catalog.Find(sqlId);
    }

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
