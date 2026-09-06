using System.Runtime.CompilerServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace AuraCanAI.Dalamud.Core;

/// <summary>
/// 游戏宏触发:直接执行宏列表中的宏(1-100),不经过热键栏。
/// 链路:UIModule.RaptureMacroModule(偏移 0x61B0).GetMacro 取宏数据 → UIModule.RaptureShellModule(偏移 0xB9B30).ExecuteMacro 执行。
/// 由游戏原生宏执行器逐行执行,完整支持 /wait、占位符、条件等宏语义。
/// 偏移来自 FFXIVClientStructs 7.51 布局(已用反射验证与本地 SDK 一致)。
/// 必须通过 Plugin.TriggerMacro 在游戏框架线程调用。
/// </summary>
public static unsafe class MacroExecutor
{
	// UIModule 内嵌子模块偏移(FFXIVClientStructs UIModule 布局,7.51 已验证)
	private const int MacroModuleOffset = 0x61B0; // RaptureMacroModule
	private const int ShellModuleOffset = 0xB9B30; // RaptureShellModule

	/// <summary>宏执行器是否正忙(MacroLocked,上一个宏还在 /wait 等)。行为引擎队列泵送用。</summary>
	public static unsafe bool IsBusy()
	{
		try
		{
			var ui = UIModule.Instance();
			if (ui == null) return false;
			var shell = (RaptureShellModule*)((byte*)ui + ShellModuleOffset);
			return shell->MacroLocked;
		}
		catch { return false; }
	}

	/// <summary>执行指定宏。index:0-99(与游戏内宏列表编号一致,游戏有 0 号宏);shared=true 取共享宏。返回是否成功触发。</summary>
	public static bool Execute(int index, bool shared = false)
	{
		try
		{
			if (index < 0 || index > 99) return false;
			var ui = UIModule.Instance();
			if (ui == null) return false;

			var macroModule = (RaptureMacroModule*)((byte*)ui + MacroModuleOffset);
			var shell = (RaptureShellModule*)((byte*)ui + ShellModuleOffset);

			// 宏执行器正忙(上一个宏还在跑 /wait 等)时不重复触发,避免叠加异常
			if (shell->MacroLocked) return false;

			var macro = macroModule->GetMacro(shared ? 1u : 0u, (uint)index);
			if (macro == null) return false;
			// 空宏不执行
			if (!macro->IsNotEmpty()) return false;

			shell->ExecuteMacro(macro);
			return true;
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"触发宏失败: {e}");
			return false;
		}
	}
}
