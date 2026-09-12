namespace AuraCanAI.Dalamud.Core;

/// <summary>
/// 角色自定义动作执行器(AI 主动执行动作;动作列表绑定在角色上,前端「角色设定 → 角色管理」编辑)。
/// 一个动作 = 依次发送两条游戏命令(相当于依次执行宏):
///   1) 「/动作名 动作|motion」—— 游戏内表情(_config 里 RoleAction.emote,已带 / 则原样用;
///      自动补本地化子命令,详见 BuildEmoteCommand:不加会多出一条游戏官方表情提示)
///   2) 「/em 文字」  —— 聊天栏原创动作文本(RoleAction.text)
/// 每步之间隔 StepGapMs,由 AuraCanAiCore 的 500ms 框架线程 tick 泵送(一次 tick 一步,足够让表情先播出来且不撞聊天栏发送频率)。
/// 冷却按「角色名 + 动作名」记录(内存态,不写配置,避免被前端保存覆盖)。
/// </summary>
public sealed class RoleActionPlayer
{
	private const int MaxQueue = 3; // 最多排队 3 个动作(防止模型连续刷)
	private const int StepGapMs = 300; // 两步之间的最小间隔(毫秒)

	private readonly AuraCanAiCore _core;
	private readonly List<Pending> _queue = new();
	private readonly Dictionary<string, DateTime> _cooldownUntil = new();

	private sealed class Pending
	{
		public string Label = ""; // 角色@动作名(日志用)
		public List<string> Steps = new(); // 待依次发送的游戏命令
		public int Next; // 下一步下标
		public DateTime LastStepAt; // 上一步发送时间
	}

	public RoleActionPlayer(AuraCanAiCore core) => _core = core;

	/// <summary>队列中待执行动作数(诊断用)</summary>
	public int QueueCount => _queue.Count;

	/// <summary>清空队列与冷却(切换/重载角色配置时调用)</summary>
	public void Reset()
	{
		_queue.Clear();
		_cooldownUntil.Clear();
	}

	/// <summary>按名字在当前角色(前端选中的 currentRole)的动作列表里找一个动作;找不到返回 null。</summary>
	public RoleAction? Find(string roleName, string actionName)
		=> MatchAction(_core.GetRoleActions(roleName), actionName);

	/// <summary>名字匹配(供工具/诊断复用):优先 名称/动作/展示名 完全相等 → 名称/动作/文字/展示名 包含。</summary>
	public static RoleAction? MatchAction(List<RoleAction> actions, string actionName)
	{
		if (actions == null || actions.Count == 0) return null;
		var key = (actionName ?? "").Trim();
		if (key.Length == 0) return null;

		bool Eq(string? v) => !string.IsNullOrEmpty(v) && string.Equals(v.Trim(), key, StringComparison.OrdinalIgnoreCase);
		bool Has(string? v) => !string.IsNullOrEmpty(v) && v.Contains(key, StringComparison.OrdinalIgnoreCase);

		return actions.FirstOrDefault(a => Eq(a.name) || Eq(a.emote) || Eq(DisplayName(a)))
			?? actions.FirstOrDefault(a => Has(a.name) || Has(a.emote) || Has(a.text))
			?? actions.FirstOrDefault(a => Has(DisplayName(a)));
	}

	/// <summary>
	/// 执行一个动作(冷却校验 + 入队)。返回给大模型看的中文结果描述(模型据此决定后续台词)。
	/// ignoreCooldown=true 供状态机待机轮换用:停留时间由状态机自己控制,不受角色动作冷却限制。
	/// </summary>
	public string Perform(string roleName, RoleAction action, bool ignoreCooldown = false)
	{
		var label = DisplayName(action);
		var steps = BuildSteps(action);
		if (steps.Count == 0) return $"动作「{label}」没有内容(未填动作和文字),没有执行";

		var key = CooldownKey(roleName, action);
		if (!ignoreCooldown && _cooldownUntil.TryGetValue(key, out var until) && DateTime.Now < until)
			return $"动作「{label}」还在冷却中(剩 {(int)Math.Ceiling((until - DateTime.Now).TotalSeconds)} 秒),现在不能做,换个动作或先说话";

		if (_queue.Count >= MaxQueue)
			return $"动作「{label}」没排上(正在做的动作还没结束)";

		if (!ignoreCooldown) _cooldownUntil[key] = DateTime.Now.AddSeconds(Math.Max(0, action.cooldown));
		_queue.Add(new Pending { Label = label, Steps = steps });
		Plugin.Log?.Information($"角色动作[{roleName}·{label}]已排队: {string.Join(" → ", steps)}");
		// 用户口径:动作自身不再回显进聊天历史(避免污染台词风格),工具只需告诉模型“成功”
		return "成功";
	}

	/// <summary>按名字执行(工具入口)。返回给模型的结果描述。</summary>
	public string PerformByName(string roleName, string actionName)
	{
		var action = Find(roleName, actionName);
		if (action == null)
		{
			var list = _core.GetRoleActions(roleName);
			if (list.Count == 0) return "当前角色没有配置动作列表,做不了动作";
			var names = string.Join(", ", list.Select(DisplayName));
			return $"没有叫「{actionName}」的动作;可用动作: {names}";
		}
		return Perform(roleName, action);
	}

	/// <summary>每 tick 推进一步(必须在游戏框架线程调用;无队列时零开销)</summary>
	public void Tick()
	{
		if (_queue.Count == 0) return;
		var p = _queue[0];
		if (p.Next > 0 && (DateTime.Now - p.LastStepAt).TotalMilliseconds < StepGapMs) return;

		var cmd = p.Steps[p.Next];
		p.LastStepAt = DateTime.Now;
		p.Next++;
		var ok = _core.RunCommandPublic(cmd);
		if (!ok) Plugin.Log?.Warning($"角色动作[{p.Label}]命令发送失败(未登录/过场中): {cmd}");
		else Plugin.Log?.Information($"角色动作[{p.Label}]已发送({p.Next}/{p.Steps.Count}): {cmd}");
		if (p.Next >= p.Steps.Count) _queue.RemoveAt(0);
	}

	/// <summary>动作 → 依次发送的游戏命令(动作名 → /动作名[ 动作|motion];文字 → /em 文字)</summary>
	public static List<string> BuildSteps(RoleAction action)
	{
		var steps = new List<string>();
		var emote = (action.emote ?? "").Trim();
		var text = (action.text ?? "").Trim();
		if (emote.Length > 0) steps.Add(BuildEmoteCommand(emote, text.Length > 0));
		if (text.Length > 0) steps.Add("/em " + text);
		return steps;
	}

	/// <summary>动作最终会发送的命令预览(前端内联展示、日志用)</summary>
	public static string DescribeCommands(RoleAction action)
	{
		var steps = BuildSteps(action);
		return steps.Count == 0 ? "(未填动作和文字,不会执行)" : string.Join(" → ", steps);
	}

	/// <summary>
	/// 表情命令:**填了文字**时才补「动作」/「motion」子命令(= 只播放动作,不出游戏官方表情文字)。
	/// 不补的话,游戏里会先出一条 /表情名 的官方提示,再加上 /em 的文字 → **两条提示**(2026-09-11 用户实测)。
	/// 没填文字时不补(保留游戏官方表情文字,否则聊天栏什么都不显示)。
	/// 子命令是本地化关键字:表情名含中日文字符 → 「 动作」;否则(英文命令如 pet)→ 「 motion」。
	/// 已自带参数(命令里有空格)时原样使用,不重复补。
	/// ⚠️ 前端 character.html 的 actionPreview() 是同一规则的 JS 副本,改这里要同步改那边。
	/// </summary>
	public static string BuildEmoteCommand(string emote, bool hasText)
	{
		var cmd = emote.Trim();
		if (!cmd.StartsWith('/')) cmd = "/" + cmd;
		if (!hasText) return cmd; // 只填了表情:保留游戏官方表情文字
		if (cmd.Contains(' ') || cmd.Contains('　')) return cmd; // 用户已写参数(如 "/抚摸 <t>"):尊重原文
		return cmd + (HasCjk(emote) ? " 动作" : " motion");
	}

	/// <summary>是否含中日文字符(用于选本地化的子命令关键字:动作 / motion)</summary>
	public static bool HasCjk(string s)
	{
		foreach (var ch in s)
			if (ch >= 0x2E80 && ch <= 0x9FFF || ch >= 0xF900 && ch <= 0xFAFF || ch >= 0xFF00 && ch <= 0xFFEF)
				return true;
		return false;
	}

	/// <summary>动作展示名(名称 → 动作 → 文字截断)</summary>
	public static string DisplayName(RoleAction a)
	{
		if (!string.IsNullOrWhiteSpace(a.name)) return a.name.Trim();
		if (!string.IsNullOrWhiteSpace(a.emote)) return a.emote.Trim();
		var t = (a.text ?? "").Trim();
		return t.Length > 12 ? t[..12] + "…" : t;
	}

	/// <summary>冷却键:同名动作在不同角色之间互不影响</summary>
	private static string CooldownKey(string roleName, RoleAction a) => $"{roleName}\u0001{DisplayName(a)}";
}
