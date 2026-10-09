#!/usr/bin/env python3
"""dbcheck.py - assertions against the cabinet SQLite database (app.db).

Why: most acceptance items can be decided from data rather than from clicking the UI.
Record counts per action type, door open/close pairing, the compensation queue and the
UTC-storage / local-display time convention are all visible in app.db. The UI driver is
then only needed for the steps that genuinely read pixels.

Time convention: app_operation_log.ts, door_open_ts and door_close_ts are stored in UTC
("yyyy-MM-dd HH:mm:ss"). Anything you pass on the command line as a lower/upper bound is
interpreted as UTC too; human-readable output is converted to local time (--tz).

Reading a live database: always copy app.db (plus app.db-wal and app.db-shm) BEFORE
querying a running cabinet, or use SQLite's backup. Opening read-only is not enough to
get a consistent snapshot while the app is writing.

Commands
    snapshot <db>                       counts per action type, time range, queue state
    verify   <db> --expect file.json    run assertions, exit 1 when any fails
    counts   <db> [--since <utc>]       per-action-type counts in a window (口径核对：一次动作 = 1 条)
    tail     <db> [-n 20]               last N rows (local time), for eyeballing
    pairs    <db> [--since <utc>]       door-timestamp self-consistency + uncommanded pairing
    columns  <db>                       which app_operation_log columns exist
    pairs    <db>                       door open/close pairing report

Exit codes: 0 ok, 1 verification failed, 2 usage/data error.
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

# Mirrors WireCabinet.Data.OperationLogActionTypes (codes only; labels are UI concerns).
ACTION_LABELS = {
    "mh_load_available_wire": "MH 存入可用焊丝",
    "op_return_wire": "OP 归还旧焊丝",
    "op_issue_available_wire": "OP 领用新焊丝",
    "op_take_wire": "OP 取出焊丝",
    "door_open": "格口开门",
    "mh_open_slot": "MH 打开单个格口",
    "mh_open_slots_batch": "MH 批量打开格口",
    "maint_trial_open": "维护试开格口",
    "door_close": "格口关门",
    "mh_assume_empty": "开门即清空",
    "agv_move": "AGV 控车",
    "agv_charge": "AGV 自动回充",
    "view_operation_log": "查看存取记录",
    "export_operation_log": "导出存取记录",
}

UTC_FMT = "%Y-%m-%d %H:%M:%S"


def connect(path: str) -> sqlite3.Connection:
    if not os.path.exists(path):
        fail(f"database not found: {path}")
    # mode=ro keeps this tool from ever writing to a production database.
    #
    # 路径必须用 as_uri() 转义再拼查询串：本仓库就在 "...\C#\..." 下，直接拼
    # f"file:{path}?mode=ro" 会把 '#' 之后当成 URI fragment，SQLite 实际打开的是另一个
    # 文件（表现为 "no such table: app_operation_log"）。as_uri() 会把 # 编成 %23。
    conn = sqlite3.connect(Path(path).resolve().as_uri() + "?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    return conn


def fail(msg: str, code: int = 2) -> None:
    print(json.dumps({"error": msg}, ensure_ascii=False))
    sys.exit(code)


def to_local(text: str | None, tz: timezone) -> str | None:
    """UTC text from the DB -> local text, the same direction the UI displays."""
    if not text:
        return None
    try:
        stamp = datetime.strptime(text.strip()[:19], UTC_FMT).replace(tzinfo=timezone.utc)
    except ValueError:
        return text
    return stamp.astimezone(tz).strftime(UTC_FMT)


def table_columns(conn: sqlite3.Connection, table: str) -> set[str]:
    return {r["name"] for r in conn.execute(f"PRAGMA table_info({table})")}


def cmd_snapshot(conn: sqlite3.Connection, tz: timezone, args) -> int:
    cols = table_columns(conn, "app_operation_log")
    rows = conn.execute(
        "SELECT action_type, COUNT(*) AS n FROM app_operation_log GROUP BY action_type"
    ).fetchall()
    by_action = {(r["action_type"] or ""): r["n"] for r in rows}
    total = conn.execute("SELECT COUNT(*) AS n FROM app_operation_log").fetchone()["n"]
    span = conn.execute(
        "SELECT MIN(ts) AS lo, MAX(ts) AS hi FROM app_operation_log"
    ).fetchone()

    result = {
        "path": args.db,
        "columns_present": sorted(cols),
        "total": total,
        "by_action": {k: by_action[k] for k in sorted(by_action)},
        "by_action_labeled": {
            (ACTION_LABELS.get(k, k) + f" [{k}]"): by_action[k] for k in sorted(by_action)
        },
        "ts_min_utc": span["lo"],
        "ts_max_utc": span["hi"],
        "ts_max_local": to_local(span["hi"], tz),
        "pending": pending_state(conn),
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


def pending_state(conn: sqlite3.Connection) -> dict:
    try:
        rows = conn.execute(
            "SELECT status, COUNT(*) AS n FROM operation_log_pending GROUP BY status"
        ).fetchall()
    except sqlite3.Error as exc:
        return {"error": str(exc)}
    state = {r["status"]: r["n"] for r in rows}
    state["total"] = sum(state.values())
    return state


def cmd_tail(conn: sqlite3.Connection, tz: timezone, args) -> int:
    rows = conn.execute(
        """
        SELECT ts, action_type, operator_id, operator_name, slot_no, lot_no, qty,
               result, error_info, door_open_ts, door_close_ts, is_cleared, is_uncommanded,
               flow_id, node_id
        FROM app_operation_log ORDER BY ts DESC, id DESC LIMIT ?
        """,
        (args.n,),
    ).fetchall()
    out = []
    for r in rows:
        row = dict(r)
        row["action_label"] = ACTION_LABELS.get(r["action_type"] or "", r["action_type"])
        row["ts_utc"] = row.pop("ts")
        row["door_open_utc"] = row.pop("door_open_ts")
        row["door_close_utc"] = row.pop("door_close_ts")
        row["ts_local"] = to_local(row["ts_utc"], tz)
        row["door_open_local"] = to_local(row["door_open_utc"], tz)
        row["door_close_local"] = to_local(row["door_close_utc"], tz)
        out.append(row)
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0


def cmd_counts(conn: sqlite3.Connection, tz: timezone, args) -> int:
    """按动作类型数条数——现场核对口径优化用，省得在界面上眯着眼数。

    用法：动作前 `snapshot` 记下 `ts_max_utc`，动作后

        python dbcheck.py counts <app.db> --since "<那个时刻>"

    口径优化后一次「人的操作」应恰好 **1 条**：存料 1、批量开门 1（带 slot_count）、
    一轮存废取新 2（归还 + 领用并取出）、自动返航连续失败 1（detail.count 记次数）。
    """
    since = getattr(args, "since", None)
    where = "WHERE ts >= ?" if since else ""
    params: tuple = (since,) if since else ()

    rows = conn.execute(
        f"""
        SELECT action_type, COUNT(*) AS n, MIN(ts) AS first_ts, MAX(ts) AS last_ts
        FROM app_operation_log {where}
        GROUP BY action_type ORDER BY n DESC, action_type
        """,
        params,
    ).fetchall()

    by_action = []
    for r in rows:
        code = r["action_type"] or ""
        by_action.append(
            {
                "action_type": code,
                "label": ACTION_LABELS.get(code, code),
                "count": r["n"],
                "first_local": to_local(r["first_ts"], tz),
                "last_local": to_local(r["last_ts"], tz),
            }
        )

    print(json.dumps(
        {
            "since_utc": since,
            "total": sum(r["n"] for r in rows),
            "counts": {r["action_type"] or "": r["n"] for r in rows},
            "by_action": by_action,
            "note": "口径优化后一次人的操作 = 1 条（开门/关门/清空是这条上的字段）。"
                    "不带 --since 会把历史全算进来，看不出本次动作。",
        },
        ensure_ascii=False,
        indent=2,
    ))
    return 0


def cmd_pairs(conn: sqlite3.Connection, tz: timezone, args) -> int:
    """门时刻自洽与配对（记录口径优化 2026-10-09 之后）。

    新口径下门时刻是**业务记录 / 开门记录上的字段**，不再是独立的 door_open / door_close 事件，
    所以不再按「关门事件去配开门事件」判，改判两件事：

    1. 凡写了 door_close_ts 的记录，必须同时有 door_open_ts，且关门不早于开门；
    2. 无指令开门（is_uncommanded = 1）仍是异常配对——「开门一条 + 关门一条」，关门必须能配回那条开门。

    **务必带 --since**：口径优化之前写的 854 条「无来源关门」（库内记 open、重启后 DI 确认锁闭补的，
    door_close_ts 有值而 door_open_ts 为空）是老数据的既成事实，10-09 修掉后不再产生；
    不带 --since 会把它们全报成不一致，看着像新版本坏了。
    """
    since = getattr(args, "since", None)

    def scoped(condition: str) -> tuple[str, list]:
        if since:
            return f"{condition} AND ts >= ?", [since]
        return condition, []

    close_where, close_params = scoped("door_close_ts IS NOT NULL AND door_close_ts <> ''")
    with_close = conn.execute(
        f"""
        SELECT id, action_type, slot_no, ts, door_open_ts, door_close_ts
        FROM app_operation_log
        WHERE {close_where}
        ORDER BY ts
        """,
        close_params,
    ).fetchall()

    inconsistent: list[dict] = []
    for r in with_close:
        open_ts = r["door_open_ts"]
        if not open_ts:
            reason = "有 door_close_ts 却没有 door_open_ts"
        elif r["door_close_ts"] < open_ts:
            reason = "关门时刻早于开门时刻"
        else:
            continue
        inconsistent.append(
            {
                "id": r["id"],
                "action_type": r["action_type"],
                "slot_no": r["slot_no"],
                "door_open_ts": open_ts,
                "door_close_ts": r["door_close_ts"],
                "reason": reason,
            }
        )

    # 无指令开门的异常配对（仍独立成条，见草案 §3.4）
    open_where, open_params = scoped("is_uncommanded = 1 AND action_type IN ('door_open', 'mh_open_slot')")
    uncommanded_opens = conn.execute(
        f"SELECT slot_no, ts FROM app_operation_log WHERE {open_where} ORDER BY ts",
        open_params,
    ).fetchall()
    open_keys = {(o["slot_no"] or "", o["ts"]) for o in uncommanded_opens}

    uc_where, uc_params = scoped("action_type = 'door_close' AND is_uncommanded = 1")
    uncommanded_closes = conn.execute(
        f"SELECT slot_no, ts, door_open_ts FROM app_operation_log WHERE {uc_where} ORDER BY ts",
        uc_params,
    ).fetchall()

    unlinked: list[dict] = []
    for c in uncommanded_closes:
        if (c["slot_no"] or "", c["door_open_ts"]) in open_keys:
            continue
        unlinked.append(
            {
                "slot_no": c["slot_no"],
                "close_utc": c["ts"],
                "close_local": to_local(c["ts"], tz),
                "door_open_ts_in_record": c["door_open_ts"],
            }
        )

    open_only_where, open_only_params = scoped(
        "door_open_ts IS NOT NULL AND door_open_ts <> '' AND (door_close_ts IS NULL OR door_close_ts = '')"
    )
    still_open = conn.execute(
        f"SELECT id, action_type, slot_no, ts, door_open_ts FROM app_operation_log WHERE {open_only_where} ORDER BY ts",
        open_only_params,
    ).fetchall()

    result = {
        "since_utc": since,
        "note": "未带 --since 时按全表判；口径优化前的无来源关门属历史既成事实，会报成不一致。",
        "records_with_door_close_ts": len(with_close),
        "inconsistent_door_timestamps": inconsistent,
        "uncommanded_close_events": len(uncommanded_closes),
        "uncommanded_closes_without_open_link": unlinked,
        "doors_open_without_close_yet": [
            {
                "id": r["id"],
                "action_type": r["action_type"],
                "slot_no": r["slot_no"],
                "open_local": to_local(r["door_open_ts"], tz),
            }
            for r in still_open
        ],
    }
    print(json.dumps(result, ensure_ascii=False, indent=2))
    # 门时刻不自洽、或异常配对断了才是失败；「还没关门」是信息性的——
    # 测试停下时门可能确实还开着。
    return 1 if (inconsistent or unlinked) else 0


def cmd_verify(conn: sqlite3.Connection, tz: timezone, args) -> int:
    with open(args.expect, encoding="utf-8") as fh:
        exp = json.load(fh)

    failures: list[str] = []
    checks: list[dict] = []
    since = exp.get("since_utc")
    where = "WHERE ts >= ?" if since else ""
    params: tuple = (since,) if since else ()

    def count(extra: str = "", extra_params: tuple = ()) -> int:
        sql = f"SELECT COUNT(*) AS n FROM app_operation_log {where}"
        if extra:
            sql += (" AND " if since else " WHERE ") + extra
        return conn.execute(sql, params + extra_params).fetchone()["n"]

    def record(name: str, ok: bool, detail):
        checks.append({"check": name, "ok": ok, "detail": detail})
        if not ok:
            failures.append(name)

    # 1. expected exact counts per action type within the window
    for code, expected in (exp.get("counts") or {}).items():
        got = count("action_type = ?", (code,))
        record(f"count {code} == {expected}", got == expected, {"expected": expected, "actual": got})

    # 2. minimum counts (e.g. "at least one door event")
    for code, minimum in (exp.get("min_counts") or {}).items():
        got = count("action_type = ?", (code,))
        record(f"count {code} >= {minimum}", got >= minimum, {"min": minimum, "actual": got})

    # 3. action types that must not appear at all in the window
    for code in exp.get("forbid_action_types") or []:
        got = count("action_type = ?", (code,))
        record(f"forbidden {code} absent", got == 0, {"actual": got})

    # 4. failed business actions must carry a reason
    for code in exp.get("failures_need_error_info") or []:
        got = count(
            "action_type = ? AND (result = ? OR result IS NULL) AND (error_info IS NULL OR error_info = '')",
            (code, "失败"),
        )
        record(f"{code} failures carry error_info", got == 0, {"rows_missing_reason": got})

    # 5. door timestamps are self-consistent fields on the record (口径优化 2026-10-09)
    #
    # 必须限定在 since_utc 窗口内：口径优化之前写的 854 条「无来源关门」（door_close_ts 有值、
    # door_open_ts 为空，10-09 已修掉不再产生）是老数据的既成事实，扫全表会把它们报成失败。
    if exp.get("require_paired_doors"):
        window = "AND ts >= ?" if since else ""
        wparams: tuple = (since,) if since else ()
        bad = conn.execute(
            f"""
            SELECT COUNT(*) AS n FROM app_operation_log
            WHERE door_close_ts IS NOT NULL AND door_close_ts <> ''
              {window}
              AND (door_open_ts IS NULL OR door_open_ts = '' OR door_close_ts < door_open_ts)
            """,
            wparams,
        ).fetchone()["n"]
        record("door timestamps are self-consistent (open present, close >= open)", bad == 0, {"bad_rows": bad})

        unlinked = conn.execute(
            f"""
            SELECT COUNT(*) AS n FROM app_operation_log c
            WHERE c.action_type = 'door_close' AND c.is_uncommanded = 1
              {"AND c.ts >= ?" if since else ""}
              AND NOT EXISTS (
                SELECT 1 FROM app_operation_log o
                WHERE o.slot_no = c.slot_no AND o.is_uncommanded = 1
                  AND o.action_type IN ('door_open','mh_open_slot')
                  AND o.ts = c.door_open_ts)
            """,
            wparams,
        ).fetchone()["n"]
        record("uncommanded door_close rows link to their open", unlinked == 0, {"unlinked": unlinked})

    # 6. time storage convention: ts must look like UTC wall clock, not local
    if exp.get("require_utc_storage"):
        latest = conn.execute("SELECT MAX(ts) AS ts FROM app_operation_log").fetchone()["ts"]
        if latest:
            stamp = datetime.strptime(latest[:19], UTC_FMT).replace(tzinfo=timezone.utc)
            drift = abs((datetime.now(timezone.utc) - stamp).total_seconds())
            limit = int(exp.get("utc_drift_tolerance_seconds", 900))
            record(
                "newest ts is UTC (drift within tolerance)",
                drift <= limit,
                {"drift_seconds": round(drift), "tolerance": limit, "ts_utc": latest,
                 "ts_local": to_local(latest, tz)},
            )

    # 7. compensation queue state
    if "max_pending" in exp:
        state = pending_state(conn)
        n = state.get("pending", 0) + state.get("failed", 0)
        record(f"open compensation items <= {exp['max_pending']}", n <= exp["max_pending"],
               {"actual": n, "state": state})

    out = {
        "expect_file": args.expect,
        "since_utc": since,
        "ok": not failures,
        "failures": failures,
        "checks": checks,
    }
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0 if not failures else 1


def cmd_columns(conn: sqlite3.Connection, tz, args) -> int:
    print(json.dumps({
        "app_operation_log": sorted(table_columns(conn, "app_operation_log")),
        "operation_log_pending": sorted(table_columns(conn, "operation_log_pending")),
    }, ensure_ascii=False, indent=2))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="assert on the cabinet app.db")
    parser.add_argument("command", choices=["snapshot", "verify", "tail", "counts", "pairs", "columns"])
    parser.add_argument("db", help="path to app.db (copy it off the cabinet first)")
    parser.add_argument("--expect", help="expectations json (verify only)")
    parser.add_argument("-n", type=int, default=20, help="rows for tail (default 20)")
    parser.add_argument("--tz", default=None, help="display timezone, e.g. Asia/Shanghai")
    parser.add_argument(
        "--since",
        default=None,
        help="UTC lower bound 'YYYY-MM-DD HH:MM:SS'；pairs 用它只看该时刻之后的记录"
        "（不带会把口径优化前的历史无来源关门报成不一致）",
    )
    args = parser.parse_args()

    if args.command == "verify" and not args.expect:
        fail("verify needs --expect <file.json>")

    tz = timezone(timedelta(hours=8))
    if args.tz:
        try:
            from zoneinfo import ZoneInfo

            tz = ZoneInfo(args.tz)
        except Exception as exc:  # noqa: BLE001 - any tz failure is a usage error
            fail(f"bad --tz {args.tz}: {exc}")

    conn = connect(args.db)
    try:
        if args.command == "snapshot":
            return cmd_snapshot(conn, tz, args)
        if args.command == "verify":
            return cmd_verify(conn, tz, args)
        if args.command == "tail":
            return cmd_tail(conn, tz, args)
        if args.command == "counts":
            return cmd_counts(conn, tz, args)
        if args.command == "pairs":
            return cmd_pairs(conn, tz, args)
        return cmd_columns(conn, tz, args)
    finally:
        conn.close()


if __name__ == "__main__":
    sys.exit(main())
