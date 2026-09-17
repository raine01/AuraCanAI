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
		if (set == null || set.states.Count == 0) return (false, "现在没有可用的身份");
		var s = Match(set.states, key, x => x.name);
		if (s == null) return (false, $"没有叫「{key}」的身份;能换成的是: {Names(set.states, x => x.name)}");
		var cur = CurrentState;
		if (cur != null && cur.id == s.id) return (true, $"你现在已经是「{s.name}」了");
		var allowed = AllowedStates();
		if (cur != null && !allowed.Any(x => x.id == s.id))
			return (false, $"你现在是「{cur.name}」,换不到「{s.name}」;能换成的是: {(allowed.Count > 0 ? Names(allowed, x => x.name) : "没有能换成的身份")}");
		_config.SmCurrentStateId = s.id;
		var role = string.IsNullOrEmpty(s.roleName) ? "(未设置人设,不演角色照常聊天)" : s.roleName;
		_core.SaveConfig();
		_core.ResetChatHistoryPublic(); // 换了状态换了人设 → 重建 system 提示
		Plugin.Log?.Information($"[状态机] 状态 → {s.name}(人设: {role})");
		return (true, $"已换成「{s.name}」,接下来按人设「{role}」说话");
	}

	/// <summary>强制设置为当前状态(不检查通路;供“闲置回到默认状态”等程序内部使用)。
	/// 「被当成AI」这类状态没有出口,所以需要能绕过通路直接设。</summary>
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
			// ⚠️ 泛用口径(2026-09-12):**不写死任何具体身份/例子**(不能假设只有 皮下/皮上/被当成AI)。
			// 「什么时候换」一律由用户给每个状态写的 desc 决定:desc 就是触发条件。用户新建任意状态机都照此工作。
			sb.Append("- 你可以换身份(用 switch_identity):判断标准是下面每个身份写的「换到它的情形」——**只要出现了某一条,就直接换过去**;不需要对方开口要求、不用等谁批准、也不是只有对方点名才能换。\n");
			sb.Append(DescribeSwitchCandidates());
			sb.Append("- 对方直接点名叫你换身份时,不要拒绝、不要解释、不要反问,直接换过去。\n");
			sb.Append("- 换身份必须**真的调用 switch_identity**;只回一句“我换上了 / 我不装了 / 好我出戏”这种嘴上表态不算换(有没有换看程序、不看台词)。\n");
			sb.Append("- 别来回换:**一轮最多换一次**;没有任何一条情形成立,就保持现在的身份。\n");
			sb.Append("- 只能换成上面列出的那几个;换不了就保持原样,不要说“我换好了”。\n");
		}
		sb.Append("动作绝不写进台词。");
		return sb.ToString();
	}

	/// <summary>可切换身份一览(泛用:内容全部来自用户配置,与状态机具体含义无关)。
	/// 供“注入提示词”与“switch_identity 工具描述”**共用**,避免两处口径漂移。
	/// 每行:`· 名字 —— 换到它的情形:&lt;desc&gt;;换过去后按人设「X」说话`。</summary>
	public string DescribeSwitchCandidates(string linePrefix = "  · ")
	{
		var sb = new StringBuilder();
		foreach (var st in AllowedStates())
		{
			var role = string.IsNullOrEmpty(st.roleName) ? "不演角色,照常聊天" : st.roleName;
			var trigger = string.IsNullOrWhiteSpace(st.desc) ? "(这个状态没写情形;对方点名要它时换)" : st.desc;
			sb.Append(linePrefix).Append(st.name).Append(" —— 换到它的情形:").Append(trigger)
			  .Append(";换过去后按人设「").Append(role).Append("」说话\n");
		}
		return sb.ToString();
	}

	/// <summary>可切换身份一览的单行版(工具描述用;条目间用 “ ; ” 隔开)。</summary>
	public string DescribeSwitchCandidatesInline()
		=> DescribeSwitchCandidates("").Replace("\r", " ").Replace("\n", " ; ").Trim().TrimEnd(';', ' ').Trim();

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
