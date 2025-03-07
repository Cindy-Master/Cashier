﻿using Cashier.Addons;
using Cashier.Commons;
using Cashier.Models;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using ECommons;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Diagnostics;
using Chat = Cashier.Commons.Chat;

namespace Cashier
{
    public unsafe sealed class Cashier : IDalamudPlugin
    {
        public static TaskManager? TaskManager { get; private set; }
        public static string PluginName { get; } = "Cashier";
        public static string Name { get; } = "Cashier";
        private const string commandName = "/ca";
        public static Cashier? Instance { get; private set; }
        public PluginUI PluginUi { get; init; }
        public Configuration Config { get; init; }
        public DalamudPluginInterface PluginInterface { get; init; }
        public uint homeWorldId = 0;
        public HookHelper HookHelper { get; init; }

        public Cashier(DalamudPluginInterface pluginInterface)
        {
            Instance = this;
            PluginInterface = pluginInterface;

            Svc.Initialize(pluginInterface);
            Config = PluginInterface.GetPluginConfig() as Configuration ?? new();
            Config.Initialize(PluginInterface);

            HookHelper = new(this);
            PluginUi = new(this);

            Svc.CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "/ca 打开主窗口\n/ca config|cfg 打开设置窗口\n/ca history 打开历史记录"
            });

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenMainUi += UiBuilder_OpenMainUi;
            PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;

            Svc.ClientState.Login += OnLogin;
            Svc.ClientState.Logout += OnLogout;
            homeWorldId = Svc.ClientState.LocalPlayer?.HomeWorld.Id ?? homeWorldId;

            ECommonsMain.Init(pluginInterface, this);
            TaskManager = new();
        }

        public void Dispose()
        {
            HookHelper.Dispose();
            TaskManager!.Abort();
            ECommonsMain.Dispose();

            Svc.ClientState.Login -= OnLogin;
            Svc.ClientState.Logout -= OnLogout;
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenMainUi -= UiBuilder_OpenMainUi;
            PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;
            Svc.CommandManager.RemoveHandler(commandName);
            PluginUi.Dispose();
        }

        private unsafe void OnCommand(string command, string args)
        {
            string arg = args.Trim().Replace("\"", string.Empty);
            if (string.IsNullOrEmpty(arg)) {
                PluginUi.Main.Show();
            } else if (arg == "cfg" || arg == "config") {
                PluginUi.Setting.Show();
            } else if (arg == "history") {
                PluginUi.History.Show();
            }

#if DEBUG
            else if (arg == "t") {
                var id = TargetSystem.Instance()->Target;
                Commons.Chat.PrintLog($"ID:{id->ObjectID:X}||{(nint)id:X}");
            } else if (arg == "tt") {
            } else if (arg.StartsWith("money")) {
                if (!uint.TryParse(arg[5..].Trim(), out uint result)) {
                    Commons.Chat.PrintLog("money parse error" + arg);
                    return;
                }
                AddonTradeHelper.SetGil(result);
            } else if (arg == "cancel") {
                AddonTradeHelper.Cancel();
            } else if (arg.StartsWith("trade name")) {
                AddonTradeHelper.RequestTrade(arg[10..].Trim());
            } else if (arg == "test") {
                Commons.Chat.PrintLog("服务器id:" + homeWorldId);
            }
#endif
        }

        private void DrawUI()
        {
            PluginUi?.Draw();
        }

        private void UiBuilder_OpenMainUi()
        {
            PluginUi.Main.Show();
        }
        private void DrawConfigUI()
        {
            PluginUi.Setting.Show();
        }

        private void OnLogin()
        {
            homeWorldId = Svc.ClientState.LocalPlayer?.HomeWorld.Id ?? homeWorldId;
        }

        private void OnLogout()
        {
            homeWorldId = 0;
        }
    }
}
