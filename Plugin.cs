using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using AuraCanAI.Dalamud.Core;
using AuraCanAI.Dalamud.Windows;
using Dalamud.Interface.Windowing;

namespace AuraCanAI.Dalamud;

public sealed class Plugin : IDalamudPlugin
{
	[PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
	[PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
	[PluginService] internal static IClientState ClientState { get; private set; } = null!;
	[PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
	[PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
	[PluginService] internal static IFramework Framework { get; private set; } = null!;
	[PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
	[PluginService] internal static IPluginLog Log { get; private set; } = null!;
	[PluginService] internal static IDataManager DataManager { get; private set; } = null!;
	[PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
	[PluginService] internal static ICondition Condition { get; private set; } = null!;
	[PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
	[PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
	[PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;

	private const string CommandName = "/aca";

	public Configuration Configuration { get; init; }
	public static AuraCanAiCore? AuraCore { get; private set; }

	public readonly WindowSystem WindowSystem = new("AuraCanAI");
	private MainWindow MainWindow { get; init; }
	private DashboardWindow Dashboard { get; init; }

	public Plugin()
	{
		Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
		// 迁移:社交距离默认 3 → 1(2026-09-05;存量配置仍存 3,首次加载时改)
		if (Configuration.MovementStopDistance == 3f)
		{
			Configuration.MovementStopDistance = 1f;
			try { Configuration.Save(PluginInterface); } catch { }
		}
		// 坐法指令默认改 /sit(实机确认;旧 /interact 存量迁移)
		if (Configuration.SeatSitCommand == "/interact")
		{
			Configuration.SeatSitCommand = "/sit";
			try { Configuration.Save(PluginInterface); } catch { }
		}
		// 站距默认迁移 → 0(实机:走到记录点正上方再 /sit 最正)
		if (Configuration.SeatApproachDistance is 1.0f or 0.45f or 0.1f)
		{
			Configuration.SeatApproachDistance = 0f;
			try { Configuration.Save(PluginInterface); } catch { }
		}
		// 卡住判定收紧:3 → 0.6s(0.5s 没动即卡)
		if (Configuration.MovementStuckTimeoutSec == 3f)
		{
			Configuration.MovementStuckTimeoutSec = 0.6f;
			try { Configuration.Save(PluginInterface); } catch { }
		}
		AuraCore = new AuraCanAiCore(PluginInterface, Configuration, ChatGui, ClientState, PlayerState, ObjectTable, Framework, Log, DataManager, Condition, SigScanner, GameInterop, GameConfig);

		MainWindow = new MainWindow(this);
		Dashboard = new DashboardWindow(this);
		WindowSystem.AddWindow(MainWindow);
		WindowSystem.AddWindow(Dashboard);

		CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
		{
			HelpMessage = "打开 AuraCanAI 主面板;子指令: /aca list 附近玩家 | /aca map [名字] 活点地图 | /aca note 查看/编辑当前选中玩家的备注 | /aca search [名字] 回忆检索 | /aca look [名字] 看向最后看你的人 | /aca behavior 行为设置 | /aca music 演奏(MIDI) | /aca setting 设置面板 | /aca macro N 触发宏(sN 共享宏;失败时自动附诊断) | /aca macrodia [N] 宏子模块诊断 | /aca action [名称] 执行当前角色的自定义动作(不带名称=列出) | 状态机: /aca smreset 重置为默认示例 | 小队: /aca party [leave|invite 名字|accept] | /aca leavescene 测试「离开」(走到人少处/坐下+60秒退队) | 移动: /aca face/approach/follow/leave [名字] | /aca move x y z | /aca stop | 场景: /aca seatadd [可选名字] 记录当前坐点 | /aca seatstand [名字|#id] 校准座前站定点 | /aca seatgo [名字|#id|玩家名|空=最近] 去坐(填玩家名=坐 TA 旁边最近的空座)"
		});

		PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
		PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi; // 「打开」→ 附近玩家面板
		PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi; // 「设置」→ 控制面板

		Log.Information("===AuraCanAI 插件已加载===");
	}

	private void OnCommand(string command, string args)
	{
		var arg = args.Trim();
		// /aca macro N:触发宏列表中第 N 号宏(0-99,游戏有 0 号宏);N 可带 s 前缀表示共享宏
		if (arg.StartsWith("macro", StringComparison.OrdinalIgnoreCase))
		{
			var spec = arg.Length > 5 ? arg[5..].Trim() : "";
			var shared = spec.StartsWith("s", StringComparison.OrdinalIgnoreCase);
			if (shared) spec = spec[1..].Trim();
			if (int.TryParse(spec, out var macroIdx) && macroIdx >= 0 && macroIdx <= 99)
			{
				var ok = TriggerMacro(macroIdx, shared);
				Log?.Information(ok
					? $"已触发宏 {macroIdx}{(shared ? "(共享)" : "")}"
					: $"触发宏 {macroIdx} 失败: {MacroExecutor.Diagnose(macroIdx, shared)}");
			}
			else
			{
				Log?.Warning("/aca macro 参数需为 0-99 的宏编号,如 /aca macro 3;共享宏用 /aca macro s3");
			}
			return;
		}
		// /aca macrodia [N]:宏子模块诊断(指针/锁定状态/宏是否为空;不执行)
		if (arg.StartsWith("macrodia", StringComparison.OrdinalIgnoreCase))
		{
			var spec = arg.Length > 8 ? arg[8..].Trim() : "1";
			var shared = spec.StartsWith("s", StringComparison.OrdinalIgnoreCase);
			if (shared) spec = spec[1..].Trim();
			if (!int.TryParse(spec, out var diagIdx) || diagIdx < 0 || diagIdx > 99)
			{
				Log?.Warning("/aca macrodia [0-99] 或 /aca macrodia s3(共享宏)");
				return;
			}
			Log?.Information($"宏诊断[{diagIdx}{(shared ? "/共享" : "")}]: {MacroExecutor.Diagnose(diagIdx, shared)}");
			return;
		}
		// /aca action [名称]:执行当前角色的自定义动作(带名称=执行;不带=列出;AI 的 rp_emote 工具走同一条路)
		if (arg.StartsWith("action", StringComparison.OrdinalIgnoreCase))
		{
			var core = AuraCore;
			if (core == null) { Log?.Warning("AuraCanAI 核心未就绪"); return; }
			var actionName = arg.Length > 6 ? arg[6..].Trim() : "";
			var roleName = core.GetCurrentRoleName();
			var list = core.GetRoleActions(roleName);
			if (list.Count == 0)
			{
				Log?.Information($"当前角色「{(roleName.Length == 0 ? "未选择" : roleName)}」没有配置动作列表(前端「角色设定 → 角色管理 → 动作列表」添加)");
				return;
			}
			if (actionName.Length == 0)
			{
				Log?.Information($"动作列表({roleName}): " + string.Join(" | ", list.Select(a =>
					RoleActionPlayer.DisplayName(a) + (a.cooldown > 0 ? $"(冷却{a.cooldown}s)" : ""))));
				return;
			}
			Log?.Information(core.RoleActions.PerformByName(roleName, actionName));
			return;
		}
		// /aca look [名字]:看向指定玩家;不带名字 = 看向最后一个看我的人(最后注视你的玩家)
		if (arg.StartsWith("look", StringComparison.OrdinalIgnoreCase))
		{
			var name = arg.Length > 4 ? arg[4..].Trim() : "";
			var core = AuraCore;
			if (core == null) { Log?.Warning("AuraCanAI 核心未就绪"); return; }
			if (name.Length == 0) name = core.GetLastLookUser();
			if (string.IsNullOrEmpty(name))
			{
				Log?.Information("/aca look:还没有人看过你");
				return;
			}
			var ok = Framework.IsInFrameworkUpdateThread
				? core.TryLook(name)
				: Framework.RunOnFrameworkThread(() => core.TryLook(name)).GetAwaiter().GetResult();
			Log?.Information(ok ? $"已选中: {name}" : $"选中失败: {name}(未找到或已离开附近)");
			return;
		}
		// /aca search [名字]:搜索指定名字(优先级最高);无名字时用当前选中玩家
		if (arg.StartsWith("search", StringComparison.OrdinalIgnoreCase))
		{
			var name = arg.Length > 6 ? arg[6..].Trim() : "";
			if (name.Length > 0)
			{
				Dashboard.StartSearch(name);
			}
			else
			{
				var targetName = GetTargetPlayerName();
				if (!string.IsNullOrEmpty(targetName))
					Dashboard.StartSearch(targetName);
				else
					Dashboard.OpenTab(3); // 未选中玩家:仅打开回忆检索页签
			}
			return;
		}
		// 子指令:list=附近玩家, map=活点地图(可带名字查询);无参数=打开面板保持上次页签
		var lower = arg.ToLowerInvariant();
		// ===== 移动:走近/跟随/走开/走到点/面向/停止 =====
		if (lower == "stop")
		{
			if (AuraCore?.Movement != null) AuraCore.Movement.Stop();
			else Log?.Warning("AuraCanAI 核心未就绪");
			return;
		}
		if (lower == "seatadd" || lower.StartsWith("seatadd ", StringComparison.Ordinal))
		{
			// 记录可坐位置:先站到/坐到椅子上,执行本命令记录 坐标+面向
			var who = lower.Length > 7 ? arg[7..].Trim() : "";
			if (AuraCore == null) { Log?.Warning("AuraCanAI 核心未就绪"); return; }
			var msg = AuraCore.AddSeatPoint(who);
			Log?.Information(msg);
			AuraCore.ChatNotice($"[场景设定] {msg}");
			return;
		}
		if (lower == "seatstand" || lower.StartsWith("seatstand ", StringComparison.Ordinal))
		{
			// 校准某座位的座前站定点:站到"自然准备坐的位置"执行(自动面向交给游戏)
			if (AuraCore == null) { Log?.Warning("AuraCanAI 核心未就绪"); return; }
			var sel2 = arg.Length > 9 ? arg[9..].Trim() : "";
			var msg2 = AuraCore.CalibrateSeatApproach(sel2);
			Log?.Information(msg2);
			AuraCore.ChatNotice($"[坐] {msg2}");
			return;
		}
		if (lower == "seatgo" || lower.StartsWith("seatgo ", StringComparison.Ordinal))
		{
			// 坐到记录点:空=离自己最近的空座;#id=调试;名字部分匹配(如 窗边沙发)
			if (AuraCore?.Movement == null) { Log?.Warning("AuraCanAI 核心未就绪"); return; }
			var sel = arg.Length > 6 ? arg[6..].Trim() : "";
			var msg = AuraCore.Movement.SitOnSeat(sel);
			if (msg.Length == 0)
			{
				Log?.Information($"去坐开始: 选择器=\"{sel}\"");
			}
			else
			{
				Log?.Warning($"去坐失败: {msg}");
				AuraCore.ChatNotice($"[坐] {msg}");
			}
			return;
		}
		var moveVerb = lower.Split(' ')[0];
		if (moveVerb is "face" or "approach" or "follow" or "leave" or "move")
		{
			var coreMV = AuraCore;
			if (coreMV?.Movement == null) { Log?.Warning("AuraCanAI 核心未就绪"); return; }
			var name = arg.Length > moveVerb.Length ? arg[moveVerb.Length..].Trim() : "";
			bool ok;
			string tip;
			switch (moveVerb)
			{
				case "face":
					ok = coreMV.Movement.Face(name);
					tip = ok ? $"已面向 {(name.Length > 0 ? name : "最近接触的人")}" : $"面向失败:{name}(未找到或未登录)";
					break;
				case "approach":
					ok = coreMV.Movement.Approach(name);
					tip = ok ? $"开始走向 {name}" : $"无法走向 {name}(已有移动/目标不存在/状态不允许)";
					break;
				case "follow":
					ok = coreMV.Movement.Follow(name);
					tip = ok ? $"开始跟随 {name}" : $"无法跟随 {name}";
					break;
				case "leave":
					ok = coreMV.Movement.Leave(name);
					tip = ok ? $"开始走开" : $"无法走开";
					break;
				default: // move
				{
					// /aca move x y z → 走到坐标;否则视为玩家名(走近)
					var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length == 3 && float.TryParse(parts[0], out var x) && float.TryParse(parts[1], out var y) && float.TryParse(parts[2], out var z))
					{
						ok = coreMV.Movement.MoveToPoint(new System.Numerics.Vector3(x, y, z));
						tip = ok ? $"开始走到点 ({x:F1},{y:F1},{z:F1})" : "无法走到该点";
					}
					else
					{
						ok = coreMV.Movement.Approach(name);
						tip = ok ? $"开始走向 {name}" : $"无法走向 {name}(或坐标格式应为: move X Y Z)";
					}
					break;
				}
			}
			Log?.Information($"移动指令 {moveVerb}: {(ok ? "成功" : "失败")} | {tip}");
			if (!ok) coreMV.ChatNotice($"[移动] {tip}");
			return;
		}
		// /aca smreset:把状态机重置为默认示例(单套「白屿涟音」)
		if (lower == "smreset" || lower == "statereset")
		{
			var coreSm = AuraCore;
			if (coreSm == null) { Log?.Warning("AuraCanAI 核心未就绪"); return; }
			var msg = coreSm.ResetStateMachine();
			Log?.Information(msg);
			coreSm.ChatNotice($"[状态机] {msg}");
			return;
		}
		// /aca note:查看/编辑当前选中玩家的备注(查询与编辑同一 UI)
		if (lower == "note")
		{
			var coreNote = AuraCore;
			if (coreNote == null) { Log?.Warning("AuraCanAI 核心未就绪"); return; }
			var info = coreNote.GetTargetPlayerInfo();
			if (info == null)
			{
				Log?.Warning("/aca note:请先选中一个玩家(且不能是自己)");
				Dashboard.OpenTab(2); // 未选中玩家:仅打开玩家备注页签
			}
			else
			{
				Dashboard.OpenNoteEditor(coreNote, info.Value.contentId, info.Value.nameWorld);
			}
			return;
		}
		// /aca party:输出小队状态调试信息(排查 in_party 条件用)
		//   /aca party leave = 立刻退出小队;/aca party invite 名字 = 邀请组队;/aca party accept = 接受邀请
		if (lower == "party" || lower.StartsWith("party ", StringComparison.Ordinal))
		{
			var coreP = AuraCore;
			if (coreP == null) { Log?.Warning("AuraCanAI 核心未就绪"); return; }
			var sub = arg.Length > 5 ? arg[5..].Trim() : "";
			if (sub.Length == 0)
			{
				Log?.Information($"小队状态: {coreP.DebugPartyInfo()}");
				return;
			}
			if (sub.Equals("leave", StringComparison.OrdinalIgnoreCase))
			{
				Log?.Information(coreP.PartyAction("leave", ""));
				return;
			}
			if (sub.Equals("accept", StringComparison.OrdinalIgnoreCase))
			{
				Log?.Information(coreP.PartyAction("accept", ""));
				return;
			}
			if (sub.StartsWith("invite", StringComparison.OrdinalIgnoreCase))
			{
				var who = sub.Length > 6 ? sub[6..].Trim() : "";
				if (who.Length == 0) { Log?.Warning("用法: /aca party invite 玩家名"); return; }
				Log?.Information(coreP.PartyAction("invite", who));
				return;
			}
			Log?.Warning("用法: /aca party | /aca party leave | /aca party invite 玩家名 | /aca party accept");
			return;
		}
		// /aca leavescene:「离开」流程测试(走到人少的地方/坐下 + 静默 60 秒退队倒计时)
		if (lower == "leavescene")
		{
			var coreL = AuraCore;
			if (coreL == null) { Log?.Warning("AuraCanAI 核心未就绪"); return; }
			Log?.Information($"离开: {coreL.LeaveScene()}");
			return;
		}
		if (lower == "list")
		{
			Dashboard.OpenTab(0);
		}
		else if (lower == "map")
		{
			Dashboard.OpenTab(1);
		}
		else if (lower.StartsWith("map ", StringComparison.Ordinal))
		{
			// /aca map xxx:打开活点地图并查询该角色
			var q = arg[4..].Trim();
			if (q.Length > 0) Dashboard.SetMapQuery(q);
			else Dashboard.OpenTab(1);
		}
		else if (lower == "behavior")
		{
			// /aca behavior:打开行为设置页签(条件触发宏)
			Dashboard.OpenTab(4);
		}
		else if (lower == "music" || lower == "midi")
		{
			// /aca music:打开演奏页签(MIDI 演奏)
			Dashboard.OpenTab(5);
		}
		else if (lower == "setting")
		{
			// /aca setting:打开设置(控制)面板
			MainWindow.IsOpen = true;
		}
		else
		{
			Dashboard.OpenTab(-1);
		}
	}

	/// <summary>取当前游戏目标的玩家名(清洗后;非玩家目标/自己返回 null)</summary>
	private static string? GetTargetPlayerName()
	{
		var target = TargetManager.Target;
		if (target == null) return null;
		if (target.ObjectKind != ObjectKind.Pc) return null;
		if (target.GameObjectId == ObjectTable.LocalPlayer?.GameObjectId) return null; // 自己
		return target is IPlayerCharacter pc ? AuraCanAiCore.GetCleanName(pc.Name.TextValue) : null;
	}

	/// <summary>触发宏列表中指定宏(1-100)。供命令/面板/网页/AI 层调用。自动切到游戏框架线程。</summary>
	public static bool TriggerMacro(int index, bool shared = false)
	{
		if (Framework.IsInFrameworkUpdateThread)
			return MacroExecutor.Execute(index, shared);
		return Framework.RunOnFrameworkThread(() => MacroExecutor.Execute(index, shared)).GetAwaiter().GetResult();
	}

	public void ToggleMainUi() => Dashboard.Toggle();
	public void ToggleConfigUi() => MainWindow.Toggle();

	public void Dispose()
	{
		PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
		PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
		PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

		WindowSystem.RemoveAllWindows();
		MainWindow.Dispose();
		Dashboard.Dispose();

		CommandManager.RemoveHandler(CommandName);

		AuraCore?.Dispose();
		AuraCore = null;
	}
}
