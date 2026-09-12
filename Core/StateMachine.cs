using System.Numerics;
using System.Text;

namespace AuraCanAI.Dalamud.Core;

/// <summary>
/// 两层状态机(2026-09 新增):
///   第一层 = 角色状态/心情(如 心情很糟糕 / 非常开心 / 受伤中…),由 AI 自由切换(switch_mood);
///   第二层 = 情景模式(待机 / 对话…),只能在当前第一层内切换,且受路径 nextSceneIds 约束(switch_scene)。
/// 每个第二层绑定一个人设(roleName)并拥有自己的「动作列表」(名称/动作/冷却/位置)。
///
/// 待机动作自动轮换(用户口径):当前动作停留够「冷却」秒后,自动随机换同情景下的另一个动作;
/// 没有别的动作就一直保持当前动作。动作若带位置,先寻路走过去,到位后再播表情。
/// 位置由 /aca pos [动作名] 在游戏内记录(空动作名 = 写进前端「记录位置」武装的那条)。
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
	public int ArmedMoodId { get; private set; }
	public int ArmedSceneId { get; private set; }
	public string ArmedActionName { get; private set; } = "";

	public StateMachine(AuraCanAiCore core, Configuration config)
	{
		_core = core;
		_config = config;
	}

	// ==================== 当前状态访问 ====================

	public bool Enabled => _config.StateMachineEnabled;
	public SmSet? CurrentSet => _config.SmSets.FirstOrDefault(s => s.id == _config.SmCurrentSetId);
	public SmMood? CurrentMood => CurrentSet?.moods.FirstOrDefault(m => m.id == _config.SmCurrentMoodId);
	public SmScene? CurrentScene => CurrentMood?.scenes.FirstOrDefault(s => s.id == _config.SmCurrentSceneId);
	public string CurrentMoodName => CurrentMood?.name ?? "";
	public string CurrentSceneName => CurrentScene?.name ?? "";

	/// <summary>当前情景使用的角色名(未设置返回空串)。</summary>
	public string CurrentSceneRole => CurrentScene?.roleName ?? "";

	/// <summary>当前情景下允许切换到的情景(路径为空 = 同第一层内不限)。</summary>
	public List<SmScene> AllowedScenes()
	{
		var mood = CurrentMood;
		if (mood == null) return new List<SmScene>();
		var cur = CurrentScene;
		if (cur == null || cur.nextSceneIds == null || cur.nextSceneIds.Count == 0) return mood.scenes;
		return mood.scenes.Where(s => cur.nextSceneIds.Contains(s.id)).ToList();
	}

	/// <summary>清空待机轮换运行时(切换状态/保存配置后调用;下一次 Tick 会重新开始)。</summary>
	public void ResetIdle()
	{
		_idleActionName = "";
		_idleWaitingMove = false;
		_idleNextSwitchAt = DateTime.MinValue;
	}

	// ==================== 切换 ====================

	/// <summary>切换第一层(角色状态)——自由切换,不受路径限制。</summary>
	public (bool ok, string message) SwitchMood(string key)
	{
		var set = CurrentSet;
		if (set == null || set.moods.Count == 0) return (false, "当前状态机还没有配置第一层(角色状态)");
		var m = Match(set.moods, key, x => x.name);
		if (m == null) return (false, $"没有叫「{key}」的角色状态;可用: {Names(set.moods, x => x.name)}");
		if (_config.SmCurrentMoodId == m.id) return (true, $"已经处于「{m.name}」状态" + (m.scenes.Count > 0 ? $";该状态下有情景: {Names(m.scenes, x => x.name)}" : ";(还没有配置情景)"));
		_config.SmCurrentMoodId = m.id;
		_config.SmCurrentSceneId = m.scenes.Count > 0 ? m.scenes[0].id : 0;
		ApplyStateChange($"第一层 → {m.name}");
		var tail = CurrentScene != null ? $";当前情景「{CurrentScene.name}」" : ";该状态还没有配置情景";
		return (true, $"已切换到「{m.name}」(角色状态){tail}");
	}

	/// <summary>切换第二层(情景)——必须在当前第一层内,且按路径约束。</summary>
	public (bool ok, string message) SwitchScene(string key)
	{
		var mood = CurrentMood;
		if (mood == null) return (false, "状态机没有当前第一层状态(先用 switch_mood 选一个)");
		var s = Match(mood.scenes, key, x => x.name);
		if (s == null)
			return (false, $"「{mood.name}」下没有叫「{key}」的情景;可用: {Names(mood.scenes, x => x.name)}");
		var cur = CurrentScene;
		if (cur != null && cur.id == s.id) return (true, $"已经处于情景「{s.name}」");
		if (cur != null && cur.nextSceneIds != null && cur.nextSceneIds.Count > 0 && !cur.nextSceneIds.Contains(s.id))
			return (false, $"从「{cur.name}」不能直接切到「{s.name}」;当前只能切到: {Names(AllowedScenes(), x => x.name)}");
		_config.SmCurrentSceneId = s.id;
		var role = string.IsNullOrEmpty(s.roleName) ? "(未设置人设,不会说话)" : s.roleName;
		ApplyStateChange($"第二层 → {s.name}(人设: {role})");
		var actInfo = s.actions.Count > 0 ? $";本情景有 {s.actions.Count} 个动作" : "";
		return (true, $"已切换到情景「{s.name}」(人设: {role}){actInfo}");
	}

	private void ApplyStateChange(string logText)
	{
		_core.SaveConfig();
		ResetIdle();
		_core.ResetChatHistoryPublic(); // 第二层换了人设 → 重建 system 提示
		Plugin.Log?.Information($"[状态机] {logText}");
	}

	// ==================== 待机动作轮换(500ms 框架线程) ====================

	/// <summary>每 500ms 推进一步:到点随机换下一个动作并执行(有位置先走过去)。</summary>
	public void Tick()
	{
		if (!_config.StateMachineEnabled) return;
		var scene = CurrentScene;
		var actions = scene?.actions ?? new List<IdleAction>();
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
		var scene = CurrentScene;
		if (scene == null) return "状态机没有当前情景";
		var actions = scene.actions ?? new List<IdleAction>();
		if (actions.Count == 0) return $"情景「{scene.name}」没有配置动作列表";
		if (string.IsNullOrWhiteSpace(name)) { var r = PickRandom(actions, _idleActionName); StartAction(r, actions); return $"成功:开始做「{r.name}」"; }
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
	public void ArmPos(int setId, int moodId, int sceneId, string actionName)
	{
		ArmedSetId = setId;
		ArmedMoodId = moodId;
		ArmedSceneId = sceneId;
		ArmedActionName = actionName ?? "";
	}

	public void ClearArm() => ArmPos(0, 0, 0, "");

	/// <summary>记录当前位置到动作的「位置」字段(/aca pos [动作名];不带名 = 用武装的那条)。</summary>
	public string RecordPosition(string actionName)
	{
		(int setId, int moodId, int sceneId, string target) = (-1, -1, -1, (actionName ?? "").Trim());
		if (target.Length == 0)
		{
			if (string.IsNullOrEmpty(ArmedActionName))
				return "没有指定动作:请带动作名(/aca pos 坐下),或先到网页状态机里点该动作的「记录位置」";
			(setId, moodId, sceneId, target) = (ArmedSetId, ArmedMoodId, ArmedSceneId, ArmedActionName);
		}
		var tf = _core.GetLocalTransform();
		if (tf == null) return "未登录/无玩家,无法记录位置";
		var (pos, yaw, tid) = tf.Value;

		IdleAction? action = null;
		SmScene? scene;
		if (moodId > 0)
		{
			scene = _config.SmSets.FirstOrDefault(x => x.id == setId)?.moods.FirstOrDefault(m => m.id == moodId)?.scenes.FirstOrDefault(s => s.id == sceneId);
			action = scene?.actions.FirstOrDefault(a => a.name == target);
		}
		else
		{
			scene = CurrentScene;
			action = scene?.actions.FirstOrDefault(a => a.name == target)
				?? _config.SmSets.SelectMany(x => x.moods).SelectMany(m => m.scenes).SelectMany(s => s.actions).FirstOrDefault(a => a.name == target);
		}
		if (action == null) return $"找不到动作「{target}」(要在当前情景的动作列表里,名字要完全一致)";

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
	public string ClearPosition(int setId, int moodId, int sceneId, string actionName)
	{
		var scene = _config.SmSets.FirstOrDefault(x => x.id == setId)?.moods.FirstOrDefault(m => m.id == moodId)?.scenes.FirstOrDefault(s => s.id == sceneId);
		var a = scene?.actions.FirstOrDefault(x => x.name == actionName);
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
		var setNow = CurrentSet;
		if (setNow == null || setNow.moods.Count == 0)
		{
			sb.Append("- 还没有配置角色状态;不用管状态机。\n");
			return sb.ToString();
		}
		var mood = CurrentMood;
		var scene = CurrentScene;
		sb.Append($"- 当前状态机:「{setNow.name}」;全部角色状态(第一层,用 switch_mood 自由切换): {Names(setNow.moods, x => x.name)}\n");
		if (mood == null)
		{
			sb.Append("- 当前没有选定状态;请先用 switch_mood 选一个。\n");
			return sb.ToString();
		}
		sb.Append($"- 当前角色状态:{mood.name}" + (string.IsNullOrWhiteSpace(mood.desc) ? "" : $" —— {mood.desc}") + "\n");
		if (scene == null)
		{
			sb.Append($"- 该状态下还没有情景;可用 switch_scene 选择:{Names(mood.scenes, x => x.name)}\n");
			return sb.ToString();
		}
		sb.Append($"- 当前情景:{scene.name}" + (string.IsNullOrWhiteSpace(scene.desc) ? "" : $" —— {scene.desc}") + "\n");
		sb.Append($"- 本情景人设:{(string.IsNullOrEmpty(scene.roleName) ? "(未设置,你不会说话)" : scene.roleName)}\n");
		var allowed = AllowedScenes();
		if (allowed.Count > 0)
			sb.Append($"- 可切换的情景(switch_scene,只能用这些): {Names(allowed, x => x.name)}\n");
		else
			sb.Append("- 当前情景不允许切换到别的情景。\n");
		if (scene.actions.Count > 0)
		{
			sb.Append("- 本情景动作(会自动轮换,也可用 rp_idle_action 指定):");
			foreach (var a in scene.actions)
			{
				var d = new List<string>();
				if (!string.IsNullOrWhiteSpace(a.emote)) d.Add($"动作:{a.emote}");
				if (a.cooldown > 0) d.Add($"停留{a.cooldown}秒");
				if (a.hasPos) d.Add("有位置(会先走过去)");
				sb.Append($" {a.name}" + (d.Count > 0 ? $"({string.Join(",", d)})" : "") + ";");
			}
			sb.Append('\n');
		}
		sb.Append("用法:心情/处境变了(开心、受伤、被冷落…)就 switch_mood;同一状态下换个情形(待机↔对话)用 switch_scene。切换状态会重置这段对话记忆。动作绝不写进台词。");
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
