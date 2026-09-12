using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace AuraCanAI.Dalamud.Core;

/// <summary>
/// 游戏宏触发:直接执行宏列表中的宏,不经过热键栏。
/// 链路:UIModule 子模块 GetMacro 取宏数据 → ExecuteMacro 执行,由游戏原生宏执行器逐行执行(完整支持 /wait、占位符)。
/// ⚠️ 子模块指针取法(2026-09-11 踩坑后重写):
///   1) 首选官方存取器 UIModule->GetRaptureMacroModule()/GetRaptureShellModule()(靠 Dalamud 内置签名解析,跟版本走);
///   2) 存取器返回 null 时,回退到「从已安装的 FFXIVClientStructs 布局反射出的字段偏移」(自动适应新版,不是写死的数字);
/// **绝不要再写死 `(byte*)ui + 0xB9B30` 这类常量**:游戏更新后 RaptureShellModule 偏移由 0xB9B30 变为 0xB9B50,
/// 偏移错 0x20 字节 → shell->MacroLocked 读到脏数据恒为 true → 行为触发全部被判定「执行器忙」而被丢弃(表现:有注视提示但宏不执行)。
/// 必须通过 Plugin.TriggerMacro 在游戏框架线程调用。
/// </summary>
public static unsafe class MacroExecutor
{
	// 官方存取器失败时的兜底偏移(运行时反射得到,非硬编码;取不到 -1 = 不启用兜底)
	// 注意:CS 里这两个字段是 internal/non-public,不能用 nameof,只能写字段名字符串
	private static readonly int MacroModuleOffset = ReflectedOffset("RaptureMacroModule");
	private static readonly int ShellModuleOffset = ReflectedOffset("RaptureShellModule");

	private static int ReflectedOffset(string fieldName)
	{
		try
		{
			var f = typeof(UIModule).GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			var off = f?.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? -1;
			if (off >= 0) return off;
		}
		catch { }
		return -1;
	}

	/// <summary>取宏模块指针(官方存取器优先,反射偏移兜底)</summary>
	private static RaptureMacroModule* MacroModule(UIModule* ui)
	{
		var m = ui->GetRaptureMacroModule();
		if (m != null) return m;
		return MacroModuleOffset >= 0 ? (RaptureMacroModule*)((byte*)ui + MacroModuleOffset) : null;
	}

	/// <summary>取 Shell 模块指针(官方存取器优先,反射偏移兜底)</summary>
	private static RaptureShellModule* ShellModule(UIModule* ui)
	{
		var s = ui->GetRaptureShellModule();
		if (s != null) return s;
		return ShellModuleOffset >= 0 ? (RaptureShellModule*)((byte*)ui + ShellModuleOffset) : null;
	}

	/// <summary>宏执行器是否正忙(MacroLocked,上一个宏还在 /wait 等)。行为引擎队列泵送用。</summary>
	public static unsafe bool IsBusy()
	{
		try
		{
			var ui = UIModule.Instance();
			if (ui == null) return false;
			var shell = ShellModule(ui);
			if (shell == null) return false;
			return shell->MacroLocked;
		}
		catch { return false; }
	}

	/// <summary>诊断文本(子模块指针 / 锁定状态 / 宏是否为空),供 /aca macrodia 与失败日志使用。</summary>
	public static string Diagnose(int index, bool shared)
	{
		try
		{
			var sb = new System.Text.StringBuilder();
			if (Plugin.ClientState == null || !Plugin.ClientState.IsLoggedIn) return "未登录";
			var ui = UIModule.Instance();
			if (ui == null) return "UIModule 为 null";
			var macroModule = MacroModule(ui);
			var shell = ShellModule(ui);
			sb.Append($"ui=0x{(nint)ui:X} macroModule=0x{(nint)macroModule:X} shell=0x{(nint)shell:X}");
			if (macroModule == null || shell == null)
			{
				sb.Append(" → 子模块指针为空(官方签名解析失败,可能游戏更新未适配)");
				return sb.ToString();
			}
			sb.Append($" MacroLocked={shell->MacroLocked}");
			if (shell->MacroLocked) sb.Append("(执行器忙:上一个宏还在 /wait,或未正常结束)");
			var macro = macroModule->GetMacro(shared ? 1u : 0u, (uint)index);
			if (macro == null) sb.Append($" | 取不到宏 {(shared ? "s" : "")}{index}");
			else sb.Append($" | 宏{(shared ? "s" : "")}{index} IsNotEmpty={macro->IsNotEmpty()} 行数={macroModule->GetLineCount(macro)}");
			return sb.ToString();
		}
		catch (Exception e) { return $"诊断异常: {e.Message}"; }
	}

	/// <summary>执行指定宏。index:0-99(与游戏内宏列表编号一致,游戏有 0 号宏);shared=true 取共享宏。返回是否成功触发。</summary>
	public static bool Execute(int index, bool shared = false)
	{
		try
		{
			if (index < 0 || index > 99) return false;
			var ui = UIModule.Instance();
			if (ui == null) return false;

			var macroModule = MacroModule(ui);
			var shell = ShellModule(ui);
			if (macroModule == null || shell == null)
			{
				Plugin.Log?.Error($"触发宏失败:子模块指针为空(macro={((nint)macroModule).ToInt64():X} shell={((nint)shell).ToInt64():X})");
				return false;
			}

			// 宏执行器正忙(上一个宏还在跑 /wait 等)时不重复触发,避免叠加异常
			if (shell->MacroLocked) return false;

			var macro = macroModule->GetMacro(shared ? 1u : 0u, (uint)index);
			if (macro == null)
			{
				Plugin.Log?.Warning($"触发宏失败:取不到宏 {(shared ? "s" : "")}{index}");
				return false;
			}
			// 空宏不执行
			if (!macro->IsNotEmpty())
			{
				Plugin.Log?.Warning($"触发宏失败:宏 {(shared ? "s" : "")}{index} 为空");
				return false;
			}

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
