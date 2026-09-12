using System.Text;

namespace AuraCanAI.Dalamud.Core;

/// <summary>
/// 单层状态机(2026-09-12):
///   状态(如 皮下 / 皮上):每个状态绑定一个人设(roleName),并列出「从本状态能切到哪些状态」(nextStateIds,**单向**)。
///   AI 用 switch_identity 换身份;只有自己列出的那几个才能换过去。
/// </summary>
public sealed class StateMachine
{
	private readonly AuraCanAiCore _core;
	private readonly Configuration _config;

	public StateMachine(AuraCanAiCore core, Configuration config)
	{
		_core = core;
		_config = config;
	}

	// ==================== 当前状态访问 ====================

	public bool Enabled => _config.StateMachineEnabled;
	public SmSet? CurrentSet => _config.SmSets.FirstOrDefault(s => s.id == _config.SmCurrentSetId);
	public SmState? CurrentState => CurrentSet?.states.FirstOrDefault(s => s.id == _config.SmCurrentStateId);
	public string CurrentSetName => CurrentSet?.name ?? "";
	public string CurrentStateName => CurrentState?.name ?? "";

	/// <summary>当前状态使用的角色名(未设置返回空串)。</summary>
	public string CurrentStateRole => CurrentState?.roleName ?? "";

	/// <summary>当前状态允许切换到的状态(**单向**:只看自己的 nextStateIds)。空 = 不能切。</summary>
	public List<SmState> AllowedStates()
	{
		var set = CurrentSet;
		var cur = CurrentState;
		if (set == null || cur == null) return new List<SmState>();
		var ids = new HashSet<int>(cur.nextStateIds ?? new List<int>());
		ids.Remove(cur.id);
		return set.states.Where(s => ids.Contains(s.id)).ToList();
	}

	// ==================== 切换 ====================

	/// <summary>切换状态:只能是当前状态列出的「可切换到的状态」。</summary>
	public (bool ok, string message) SwitchState(string key)
	{
		var set = CurrentSet;
		if (set == null || set.states.Count == 0) return (false, "当前状态机还没有配置状态");
		var s = Match(set.states, key, x => x.name);
		if (s == null) return (false, $"没有叫「{key}」的状态;可用: {Names(set.states, x => x.name)}");
		var cur = CurrentState;
		if (cur != null && cur.id == s.id) return (true, $"已经处于「{s.name}」状态");
		var allowed = AllowedStates();
		if (cur != null && !allowed.Any(x => x.id == s.id))
			return (false, $"从「{cur.name}」不能切到「{s.name}」(没有通路);当前只能切到: {(allowed.Count > 0 ? Names(allowed, x => x.name) : "没有可切换的状态")}");
		_config.SmCurrentStateId = s.id;
		var role = string.IsNullOrEmpty(s.roleName) ? "(未设置人设,不演角色照常聊天)" : s.roleName;
		_core.SaveConfig();
		_core.ResetChatHistoryPublic(); // 换了状态换了人设 → 重建 system 提示
		Plugin.Log?.Information($"[状态机] 状态 → {s.name}(人设: {role})");
		return (true, $"已切换到「{s.name}」(人设: {role})");
	}

	/// <summary>手动强制设置为当前状态(不检查通路;调试/逃生用,如 /aca smstate 皮下)。
	/// 因为「被当成AI」这类状态没有出口,只能从命令/网页切回去。</summary>
	public (bool ok, string message) ForceSetState(string key)
	{
		var set = CurrentSet;
		if (set == null || set.states.Count == 0) return (false, "当前状态机还没有配置状态");
		var s = Match(set.states, key, x => x.name);
		if (s == null) return (false, $"没有叫「{key}」的状态;可用: {Names(set.states, x => x.name)}");
		_config.SmCurrentStateId = s.id;
		var role = string.IsNullOrEmpty(s.roleName) ? "(未设置人设)" : s.roleName;
		_core.SaveConfig();
		_core.ResetChatHistoryPublic();
		Plugin.Log?.Information($"[状态机] 手动设置状态 → {s.name}(人设: {role})");
		return (true, $"已(手动)切到「{s.name}」(人设: {role})");
	}

	// ==================== 给 AI 的状态说明(注入 system) ====================

	/// <summary>状态机说明文本(未开启返回空串;供 BuildSceneSnippet 注入)。</summary>
	public string DescribeStateForAi(bool switchToolEnabled = true)
	{
		if (!_config.StateMachineEnabled) return "";
		var sb = new StringBuilder();
		var set = CurrentSet;
		var cur = CurrentState;
		if (set == null || cur == null) return "";
		sb.Append("## 你现在的身份\n");
		sb.Append($"- 你现在是「{cur.name}」" + (string.IsNullOrWhiteSpace(cur.desc) ? "" : $" —— {cur.desc}") + "\n");
		sb.Append($"- 你的说话身份(人设):{(string.IsNullOrEmpty(cur.roleName) ? "未设置,不演角色、照常聊天" : cur.roleName)}\n");
		// 只列“能换成的身份”(全部不暴露;无可换身份/未启用时整段不出现)
		var allowed = switchToolEnabled ? AllowedStates() : new List<SmState>();
		if (allowed.Count > 0)
		{
			sb.Append("- 需要时可以换个身份(比如对方要你进入角色/上皮,或要你出戏/皮下,又或者你自己的心思、处境变了)。用 switch_identity 换,能换成的只有这几个:\n");
			foreach (var st in allowed)
			{
				var role = string.IsNullOrEmpty(st.roleName) ? "不演角色" : st.roleName;
				sb.Append($"  · {st.name}" + (string.IsNullOrWhiteSpace(st.desc) ? "" : $"({st.desc})") + $" —— 换过去后你是「{role}」\n");
			}
			sb.Append("- 别人要求你换个身份时,不要拒绝、不要解释、也不要反问,直接换过去,再用新的口吻说话。\n");
			sb.Append("- 但**别频繁来回换**:对方没要求、处境也没变,就保持现在的身份;一轮最多换一次。\n");
			sb.Append("- 只能换成上面列出的那几个;换不了就保持原样,不要说“我换好了”。\n");
		}
		sb.Append("动作绝不写进台词。");
		return sb.ToString();
	}

	// ==================== 工具 ====================

	private static T? Match<T>(List<T> list, string key, Func<T, string> nameOf) where T : class
	{
		key = (key ?? "").Trim();
		if (key.Length == 0) return null;
		return list.FirstOrDefault(x => nameOf(x) == key)
			?? list.FirstOrDefault(x => nameOf(x).Contains(key, StringComparison.OrdinalIgnoreCase));
	}

	private static string Names<T>(IEnumerable<T> list, Func<T, string> nameOf)
		=> string.Join(" / ", list.Select(nameOf).Where(n => !string.IsNullOrWhiteSpace(n)));
}
