using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Probablytalk;

// 顾客名册：列出本存档里所有产生过对话的顾客（名字 / 势力 / 最近到访日 / 谈过几次 / 成交几次 / 离店总结）。
// 数据来自 CustomerArchive，落的是 PlayerStore 的 per-save modData，因此天然是存档级：
// 换存档即换名单，新档即空名单。
// 入口挂在游戏原生「选项」界面末尾（见 ArchivePatches），点一行展开单人档案：总结、态度与本次往来。
internal static class CustomerRosterUI
{
    private const string WindowId = "probablytalk_roster";
    private const string DetailWindowId = "probablytalk_roster_detail";
    private const int MaxSummaryChars = 180;
    private const int MaxTranscriptRows = 12;

    private static CustomUIWindow _window;
    private static CustomUIWindow _detail;

    // 选项界面里的入口按钮：已挂过一次就不再重复挂（窗口重开时元素会重建）
    internal static void AttachToOptionsWindow(CustomUIWindow window, string modId)
    {
        try
        {
            if (window == null || !window.IsAlive || CustomUIManager.Instance == null) return;
            if (window.Get("roster_open") != null) return;
            CustomUIBuilder builder = new CustomUIBuilder(CustomUIManager.Instance, window);
            builder.AddSpace(10f);
            builder.AddSpace(2f);
            builder.BeginRow(4f, TextAnchor.MiddleLeft);
            builder.AddHeader(Loc(modId, "roster_section", "Talk Records"), "roster_header");
            builder.End();
            int count = CustomerArchive.Roster().Count;
            builder.AddButton(Loc(modId, "roster_open", "Customer Roster") + "  (" + count + ")", () => Toggle(modId), "roster_open");
            builder.Last.SetPreferredSize(320f, 0f);
            CustomUIBuilder.RebuildLayout(window.ContentRoot);
        }
        catch (Exception ex)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("roster entry failed: " + ex.Message);
        }
    }

    internal static void Toggle(string modId)
    {
        if (_window != null && _window.IsAlive)
        {
            _window.Close();
            _window = null;
            return;
        }
        Open(modId);
    }

    private static void Open(string modId)
    {
        try
        {
            if (CustomUIManager.Instance == null) return;
            if (_window != null && _window.IsAlive) _window.Close();
            string layerId = CustomUIManager.HasLayer(CustomUIManager.LAYER_PAPER) ? CustomUIManager.LAYER_PAPER : null;
            CustomUIBuilder builder = CustomUIManager.Instance
                .CreateWindow(WindowId, Loc(modId, "roster_title", "Customer Roster"), layerId)
                .SetSize(680f, 520f)
                .SetCloseOnEscape(true);
            _window = builder.GetWindow();

            builder.BeginScrollFlexible();
            List<CustomerArchive.Record> rows = CustomerArchive.Roster();
            if (rows.Count == 0)
            {
                builder.AddLabel(Loc(modId, "roster_empty", "No customer has spoken with you yet."));
            }
            foreach (CustomerArchive.Record record in rows) AddRow(builder, modId, record);
            builder.End();

            builder.Show();
            CustomUIBuilder.RebuildLayout(_window.ContentRoot);
        }
        catch (Exception ex)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("roster open failed: " + ex.Message);
        }
    }

    private static void AddRow(CustomUIBuilder builder, string modId, CustomerArchive.Record record)
    {
        int debt = CreditBook.DebtOf(record.Identifier);
        string title = (string.IsNullOrWhiteSpace(record.Name) ? record.Identifier : record.Name)
            + "  ·  " + StoreContext.FactionText(record.Faction)
            + "  ·  " + Loc(modId, "roster_day", "Day") + " " + record.LastDay
            + "  ·  " + Loc(modId, "roster_talks", "Talks") + " " + record.Talks
            + "  ·  " + Loc(modId, "roster_deals", "Deals") + " " + record.Deals
            + (debt > 0 ? "  ·  " + Loc(modId, "roster_debt", "Owes") + " " + debt : string.Empty);
        builder.BeginRow(6f, TextAnchor.MiddleLeft);
        builder.AddButton(title, () => OpenDetail(modId, record), null);
        builder.End();
        if (!string.IsNullOrWhiteSpace(record.Summary))
        {
            builder.AddLabel(TextLimit.Truncate(record.Summary, MaxSummaryChars));
        }
        builder.AddSpace(2f);
    }

    private static void OpenDetail(string modId, CustomerArchive.Record record)
    {
        try
        {
            if (record == null || CustomUIManager.Instance == null) return;
            if (_detail != null && _detail.IsAlive) _detail.Close();
            string layerId = CustomUIManager.HasLayer(CustomUIManager.LAYER_OVERLAY) ? CustomUIManager.LAYER_OVERLAY : null;
            CustomUIBuilder builder = CustomUIManager.Instance
                .CreateWindow(DetailWindowId, string.IsNullOrWhiteSpace(record.Name) ? record.Identifier : record.Name, layerId)
                .SetSize(560f, 420f)
                .SetCloseOnEscape(true);
            _detail = builder.GetWindow();

            builder.BeginScrollFlexible();
            int debt = CreditBook.DebtOf(record.Identifier);
            if (debt > 0)
            {
                builder.AddHeader(Loc(modId, "roster_debt", "Owes"), "debt_header");
                builder.AddLabel(debt + "  (" + Loc(modId, "roster_day", "Day") + " " + CreditBook.SinceDay(record.Identifier) + ")");
            }
            if (!string.IsNullOrWhiteSpace(record.Archetype))
            {
                builder.AddLabel(Loc(modId, "roster_archetype", "Archetype") + "：" + record.Archetype);
            }
            if (record.Ocean != null)
            {
                builder.AddLabel(Loc(modId, "roster_ocean", "OCEAN") + "："
                    + Loc(modId, "roster_ocean_o", "Open") + " " + record.Ocean.Openness + " · "
                    + Loc(modId, "roster_ocean_c", "Consc") + " " + record.Ocean.Conscientiousness + " · "
                    + Loc(modId, "roster_ocean_e", "Extra") + " " + record.Ocean.Extraversion + " · "
                    + Loc(modId, "roster_ocean_a", "Agree") + " " + record.Ocean.Agreeableness + " · "
                    + Loc(modId, "roster_ocean_n", "Neuro") + " " + record.Ocean.Neuroticism);
            }
            builder.AddHeader(Loc(modId, "roster_summary", "Summary"), "summary_header");
            string summary = string.IsNullOrWhiteSpace(record.Summary)
                ? Loc(modId, "roster_no_summary", "The summary is written after the customer leaves.")
                : record.Summary;
            builder.AddLabel(summary);
            if (!string.IsNullOrWhiteSpace(record.Stance))
            {
                builder.AddHeader(Loc(modId, "roster_stance", "Stance"), "stance_header");
                builder.AddLabel(record.Stance);
            }
            if (record.Transcript.Count > 0)
            {
                builder.AddHeader(Loc(modId, "roster_transcript", "This Visit"), "transcript_header");
                int start = Mathf.Max(0, record.Transcript.Count - MaxTranscriptRows);
                for (int i = start; i < record.Transcript.Count; i++)
                {
                    KeyValuePair<string, string> line = record.Transcript[i];
                    string who;
                    if (line.Key == "player") who = Loc(modId, "roster_you", "You");
                    else if (line.Key == "credit") who = Loc(modId, "roster_credit", "Credit");
                    else who = Loc(modId, "roster_customer", "Customer");
                    builder.AddLabel(who + "：" + line.Value);
                }
            }
            builder.End();

            builder.Show();
            CustomUIBuilder.RebuildLayout(_detail.ContentRoot);
        }
        catch (Exception ex)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("roster detail failed: " + ex.Message);
        }
    }

    private static string Loc(string modId, string key, string fallback)
    {
        if (string.IsNullOrEmpty(modId)) return fallback;
        string value = ModHelper.GetLocalized(modId, key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }
}
