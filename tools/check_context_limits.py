#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""上下文上限检查器。

规范见 docs/decisions/2026-11-02-上下文不设上限.md：凡是进入模型请求的内容（分区上下文、
简报文本、对话历史、回灌的记忆）一律不得设容量上限。本脚本按四条规则扫描上下文通路源文件。

用法：
    python tools/check_context_limits.py            # 扫全仓 + 跑合成夹具
    python tools/check_context_limits.py --quiet

退出码：0 = 干净且夹具通过；1 = 有违规；2 = 检查器自身故障（夹具不过、清单文件缺失、
覆盖漏文件）。夹具不过时绝不报绿——只扫真码的两面性无从验证，全绿可能是假阴性。
"""

import argparse
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# 上下文通路：这些文件里的任何内容都直接或间接进入模型请求
SCOPE = [
    os.path.join("AI", "StoreContext.cs"),
    os.path.join("AI", "ContextBrief.cs"),
    os.path.join("AI", "ClientSpeech.cs"),
    os.path.join("AI", "ClientInsight.cs"),
    os.path.join("Patches", "ClientInsightPatches.cs"),
    os.path.join("AI", "CustomerArchive.cs"),
    os.path.join("AI", "RegularManager.cs"),
    os.path.join("AI", "PromptBook.cs"),
    os.path.join("AI", "StyleBook.cs"),
    os.path.join("AI", "AffinityBook.cs"),
    os.path.join("AI", "TokenMeter.cs"),
    os.path.join("AI", "Player2Client.cs"),
    os.path.join("AI", "Client", "IAIClient.cs"),
    os.path.join("UI", "CustomerChatUI.cs"),
    os.path.join("ModEntry.cs"),
]

# 引用面守卫：调用这些入口的文件必须出现在 SCOPE 里，否则新增一条上下文通路会悄悄逃过检查
ENTRY_POINTS = re.compile(r"StoreContext\.Build|ContextBrief\.Render|\bInteractiveContext\b|\bChat(?:Async)?\s*\(")

RULES = [
    (
        "size-budget",
        "体积/预算封顶（字符、token、字节，或 max_tokens）",
        re.compile(r"max_tokens|\bTrimToBudget\b|\b\w*(?:Max|Limit|Budget)\w*(?:Chars|Tokens|Bytes|Size)\b"),
    ),
    (
        "char-truncate",
        "定长截断文本",
        # 只认「从 0 起、按长度/上限切」的形状：Substring(0, 40) / Substring(0, maxLen) / Truncate(...)。
        # Substring(KeyPrefix.Length)、Substring(0, split) 这类是键名或分隔符解析，不是截断
        re.compile(r"Trunc\w*\s*\(|\.Substring\s*\(\s*0\s*,\s*(?:\d+|\w*(?:Max|Limit|Length|Chars|Size)\w*)\b|\.Remove\s*\(\s*0\s*,"),
    ),
    (
        "count-cap",
        "条数上限（命名常量或对 Count 的数值封顶）",
        # Count 的数值比较单独判：> 0 / == 0 这类是空判，不算封顶
        re.compile(r"\b\w*(?:Max|Limit)\w*(?:Items|Lines|Events|Turns|Records|Messages|Rows|Entries|Count)\b"),
    ),
    (
        "history-drop",
        "丢弃历史（丢最旧、跳过、截取）",
        re.compile(r"\.RemoveAt\s*\(\s*0\s*\)|\.RemoveRange\s*\(\s*0\b|\.Skip\s*\(|\.Take\s*\(|丢最旧|丢弃最旧"),
    ),
    (
        "private-channel",
        "玩家面板专属字段进入上下文（顾客看不到这些数字）",
        # 只认「字段访问」的形状，不认裸词：注释里提到 order/traffic 不该报错。
        # 判据来自反编译：这些字段只被玩家 UI/难度/后台逻辑读，没有任何顾客侧代码读它们。
        # 反过来，garbageLevel（游戏用它罚顾客态度）、isPowerOn、事件与招牌是可见的，不在黑名单
        re.compile(
            r"\bstation\.(order|traffic|happiness|todayGutterflowPurity|todayRustwaterPurity)\b"
            r"|\bstore\.(retailMarkup|contrabandMarkup\w*|dealMakerBargainBonus|rentValue|loanValue"
            r"|dayUntilRent|dayUntilLoan|storeReputations|playerCash|appraiserLevel|appraisalProgress"
            r"|vendingMislabel\w*|requestMislabel\w*|lastInspectionLossValue|lastTheftValue)\b"
            r"|\bclient\.(tradeReputationMultiplier|buyTradeRepMultiplierOverride|sellTradeRepMultiplierOverride)\b"
        ),
    ),
]

COUNT_GUARD = re.compile(r"\.Count\s*(<=|>=|<|>)\s*(\d+)")

# 已批准的豁免：(相对路径, 规则 id) -> 理由。豁免是审计面，加一条就要写清为什么它不是上下文上限
EXEMPT = {
    (os.path.join("AI", "ClientSpeech.cs"), "char-truncate"):
        "裁的是玩家可配置的「单句长度」，作用于发言成品与气泡文本，不是送入模型的资料",
    (os.path.join("UI", "CustomerChatUI.cs"), "char-truncate"):
        "同上：交互回复按同一份单句长度设置成形后才入历史",
    (os.path.join("AI", "CustomerArchive.cs"), "count-cap"):
        "MaxRecords 约束存档里保留的档案条目数，不裁单次请求的内容",
    # 规范本身要求「长度口径写进提示词」，所以提示词里出现台词长度上限是合规写法，
    # 不是给送入模型的资料封顶。真正裁内容的动作另有规则守着：任何 Truncate/Substring
    # 都会在该文件被 char-truncate 抓到，改名叫 MaxLineChars 之外的名字也躲不掉。
    (os.path.join("AI", "PromptBook.cs"), "size-budget"):
        "MaxLineChars 只被插进输出契约文本（告诉模型说多长），不参与任何资料裁剪；"
        "若它哪天被用于截断，char-truncate 会当场抓到",
}

COMMENT = re.compile(r"//.*$")


def code_only(line):
    """注释里的散文不算代码：规范文档会引用被禁的符号名，扫到就会误报。"""
    return COMMENT.sub("", line)


def count_guards(text):
    """Count 的数值封顶：> 0 是空判，>=1 / <N 这类才是限制条数。"""
    hits = []
    for match in COUNT_GUARD.finditer(text):
        op, value = match.group(1), int(match.group(2))
        if op in ("<", "<=") and value >= 1:
            hits.append(match.group(0))
        elif op in (">=",) and value >= 1:
            hits.append(match.group(0))
    return hits


def scan_source(rel_path, source):
    """返回 (规则 id, 行号, 命中片段) 列表；纯函数，夹具与真码走同一条路。"""
    findings = []
    for number, raw in enumerate(source.splitlines(), 1):
        line = code_only(raw)
        if not line.strip():
            continue
        for rule_id, _label, pattern in RULES:
            match = pattern.search(line)
            if match:
                findings.append((rule_id, number, match.group(0).strip()))
        for hit in count_guards(line):
            findings.append(("count-cap", number, hit.strip()))
    return findings


def is_exempt(rel_path, rule_id):
    reason = EXEMPT.get((rel_path, rule_id))
    return bool(reason)


def check_repo():
    violations = []
    gaps = []
    covered = {os.path.normcase(p) for p in SCOPE}

    for rel in SCOPE:
        path = os.path.join(ROOT, rel)
        if not os.path.isfile(path):
            gaps.append("清单里的上下文通路文件不存在：%s（改名会让检查空转）" % rel)
            continue
        with open(path, "r", encoding="utf-8-sig") as handle:
            source = handle.read()
        for rule_id, number, snippet in scan_source(rel, source):
            if not is_exempt(rel, rule_id):
                violations.append("%s:%d [%s] %s" % (rel, number, rule_id, snippet))

    # 引用面：调上下文入口/发请求的文件必须在 SCOPE 内
    for dir_path, dir_names, file_names in os.walk(ROOT):
        dir_names[:] = [d for d in dir_names if d not in ("bin", "obj", "build") and not d.startswith(".")]
        for name in file_names:
            if not name.endswith(".cs"):
                continue
            full = os.path.join(dir_path, name)
            rel = os.path.relpath(full, ROOT)
            if os.path.normcase(rel) in covered:
                continue
            with open(full, "r", encoding="utf-8-sig") as handle:
                source = handle.read()
            if ENTRY_POINTS.search(source):
                gaps.append("%s 调用了上下文入口却不在 SCOPE 里，请纳入检查" % rel)
    return violations, gaps


# ---------------------------------------------------------------------------
# 合成夹具：双向自测。脏样例每类都必须被抓，干净样例必须零报。
# 夹具不抄被检代码的常量或字面量，只造出该规则针对的形状。
# ---------------------------------------------------------------------------

FIXTURES_DIRTY = {
    "size-budget": [
        "private const int PromptBudgetChars = 4500;",
        "body[\"max_tokens\"] = 512;",
        "TrimToBudget(context);",
    ],
    "char-truncate": [
        "note = TruncateByLength(evt.newsDescription, 160);",
        "return content.Substring(0, 40);",
        "builder.Remove(0, 12);",
    ],
    "count-cap": [
        "private const int MaxCounterRows = 12;",
        "for (int i = 0; i < turns.Count && lines.Count < 3; i++)",
        "if (list.Count >= 5) break;",
    ],
    "history-drop": [
        "while (turns.Count > 24) turns.RemoveAt(0);",
        "var tail = lines.Skip(2);",
    ],
    "private-channel": [
        'shop["order"] = station.order;',
        'policy["retailMarkup"] = store.retailMarkup;',
        'body["tradeRepMultiplier"] = Math.Round(client.tradeReputationMultiplier, 2);',
        'shop["cash"] = store.playerCash;',
    ],
}

# 干净样例：空判、全量下发、按键名切分、以及注释里提到禁用词——都不该报
FIXTURES_CLEAN = [
    "private const int AbsentLeftDays = 7;",
    "if (string.IsNullOrEmpty(text) || items.Count == 0) return items;",
    "if (lines.Count > 0) body[\"dialogue\"] = lines;",
    "foreach (KeyValuePair<string, string> turn in transcript)",
    "JArray events = ActiveEvents(station);",
    "internal static int Estimate(string text)",
    "// 这里的 MaxContextChars 只是注释里提到旧上限，不该被当成代码",
    "entry[\"note\"] = evt.newsDescription.Trim();",
    "turns.Add(new KeyValuePair<string, string>(\"user\", text));",
    "string rest = key.Substring(KeyPrefix.Length);",
    "identifier = rest.Substring(0, split);",
    "return text.Trim();",
    "private const int MaxActiveRegulars = 4;",
    "shop[\"budget\"] = client.GetBudget();",
    "public int PromptTokens { get; set; }",
    # 看得见的环境量：垃圾（游戏用它罚顾客态度）、停电、招牌，都不是玩家后台字段
    "if (store.garbageLevel > 0) shop[\"garbage\"] = store.garbageLevel;",
    "if (!store.isPowerOn) shop[\"powerOut\"] = true;",
    "if (!string.IsNullOrWhiteSpace(store.storeName)) shop[\"storeName\"] = store.storeName;",
]


def run_fixtures():
    """返回错误列表；空表示检查器两面都灵。"""
    errors = []
    for rule_id, samples in FIXTURES_DIRTY.items():
        caught = False
        for sample in samples:
            hits = [f for f in scan_source("fixture.cs", sample) if f[0] == rule_id]
            if hits:
                caught = True
                break
        if not caught:
            errors.append("夹具失效：%s 规则的脏样例没被检出，该规则形同虚设" % rule_id)

    for line in FIXTURES_CLEAN:
        hits = scan_source("fixture.cs", line)
        if hits:
            errors.append("夹具失效：干净样例被误报 -> %s（%s）" % (line.strip(), hits[0][0]))
    return errors


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description="检查上下文不设上限")
    parser.add_argument("--quiet", action="store_true", help="只在有问题时输出")
    args = parser.parse_args()

    fixture_errors = run_fixtures()
    if fixture_errors:
        print("检查器自测未通过（结论不可信）：")
        for err in fixture_errors:
            print("  - " + err)
        return 2

    violations, gaps = check_repo()
    if gaps:
        print("检查覆盖有洞：")
        for gap in gaps:
            print("  - " + gap)

    exempt_count = len(EXEMPT)
    if not args.quiet or violations or gaps:
        print("上下文通路文件 %d 个，豁免 %d 条（豁免理由见脚本内表）" % (len(SCOPE), exempt_count))

    if violations:
        print("上下文通路违规（容量上限 / 玩家后台字段）：")
        for v in violations:
            print("  - " + v)
        print("按规范改：内容一律全量下发、长度口径写进提示词；"
              "玩家面板专属字段不许进上下文。确属豁免就改 EXEMPT 表并写理由。")
        return 1
    if gaps:
        return 2

    print("干净：上下文通路无容量上限，且双向夹具通过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
