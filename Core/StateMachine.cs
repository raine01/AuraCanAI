using System.Numerics;
using System.Text;

namespace AuraCanAI.Dalamud.Core;

/// <summary>
/// 单层状态机(2026-09-12 由两层坍缩):
///   状态(如 皮下 / 皮上 / 心情很糟糕…):每个状态绑定一个人设(roleName)、有一份待机动作(actions),
///   并且**只有勾选互通的状态之间才能切换**(nextStateIds 以双向保存;空 = 不能切到别的状态)。
///   AI 用 switch_state 切换;待机动作按冷却自动随机轮换(有位置先寻路走过去)。
///   位置由 /aca pos [动作名] 在游戏内记录(空动作名 = 写进前端「记录位置」武装的那条)。
/// </summary>
public sealed class StateMachine
{
	private const int MinSwitchSec = 3;       // 自动轮换最小间隔(冷却为 0 时防止每 500ms 乱换)
	private const int DefaultSwitchSec = 30;  // 冷却 <= 0 时按 30 秒
	private const float MoveTimeoutSec = 30f; // 待机动作走位超时(秒)

	private readonly AuraCanAiCore _core;
	private readonly Configuration _config;

	// ===== 待机动作轮换运行时 =====
	private string _idleActionName = "";
	private DateTime _idleNextSwitchAt = DateTime.MinValue;
	private bool _idleWaitingMove; // 已发出走位,等到达后播动作
	private DateTime _idleMoveDeadline = DateTime.MinValue;

	// ===== 位置记录武装(前端某行点「记录位置」→ 游戏内 /aca pos 不带名写进这条) =====
	public int ArmedSetId { get; private set; }
	public int ArmedStateId { get; private set; }
	public string ArmedActionName { get; private set; } = "";

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

	/// <summary>当前状态允许切换到的状态(互通:自己的勾选 ∪ 对方勾了自己;排除自己)。空 = 不能切。</summary>
	public List<SmState> AllowedStates()
	{
		var set = CurrentSet;
		var cur = CurrentState;
		if (set == null || cur == null) return new List<SmState>();
		var ids = new HashSet<int>(cur.nextStateIds ?? new List<int>());
		foreach (var s in set.states)
			if (s.id != cur.id && (s.nextStateIds?.Contains(cur.id) ?? false)) ids.Add(s.id);
		ids.Remove(cur.id);
		return set.states.Where(s => ids.Contains(s.id)).ToList();
	}

	/// <summary>清空待机轮换运行时(切换状态/保存配置后调用;下一次 Tick 会重新开始)。</summary>
	public void ResetIdle()
	{
		_idleActionName = "";
		_idleWaitingMove = false;
		_idleNextSwitchAt = DateTime.MinValue;
	}

	// ==================== 切换 ====================

	/// <summary>切换状态:只能是当前状态「勾选互通」的状态(空勾选 = 不能切)。</summary>
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
			return (false, $"从「{cur.name}」不能切到「{s.name}」(没有勾选互通);当前只能切到: {(allowed.Count > 0 ? Names(allowed, x => x.name) : "没有可切换的状态")}");
		_config.SmCurrentStateId = s.id;
		var role = string.IsNullOrEmpty(s.roleName) ? "(未设置人设,不演角色照常聊天)" : s.roleName;
		ApplyStateChange($"状态 → {s.name}(人设: {role})");
		var actInfo = s.actions.Count > 0 ? $";本状态有 {s.actions.Count} 个动作" : "";
		return (true, $"已切换到「{s.name}」(人设: {role}){actInfo}");
	}

	private void ApplyStateChange(string logText)
	{
		_core.SaveConfig();
		ResetIdle();
		_core.ResetChatHistoryPublic(); // 换状态换了人设 → 重建 system 提示
		Plugin.Log?.Information($"[状态机] {logText}");
	}

	// ==================== 待机动作轮换(500ms 框架线程) ====================

	/// <summary>每 500ms 推进一步:到点随机换下一个动作并执行(有位置先走过去)。</summary>
	public void Tick()
	{
		if (!_config.StateMachineEnabled) return;
		var actions = CurrentState?.actions ?? new List<IdleAction>();
		if (actions.Count == 0) { _idleActionName = ""; return; }

		// 前端改名/删除了当前动作 → 重置
		if (!string.IsNullOrEmpty(_idleActionName) && actions.All(a => a.name != _idleActionName))
			_idleActionName = "";

		if (_idleWaitingMove)
		{
			if (!_core.Movement.IsActive)
			{
				_idleWaitingMove = false;
				PlayEmote(_idleActionName, actions);
				_idleNextSwitchAt = DateTime.Now.AddSeconds(IntervalFor(_idleActionName, actions));
			}
			else if (DateTime.Now >= _idleMoveDeadline)
			{
				Plugin.Log?.Warning($"[状态机] 待机动作「{_idleActionName}」走位超时,放弃");
				_idleWaitingMove = false;
				_idleNextSwitchAt = DateTime.Now.AddSeconds(IntervalFor(_idleActionName, actions));
			}
			return;
		}

		if (string.IsNullOrEmpty(_idleActionName))
		{
			StartAction(PickRandom(actions, null), actions);
			return;
		}

		if (DateTime.Now < _idleNextSwitchAt) return;
		if (_core.Movement.IsActive) { _idleNextSwitchAt = DateTime.Now.AddSeconds(2); return; } // 别打断别的移动
		StartAction(PickRandom(actions, _idleActionName), actions);
	}

	/// <summary>手动执行一个待机动作(AI 工具 rp_idle_action / /aca idle)。空名字 = 随机一个。</summary>
	public string PerformIdleAction(string name)
	{
		var s = CurrentState;
		if (s == null) return "状态机没有当前状态";
		var actions = s.actions ?? new List<IdleAction>();
		if (actions.Count == 0) return $"状态「{s.name}」没有配置动作列表";
		if (string.IsNullOrWhiteSpace(name))
		{
			var r = PickRandom(actions, _idleActionName);
			StartAction(r, actions);
			return $"成功:开始做「{r.name}」";
		}
		var a = actions.FirstOrDefault(x => x.name == name) ?? actions.FirstOrDefault(x => x.emote == name);
		if (a == null) return $"没有叫「{name}」的动作;可用: {Names(actions, x => x.name)}";
		StartAction(a, actions);
		return $"成功:开始做「{a.name}」" + (a.hasPos ? "(先走过去,到位后做动作)" : "");
	}

	private void StartAction(IdleAction a, List<IdleAction> actions)
	{
		_idleActionName = a.name;
		if (a.hasPos)
		{
			if (_core.Movement.IsActive) { _idleNextSwitchAt = DateTime.Now.AddSeconds(2); return; }
			var ok = _core.Movement.MoveToPoint(new Vector3(a.x, a.y, a.z));
			if (ok)
			{
				_idleWaitingMove = true;
				_idleMoveDeadline = DateTime.Now.AddSeconds(MoveTimeoutSec);
				Plugin.Log?.Information($"[状态机] 待机动作「{a.name}」→ 走向 ({a.x:F1},{a.z:F1})");
				return;
			}
			Plugin.Log?.Warning($"[状态机] 待机动作「{a.name}」走位失败(位置可能不在当前房间),原地做动作");
		}
		PlayEmote(a.name, actions);
		_idleNextSwitchAt = DateTime.Now.AddSeconds(IntervalFor(a.name, actions));
	}

	private void PlayEmote(string actionName, List<IdleAction> actions)
	{
		var a = actions.FirstOrDefault(x => x.name == actionName);
		if (a == null || string.IsNullOrWhiteSpace(a.emote)) return; // 没填动作 = 只走位站着
		var ra = new RoleAction { name = a.name, emote = a.emote, text = "" };
		var r = _core.RoleActions.Perform(_core.GetActiveRoleName(), ra, ignoreCooldown: true);
		Plugin.Log?.Information($"[状态机] 待机动作「{a.name}」表情: {r}");
	}

	private static IdleAction PickRandom(List<IdleAction> actions, string? exclude)
	{
		var pool = actions.Where(a => a.name != exclude).ToList();
		if (pool.Count == 0) pool = actions;
		return pool[Random.Shared.Next(pool.Count)];
	}

	private static int IntervalFor(string actionName, List<IdleAction> actions)
	{
		var sec = actions.FirstOrDefault(x => x.name == actionName)?.cooldown ?? 0;
		if (sec <= 0) sec = DefaultSwitchSec;
		return Math.Max(MinSwitchSec, sec);
	}

	// ==================== 位置记录 ====================

	/// <summary>武装「位置记录」目标(前端某行的「记录位置」按钮)。</summary>
	public void ArmPos(int setId, int stateId, string actionName)
	{
		ArmedSetId = setId;
		ArmedStateId = stateId;
		ArmedActionName = actionName ?? "";
	}

	public void ClearArm() => ArmPos(0, 0, "");

	/// <summary>记录当前位置到动作的「位置」字段(/aca pos [动作名];不带名 = 用武装的那条)。</summary>
	public string RecordPosition(string actionName)
	{
		(int setId, int stateId, string target) = (-1, -1, (actionName ?? "").Trim());
		if (target.Length == 0)
		{
			if (string.IsNullOrEmpty(ArmedActionName))
				return "没有指定动作:请带动作名(/aca pos 坐下),或先到网页状态机里点该动作的「记录位置」";
			(setId, stateId, target) = (ArmedSetId, ArmedStateId, ArmedActionName);
		}
		var tf = _core.GetLocalTransform();
		if (tf == null) return "未登录/无玩家,无法记录位置";
		var (pos, yaw, tid) = tf.Value;

		IdleAction? action = null;
		SmState? st;
		if (setId > 0)
		{
			st = _config.SmSets.FirstOrDefault(x => x.id == setId)?.states.FirstOrDefault(m => m.id == stateId);
			action = st?.actions.FirstOrDefault(a => a.name == target);
		}
		else
		{
			st = CurrentState;
			action = st?.actions.FirstOrDefault(a => a.name == target)
				?? _config.SmSets.SelectMany(x => x.states).SelectMany(m => m.actions).FirstOrDefault(a => a.name == target);
		}
		if (action == null) return $"找不到动作「{target}」(要在当前状态的动作列表里,名字要完全一致)";

		action.hasPos = true;
		action.x = pos.X; action.y = pos.Y; action.z = pos.Z;
		action.yaw = yaw; action.territoryId = tid;
		_core.SaveConfig();
		ClearArm();
		var deg = yaw * 180f / MathF.PI;
		var msg = $"已记录位置到动作「{target}」: ({pos.X:F1},{pos.Z:F1}) 面向 {deg:F0}°";
		Plugin.Log?.Information($"[状态机] {msg}");
		return msg;
	}

	/// <summary>清空某动作的位置(前端「清空位置」)。</summary>
	public string ClearPosition(int setId, int stateId, string actionName)
	{
		var st = _config.SmSets.FirstOrDefault(x => x.id == setId)?.states.FirstOrDefault(m => m.id == stateId);
		var a = st?.actions.FirstOrDefault(x => x.name == actionName);
		if (a == null) return "找不到该动作";
		a.hasPos = false;
		_core.SaveConfig();
		return $"已清空「{actionName}」的位置";
	}

	// ==================== 给 AI 的状态说明(注入 system) ====================

	/// <summary>状态机说明文本(未开启返回空串;供 BuildSceneSnippet 注入)。</summary>
	public string DescribeStateForAi()
	{
		if (!_config.StateMachineEnabled) return "";
		var sb = new StringBuilder();
		sb.Append("## 状态机(你当前的状态,切换用工具)\n");
		var set = CurrentSet;
		if (set == null || set.states.Count == 0)
		{
			sb.Append("- 还没有配置状态;不用管状态机。\n");
			return sb.ToString();
		}
		var cur = CurrentState;
		sb.Append($"- 当前状态机:「{set.name}」\n");
		sb.Append("- 全部状态及其人设(切换用 switch_state):\n");
		foreach (var st in set.states)
		{
			var role = string.IsNullOrEmpty(st.roleName) ? "不演角色" : st.roleName;
			sb.Append($"  · {st.name}" + (string.IsNullOrWhiteSpace(st.desc) ? "" : $"({st.desc})") + $" → 人设: {role}\n");
		}
		if (cur == null)
		{
			sb.Append("- 当前没有选定状态;请先用 switch_state 选一个。\n");
			return sb.ToString();
		}
		sb.Append($"- 当前状态:{cur.name}" + (string.IsNullOrWhiteSpace(cur.desc) ? "" : $" —— {cur.desc}") + "\n");
		sb.Append($"- 本状态人设:{(string.IsNullOrEmpty(cur.roleName) ? "(未设置,不演角色照常聊天)" : cur.roleName)}\n");
		var allowed = AllowedStates();
		if (allowed.Count > 0)
			sb.Append($"- 可切换到的状态(switch_state,只有这些): {Names(allowed, x => x.name)}\n");
		else
			sb.Append("- 当前状态不能切换到别的状态(没勾选互通)。\n");
		if (cur.actions.Count > 0)
		{
			sb.Append("- 本状态动作(会自动轮换,也可用 rp_idle_action 指定):");
			foreach (var a in cur.actions)
			{
				var d = new List<string>();
				if (!string.IsNullOrWhiteSpace(a.emote)) d.Add($"动作:{a.emote}");
				if (a.cooldown > 0) d.Add($"停留{a.cooldown}秒");
				if (a.hasPos) d.Add("有位置(会先走过去)");
				sb.Append($" {a.name}" + (d.Count > 0 ? $"({string.Join(",", d)})" : "") + ";");
			}
			sb.Append('\n');
		}
		sb.Append("用法:这是你当前的处境/身份,不是台词素材。处境/身份/心情变了就 switch_state 切到对应状态。\n");
		sb.Append("- 什么时候算「变了」以各状态的说明(—— 后面那段)为准。例:说明里提到「以角色身份/角色扮演/上皮」的状态,就是有人要求你进入角色时要切过去的那个;提到「没有在扮演角色/皮下」的状态,就是对方出戏、要聊现实或游戏本身时切过去的那个。\n");
		sb.Append("- 别人要求你切换身份/进入角色/出戏时,不要拒绝、不要解释、也不要反问,直接调用工具切过去,再按新状态的口吻说话。\n");
		sb.Append("- 但**别频繁来回切**:对方没要求、处境也没变,就保持在当前状态;一轮最多切一次。\n");
		sb.Append("- 只能切到「可切换到的状态」列出的那几个;切不过去就保持原状态,不要说“我换好了”。\n");
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

	public static string DisplayName(IdleAction a)
		=> string.IsNullOrWhiteSpace(a.name) ? (string.IsNullOrWhiteSpace(a.emote) ? "(未命名)" : a.emote) : a.name;
}
