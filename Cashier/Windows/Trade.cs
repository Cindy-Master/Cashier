﻿using Cashier.Commons;
using Cashier.Model;
using Cashier.Models;
using Cashier.Universalis;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Internal;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Timers;

namespace Cashier.Windows;
public unsafe class Trade
{
    private Cashier _cashier { get; init; }
    private int[] _position = new int[2];
    private bool _onceVisible = true;
    /// <summary>
    /// 窗口大小
    /// </summary>
    private const int Width = 540, Height = 560;
    /// <summary>
    /// 显示价格的颜色，RBGA
    /// 绿色为设定HQ但交易NQ；黄色为设定NQ但交易HQ
    /// </summary>
    private readonly static Vector4[] Color = [new(1, 1, 1, 1), new(0, 1, 0, 1), new(1, 1, 0, 1)];
    private readonly static string[] ColumnName = ["", "物品", "数量", "预期", "最低价"];
    private readonly static float[] ColumnWidth = [26, -1, 42, 80, 80];
    private const int RowWidth = 30;
    private readonly static Vector2 ImageSize = new(26, 26);
    private readonly Lazy<IDalamudTextureWrap?> GilImage = new(PluginUI.GetIcon(65002));
    private readonly Timer _refreshTimer = new(100) { AutoReset = true };

    /// <summary>
    /// 是否交易中
    /// </summary>
    public bool IsTrading { get; private set; } = false;
    /// <summary>
    /// 交易物品记录，0自己，1对面
    /// </summary>
    private TradeItem[][] _tradeItemList = new TradeItem[2][];
    private bool[] _tradePlayerConfirm = new bool[2];
    /// <summary>
    /// 交易金币记录，0自己，1对面
    /// </summary>
    private uint[] _tradeGil = [0, 0];
    /// <summary>
    /// 连续交易物品记录，Key itemId，Value (name, nq, hq, stackSize)
    /// </summary>
    private Dictionary<uint, RecordItem>[] multiItemList = [new(), new()];
    private class RecordItem
    {
        public uint Id { get; private init; }
        public string Name { get; private init; }
        public uint NqCount { get; set; } = 0;
        public uint HqCount { get; set; } = 0;
        public uint StackSize { get; private init; }
        public RecordItem(uint id, string? name, uint stackSize)
        {
            Id = id;
            Name = name ?? "???";
            StackSize = stackSize;
        }
    }
    private uint[] multiGil = [0, 0];
    private uint worldId = 0;
    /// <summary>
    /// 交易目标
    /// </summary>
    public TradeTarget Target { get; private set; } = new();
    /// <summary>
    /// 上次交易目标，用于判断是否连续交易
    /// </summary>
    private TradeTarget LastTarget = new();
    private readonly nint agentTradePtr;
    private AtkUnitBase* addonTrade;

    #region Init
    private DalamudLinkPayload _payload { get; init; }
    private Configuration Config => _cashier.Config;
    public Trade(Cashier cashier)
    {
        _cashier = cashier;
        _payload = cashier.PluginInterface.AddChatLinkHandler(0, OnTradeTargetClick);

        agentTradePtr = (nint)AgentModule.Instance()->GetAgentByInternalId(AgentId.Trade);
        _refreshTimer.Elapsed += (_, __) => RefreshData();

        _cashier.HookHelper.OnSetTradeTarget += SetTradeTarget;
        _cashier.HookHelper.OnTradeBegined += OnTradeBegined;
        _cashier.HookHelper.OnTradeFinished += OnTradeFinished;
        _cashier.HookHelper.OnTradeCanceled += OnTradeCancelled;
        _cashier.HookHelper.OnTradeConfirmChanged -= OnTradeConfirmChanged;
        _cashier.HookHelper.OnTradeFinalCheck += OnTradeFinalChecked;
        _cashier.HookHelper.OnTradeMoneyChanged += OnTradeMoneyChanged;
        _cashier.HookHelper.OnSetTradeItemSlot += OnSetTradeSlotItem;
        _cashier.HookHelper.OnClearTradeItemSlot += OnClearTradeSlotItem;
    }

    public void Dispose()
    {
        _cashier.HookHelper.OnSetTradeTarget -= SetTradeTarget;
        _cashier.HookHelper.OnTradeBegined -= OnTradeBegined;
        _cashier.HookHelper.OnTradeFinished -= OnTradeFinished;
        _cashier.HookHelper.OnTradeCanceled -= OnTradeCancelled;
        _cashier.HookHelper.OnTradeConfirmChanged -= OnTradeConfirmChanged;
        _cashier.HookHelper.OnTradeFinalCheck -= OnTradeFinalChecked;
        _cashier.HookHelper.OnTradeMoneyChanged -= OnTradeMoneyChanged;
        _cashier.HookHelper.OnSetTradeItemSlot -= OnSetTradeSlotItem;
        _cashier.HookHelper.OnClearTradeItemSlot -= OnClearTradeSlotItem;

        _cashier.PluginInterface.RemoveChatLinkHandler(0);
        _refreshTimer.Dispose();
    }
    #endregion

    #region 绘制窗口

    public unsafe void Draw()
    {
        if (!Config.ShowTradeWindow || !IsTrading || !_onceVisible) {
            return;
        }
        if (_position[0] == int.MinValue) {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Trade", out var addonPtr) && addonPtr->UldManager.LoadedState == AtkLoadState.Loaded) {
                _position[0] = addonPtr->X - Width - 5;
                _position[1] = addonPtr->Y + 2;
            } else {
                return;
            }
        }
        ImGui.SetNextWindowSize(new Vector2(Width, Height), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(new Vector2(_position[0], _position[1]), ImGuiCond.Once);
        if (ImGui.Begin("玩家交易", ref _onceVisible, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar)) {
            ImGui.TextUnformatted("<--");

            ImGui.SameLine(ImGui.GetColumnWidth() - 90);
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered()) {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted("预期金额：");
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextColored(Color[0], "左键单击复制到剪贴板");

                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextColored(Color[0], "“---”为未设定预期金额");

                //绿色为设定HQ但交易NQ
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextColored(Color[1], "设定了HQ的预期金额，但交易的是NQ物品");
                //黄色为设定NQ但交易HQ
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextColored(Color[2], "设定了NQ的预期金额，但交易的是HQ物品");

                ImGui.EndTooltip();
            }
            ImGui.AlignTextToFramePadding();

            // 显示当前交易对象的记录
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.History)) {
                _cashier.PluginUi.History.Show(Target.PlayerName + "@" + Target.WorldName);
            }
            if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip("显示当前交易对象的交易记录");
            }

            // 显示设置窗口
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog)) {
                _cashier.PluginUi.Setting.Show();
            }

            DrawTradeTable(_tradeItemList[0], _tradeGil[0]);
            ImGui.Spacing();

            ImGui.TextUnformatted($"{Target.PlayerName} @ {Target.WorldName} -->");
            DrawTradeTable(_tradeItemList[1], _tradeGil[1]);
            ImGui.End();
        }
    }

    /// <summary>
    /// 绘制交易道具表
    /// </summary>
    /// <param name="items"></param>
    /// <param name="gil"></param>
    private void DrawTradeTable(TradeItem[] items, uint gil)
    {
        if (ImGui.BeginTable("交易栏", ColumnName.Length, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV)) {

            for (int i = 0; i < ColumnName.Length; i++) {
                if (ColumnWidth.Length > i) {
                    if (ColumnWidth[i] >= 0) {
                        ImGui.TableSetupColumn(ColumnName[i], ImGuiTableColumnFlags.WidthFixed, ColumnWidth[i]);
                    } else {
                        ImGui.TableSetupColumn(ColumnName[i], ImGuiTableColumnFlags.WidthStretch);
                    }
                }
            }
            ImGui.TableHeadersRow();
            for (int i = 0; i < items.Length; i++) {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, RowWidth);
                ImGui.TableNextColumn();

                if (items[i].Id == 0) {
                    continue;
                }
                var icon = PluginUI.GetIcon(items[i].IconId, items[i].Quality);
                if (icon != null) {
                    ImGui.Image(icon.ImGuiHandle, ImageSize);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(items[i].Name + (items[i].Quality ? SeIconChar.HighQuality.ToIconString() : string.Empty));

                var itemPreset = items[i].ItemPreset;
                if (ImGui.IsItemHovered() && itemPreset != null) {
                    ImGui.SetTooltip($"预设：{itemPreset.GetPresetString()}");
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Convert.ToString(items[i].Count));

                ImGui.TableNextColumn();
                if (itemPreset == null) {
                    ImGui.TextDisabled("---");
                } else {
                    var presetType = items[i].Quality == itemPreset.Quality ? 0 : Convert.ToInt32(items[i].Quality) << 1 + Convert.ToInt32(itemPreset.Quality);
                    if (items[i].Count == items[i].StackSize && itemPreset.StackPrice != 0) {
                        items[i].PresetPrice = itemPreset.StackPrice;
                        ImGui.TextColored(Color[presetType], $"{items[i].PresetPrice:#,0}");
                    } else if (itemPreset.SetCount != 0 && itemPreset.SetPrice != 0) {
                        items[i].PresetPrice = 1.0f * items[i].Count / itemPreset.SetCount * itemPreset.SetPrice;
                        ImGui.TextColored(Color[presetType], $"{items[i].PresetPrice:#,0}");
                    } else {
                        items[i].PresetPrice = 0;
                        ImGui.TextDisabled("---");
                    }
                    if (ImGui.IsItemClicked()) {
                        ImGui.SetClipboardText($"{items[i].PresetPrice:#,0}");
                    }
                }
                // 显示大区最低价格
                ImGui.TableNextColumn();

                if (!items[i].Quality) {
                    // NQ能够接受HQ价格
                    var nq = items[i].ItemPrice.GetMinPrice(worldId).Item1;
                    var hq = items[i].ItemPrice.GetMinPrice(worldId).Item2;
                    if (nq == 0) {
                        items[i].MinPrice = hq;
                    } else if (hq == 0) {
                        items[i].MinPrice = nq;
                    } else {
                        items[i].MinPrice = Math.Min(nq, hq);
                    }
                } else {
                    items[i].MinPrice = items[i].ItemPrice.GetMinPrice(worldId).Item2;
                }
                if (items[i].MinPrice > 0) {
                    ImGui.TextUnformatted(items[i].MinPrice.ToString("#,0"));
                    if (ImGui.IsItemHovered()) {
                        ImGui.SetTooltip($"NQ: {items[i].ItemPrice.GetMinPrice(worldId).Item1:#,0}\nHQ: {items[i].ItemPrice.GetMinPrice(worldId).Item2:#,0}\nWorld: {items[i].ItemPrice.GetMinPrice(worldId).Item3}\nTime: " + DateTimeOffset.FromUnixTimeMilliseconds(items[i].ItemPrice?.GetMinPrice(worldId).Item4 ?? 0).LocalDateTime.ToString(Price.format));
                    }
                } else {
                    ImGui.TextDisabled("---");
                    if (ImGui.IsItemHovered() && items[i].ItemPrice.GetMinPrice(worldId).Item3.Length > 0) {
                        ImGui.SetTooltip(items[i].ItemPrice.GetMinPrice(worldId).Item3);
                    }
                }
            }

            ImGui.TableNextRow(ImGuiTableRowFlags.None, RowWidth);
            ImGui.TableNextColumn();

            if (GilImage != null) {
                ImGui.Image(GilImage.Value.ImGuiHandle, ImageSize);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{gil:#,0}");

            float sum = 0;
            uint min = gil;
            foreach (var item in items) {
                if (item.MinPrice > 0) {
                    min += (uint)item.MinPrice * item.Count;
                }
            }

            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            foreach (var item in items) {
                sum += item.PresetPrice;
            }

            sum += gil;
            ImGui.TextUnformatted($"{sum:#,0}");
            if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip($"包含金币在内的全部金额，单击复制：{sum:#,0}");
            }
            if (ImGui.IsItemClicked()) {
                ImGui.SetClipboardText($"{sum:#,0}");
            }

            ImGui.TableNextColumn();

            ImGui.TextUnformatted($"{min:#,0}");
            if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip($"以最低价计算的金额，单击复制：{min:#,0}");
            }
            if (ImGui.IsItemClicked()) {
                ImGui.SetClipboardText($"{min:#,0}");
            }

            ImGui.EndTable();
        }
    }

    #endregion

    /// <summary>
    /// 交易结算
    /// </summary>
    /// <param name="status">交易状态</param>
    private void Finish(bool status)
    {
        uint[] gil = [_tradeGil[0], _tradeGil[1]];
        TradeItem[][] list = [
            _tradeItemList[0].Where(i => i.Id != 0).ToArray(),
                _tradeItemList[1].Where(i => i.Id != 0).ToArray()
        ];

        _cashier.PluginUi.History.AddHistory(status, $"{Target.PlayerName}@{Target.WorldName}", gil, list);

        if (LastTarget != Target) {
            multiGil = [0, 0];
            multiItemList = [new(), new()];
        }
        // 如果交易成功，将内容累积进数组
        if (status) {
            multiGil[0] += _tradeGil[0];
            multiGil[1] += _tradeGil[1];
            foreach (TradeItem item in list[0]) {
                RecordItem rec;
                if (multiItemList[0].ContainsKey(item.Id)) {
                    rec = multiItemList[0][item.Id];
                } else {
                    rec = new(item.Id, item.Name, item.StackSize);
                }
                if (item.Quality) {
                    rec.HqCount += item.Count;
                } else {
                    rec.NqCount += item.Count;
                }
                multiItemList[0][item.Id] = rec;
            }
            foreach (TradeItem item in list[1]) {
                RecordItem rec;
                if (multiItemList[1].ContainsKey(item.Id)) {
                    rec = multiItemList[1][item.Id];
                } else {
                    rec = new(item.Id, item.Name, item.StackSize);
                }
                if (item.Quality) {
                    rec.HqCount += item.Count;
                } else {
                    rec.NqCount += item.Count;
                }
                multiItemList[1][item.Id] = rec;
            }
        }
        if (LastTarget == Target) {
            Svc.ChatGui.Print(BuildMultiTradeSeString(_payload, status, Target, list, gil, multiItemList, multiGil).BuiltString);
        } else {
            Svc.ChatGui.Print(BuildTradeSeString(_payload, status, Target, list, gil).BuiltString);
        }
        if (status) {
            LastTarget = Target;
        }
    }

    /// <summary>
    /// 重置变量
    /// </summary>
    private unsafe void Reset()
    {
        _tradeItemList = [[new(), new(), new(), new(), new()], [new(), new(), new(), new(), new()]];
        _tradeGil = new uint[2];
        _tradePlayerConfirm = new bool[2];
        _onceVisible = true;
        _position = [int.MinValue, int.MinValue];
        worldId = Svc.ClientState.LocalPlayer?.HomeWorld.Id ?? 0;
    }

    #region 构建输出

    /// <summary>
    /// 输出单次交易的内容
    /// </summary>
    /// <param name="status">交易状态</param>
    /// <param name="target">交易目标</param>
    /// <param name="items">交易物品</param>
    /// <param name="gil">交易金币</param>
    /// <returns></returns>
    private static SeStringBuilder BuildTradeSeString(DalamudLinkPayload payload, bool status, TradeTarget target, TradeItem[][] items, uint[] gil)
    {
        if (items.Length == 0 || gil.Length == 0) {
            return new SeStringBuilder().AddText($"[{Cashier.PluginName}]").AddUiForeground("获取交易内容失败", 17);
        }
        var builder = new SeStringBuilder()
            .AddUiForeground($"[{Cashier.PluginName}]", 45)
            .AddText(SeIconChar.ArrowRight.ToIconString())
            .Add(payload)
            .AddUiForeground(1)
            .Add(new PlayerPayload(target.PlayerName!, (uint)target.WorldId!))
            .AddUiForegroundOff();
        if (target.WorldId != Svc.ClientState.LocalPlayer?.HomeWorld.Id) {
            builder.Add(new IconPayload(BitmapFontIcon.CrossWorld))
                .AddText(target.WorldName!);
        }
        builder.Add(RawPayload.LinkTerminator);
        if (!status) {
            builder.AddUiForeground(" (取消)", 62);
        }
        // 获得
        if (gil[0] != 0 || items[0].Length != 0) {
            builder.Add(new NewLinePayload());
            builder.AddText("<<==  ");
            if (gil[0] != 0) {
                builder.AddText($"{gil[0]:#,0}{(char)SeIconChar.Gil}");
            }
            for (int i = 0; i < items[0].Length; i++) {
                if (i != 0 || gil[0] != 0) {
                    builder.AddText(", ");
                }
                var name = items[0][i].Name + (items[0][i].Quality ? SeIconChar.HighQuality.ToIconString() : string.Empty) + "x" + items[0][i].Count;
                builder.AddItemLink(items[0][i].Id, items[0][i].Quality, name)
                    .Add(RawPayload.LinkTerminator);
            }
        }

        // 支付
        if (gil[1] != 0 || items[1].Length != 0) {
            builder.Add(new NewLinePayload());
            builder.AddText("==>>  ");
            if (gil[1] != 0) {
                builder.AddText($"{gil[1]:#,0}{(char)SeIconChar.Gil}");
            }
            for (int i = 0; i < items[1].Length; i++) {
                if (i != 0 || gil[1] != 0) {
                    builder.AddText(", ");
                }
                var name = items[1][i].Name + (items[1][i].Quality ? SeIconChar.HighQuality.ToIconString() : string.Empty) + "x" + items[1][i].Count;
                builder.AddItemLink(items[1][i].Id, items[1][i].Quality, name)
                    .Add(RawPayload.LinkTerminator);
            }
        }
        return builder;
    }

    /// <summary>
    /// 输出多次交易内容
    /// </summary>
    /// <param name="status">交易状态</param>
    /// <param name="target">交易目标</param>
    /// <param name="items">交易物品</param>
    /// <param name="gil">交易金币</param>
    /// <param name="multiItems">累积交易物品</param>
    /// <param name="multiGil">累积交易金币</param>
    /// <returns></returns>
    private static SeStringBuilder BuildMultiTradeSeString(DalamudLinkPayload payload, bool status, TradeTarget target, TradeItem[][] items, uint[] gil, Dictionary<uint, RecordItem>[] multiItems, uint[] multiGil)
    {
        if (items.Length == 0 || gil.Length != 2 || multiItems.Length == 0 || multiGil.Length != 2) {
            return new SeStringBuilder()
                .AddText($"[{Cashier.PluginName}]")
                .AddUiForeground("获取交易内容失败", 17);
        }
        var builder = BuildTradeSeString(payload, status, target, items, gil);
        builder.Add(new NewLinePayload()).AddText("连续交易:");
        // 如果金币和物品都没有，则略过该行为，不输出
        // 获得
        if (multiGil[0] != 0 || multiItems[0].Count != 0) {
            builder.Add(new NewLinePayload()).AddText("<<==  ");
            if (multiGil[0] != 0) {
                builder.AddText($"{multiGil[0]:#,0}{(char)SeIconChar.Gil}");
            }

            foreach (var itemId in multiItems[0].Keys) {
                var item = multiItems[0][itemId];

                builder.AddItemLink(itemId, item.NqCount == 0);
                builder.AddUiForeground(SeIconChar.LinkMarker.ToIconString(), 500);
                builder.AddUiForeground(item.Name, 1);

                var nqStr = item.StackSize > 1 && item.NqCount >= item.StackSize ? (item.NqCount / item.StackSize + "组" + item.NqCount % item.StackSize) : item.NqCount.ToString("#,0");
                var hqStr = item.StackSize > 1 && item.HqCount >= item.StackSize ? (item.HqCount / item.StackSize + "组" + item.HqCount % item.StackSize) : item.HqCount.ToString("#,0");
                if (nqStr.EndsWith("组0")) {
                    nqStr = nqStr[..^1];
                }
                if (hqStr.EndsWith("组0")) {
                    hqStr = hqStr[..^1];
                }

                if (item.HqCount == 0) {
                    builder.AddUiForeground($"<{nqStr}>", 1);
                } else if (item.NqCount == 0) {
                    builder.AddUiForeground($"<{SeIconChar.HighQuality.ToIconString()}{hqStr}>", 1);
                } else {
                    builder.AddUiForeground($"<{nqStr}/{SeIconChar.HighQuality.ToIconString()}{hqStr}>", 1);
                }
                builder.Add(RawPayload.LinkTerminator);
            }
        }
        // 支付
        if (multiGil[1] != 0 || multiItems[1].Count != 0) {
            builder.Add(new NewLinePayload()).AddText("==>>  ");
            if (multiGil[1] != 0) { builder.AddText($"{multiGil[1]:#,0}{(char)SeIconChar.Gil}"); }

            foreach (var itemId in multiItems[1].Keys) {
                var item = multiItems[1][itemId];

                builder.AddItemLink(itemId, item.NqCount == 0);
                builder.AddUiForeground(SeIconChar.LinkMarker.ToIconString(), 500);
                builder.AddUiForeground(item.Name, 1);

                var nqStr = item.StackSize > 1 && item.NqCount >= item.StackSize ? (item.NqCount / item.StackSize + "组" + item.NqCount % item.StackSize) : item.NqCount.ToString("#,0");
                var hqStr = item.StackSize > 1 && item.HqCount >= item.StackSize ? (item.HqCount / item.StackSize + "组" + item.HqCount % item.StackSize) : item.HqCount.ToString("#,0");
                if (nqStr.EndsWith("组0")) {
                    nqStr = nqStr[..^1];
                }
                if (hqStr.EndsWith("组0")) {
                    hqStr = hqStr[..^1];
                }

                if (item.HqCount == 0) {
                    builder.AddUiForeground($"<{nqStr}>", 1);
                } else if (item.NqCount == 0) {
                    builder.AddUiForeground($"<{SeIconChar.HighQuality.ToIconString()} {hqStr}>", 1);
                } else {
                    builder.AddUiForeground($"<{nqStr}/{SeIconChar.HighQuality.ToIconString()} {hqStr}>", 1);
                }
                builder.Add(RawPayload.LinkTerminator);
            }
        }
        return builder;
    }

    #endregion

    /// <summary>
    /// 点击输出内容中的交易对象
    /// </summary>
    /// <param name="commandId"></param>
    /// <param name="str"></param>
    public void OnTradeTargetClick(uint commandId, SeString str)
    {
        PlayerPayload? payload = (PlayerPayload?)str.Payloads.Find(i => i.Type == PayloadType.Player);
        if (payload != null) {
            _cashier.PluginUi.History.Show(payload.PlayerName + "@" + payload.World.Name.RawString);
        } else {
            Commons.Chat.PrintError("未找到交易对象");
            Svc.PluginLog.Verbose($"未找到交易对象，data=[{str.ToJson()}]");
        }
    }

    private void OnTradeBegined()
    {
        if (IsTrading) {
            // 上次交易未结束
            Svc.PluginLog.Warning("上次交易未结束时开始交易");
            return;
        }

        Svc.PluginLog.Debug("交易开始");
        IsTrading = true;
        _refreshTimer.Start();
        Reset();

        if (addonTrade == default && GenericHelpers.TryGetAddonByName<AtkUnitBase>("Trade", out var addonPtr)) {
            addonTrade = addonPtr;
        }
    }

    private void OnTradeCancelled()
    {
        if (!IsTrading) {
            // 未开始交易
            Svc.PluginLog.Warning("未开始交易时交易被取消");
            return;
        }
        IsTrading = false;
        _refreshTimer.Stop();
        Finish(false);
    }

    private void OnTradeFinished()
    {
        if (!IsTrading) {
            // 未开始交易
            Svc.PluginLog.Warning("未开始交易时交易完成");
            return;
        }
        IsTrading = false;
        _refreshTimer.Stop();
        _cashier.PluginUi.Main.OnTradeFinished(Target.ObjectId, _tradeGil[0]);
        Finish(true);
    }

    private void OnTradeFinalChecked()
    {
        if (!IsTrading) {
            // 未开始交易
            Svc.PluginLog.Warning("未开始交易时进入最终确认");
            return;
        }
        _cashier.PluginUi.Main.OnTradeFinalChecked(Target.ObjectId, _tradeGil[0]);
    }


    /// <summary>
    /// 设置交易目标
    /// </summary>
    /// <param name="objectId"></param>
    private void SetTradeTarget(nint objectId)
    {
        var player = Svc.ObjectTable.FirstOrDefault(i => i.ObjectId == objectId) as PlayerCharacter;
        if (player != null) {
            if (player.ObjectId != Svc.ClientState.LocalPlayer?.ObjectId) {
                var world = Svc.DataManager.GetExcelSheet<World>()?.FirstOrDefault(r => r.RowId == player.HomeWorld.Id);
                Target = new(player.HomeWorld.Id, world?.Name ?? "???", (uint)objectId, player.Name.TextValue);
            }
        } else {
            Svc.PluginLog.Error($"找不到交易对象，id: {objectId:X}");
            Target = new();
        }
    }

    public void RefreshData()
    {
        uint[] AddonIndex = [8, 9, 10, 11, 12, 19, 20, 21, 22, 23];
        int getCount(uint nodeId)
        {
            var imageNode = addonTrade->GetNodeById(nodeId)->GetAsAtkComponentNode()->Component->UldManager.NodeList[2]->GetAsAtkComponentNode();
            if (!imageNode->AtkResNode.IsVisible) {
                return -1;
            }

            var countTextNode = imageNode->Component->UldManager.NodeList[6]->GetAsAtkTextNode();
            return int.TryParse(countTextNode->NodeText.ToString(), out int result) ? result : -1;
        }
        for (int i = 0; i < 10; i++) {
            if (_tradeItemList[i < 5 ? 0 : 1][i % 5] is TradeItem { Id: > 0 } item) {
                item.Count = (uint)getCount(AddonIndex[i]);
            }
        }
    }

    /// <summary>
    /// 交易物品槽设置物品id
    /// </summary>
    /// <param name="a1"></param>
    /// <param name="itemId"></param>
    private void OnSetTradeSlotItem(nint a1, int itemId)
    {
        var index = (a1 - agentTradePtr - 48) / 136;
        if (index < 0 || index > 9) {
            return;
        }
        _tradeItemList[index < 5 ? 0 : 1][index % 5] = new((uint)itemId % 1000000, 1, itemId > 1000001);
    }

    /// <summary>
    /// 物品槽格子被清空
    /// </summary>
    /// <param name="a1"></param>
    private void OnClearTradeSlotItem(nint a1)
    {
        if (!IsTrading) {
            return;
        }
        // 这个函数每帧每格子都被调用，看看有无优化
        var index = (a1 - agentTradePtr - 48) / 136;
        if (index < 0 || index > 9) {
            return;
        }

        if (Marshal.ReadInt32(a1 + 0x70) == 0) {
            return;
        }
        _tradeItemList[index < 5 ? 0 : 1][index % 5] = new();
    }

    /// <summary>
    /// 设置交易金币
    /// </summary>
    /// <param name="money">金币</param>
    /// <param name="isPlayer1">是否为玩家1(自己)</param>
    private void OnTradeMoneyChanged(uint money, bool isPlayer1)
    {
        _tradeGil[isPlayer1 ? 0 : 1] = money;
    }

    /// <summary>
    /// 某方确认交易条件
    /// </summary>
    /// <param name="isPlayer1">是否为玩家1(自己)</param>
    private void OnTradeConfirmChanged(nint objectId, bool confirmed)
    {
        if (objectId == Svc.ClientState.LocalPlayer!.ObjectId) {
            _tradePlayerConfirm[0] = confirmed;
        } else {
            _tradePlayerConfirm[1] = confirmed;
        }
    }
}
