namespace AuraCanAI.Dalamud.Core;

using System.Text.RegularExpressions;

/// <summary>
/// 行为引擎:跟随 500ms 轮询求值所有行为规则。
/// - 边沿触发:条件从「不满足→满足」跳变时触发一次
/// - 冷却:cooldown 秒内不重复触发(默认 20,0 = 无冷却)
/// - 动作队列:最多保留 3 个动作,每个保留 5 秒,超时移除,队列满跳过
/// - 触发成功后日志留痕;条目勾选「聊天内提示」时额外 /e 提示
/// Tick 必须在游戏框架线程调用(由 AuraCanAiCore 定时器保证)。
/// </summary>
public class BehaviorEngine
{
	private readonly AuraCanAiCore _core;
	private List<BehaviorRule> _rules = new();
	private readonly List<PendingAction> _queue = new(); // 即时宏触发队列(3 个/5 秒/满跳过)
	private readonly List<DelayedAction> _delayed = new(); // after 延迟任务(look 或延迟动作,不占队列)
	private readonly List<MacroChain> _chains = new(); // 多宏连发链(trigger 1,2,3,逐个执行,不占队列)
	private bool _macroFiredThisTick; // 本 tick 是否已触发过宏(延迟/队列/链共用,防同帧连发)

	private const int MaxQueue = 3; // 队列最多 3 个动作
	private static readonly TimeSpan QueueTtl = TimeSpan.FromSeconds(5); // 动作在队列内最多保留 5 秒
	private const int ChainGapMs = 300; // 多宏链:上一个宏结束后再等待的毫秒数(等执行器状态清空)
	private static readonly TimeSpan ChainTtl = TimeSpan.FromSeconds(300); // 多宏链最长存活(防异常卡死)

	// 启动保护期:插件刚启动时,进出场/附近玩家等条件会因场景初始化而立即满足,
	// 为避免每次重启都触发一堆动作,启动后这段时间内丢弃所有动作(状态机照常更新)。
	private readonly DateTime _startedAt = DateTime.Now;
	private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(5);

	// 配置重载预热:Reload 后首次 Tick 把当前已满足的条件视为已触发(丢弃动作),
	// 避免"改了一个行为导致其他满足条件的行为全部重跑"(WasTrue 重置导致的假边沿)。
	private bool _warmingUp = true;

	/// <summary>是否仍在启动保护期内(期间触发动作全部丢弃)</summary>
	public bool InStartupGrace => DateTime.Now - _startedAt < StartupGrace;

	public int RuleCount => _rules.Count;
	public int QueueCount => _queue.Count;

	private sealed class PendingAction
	{
		public BehaviorRule Rule = null!;
		public DateTime EnqueuedAt;
	}

	private sealed class DelayedAction
	{
		public BehaviorRule Rule = null!;
		public DateTime DueTime;
	}

	/// <summary>多宏连发链运行时状态(trigger 1,2,3:执行完前一个再执行下一个)</summary>
	private sealed class MacroChain
	{
		public BehaviorRule Rule = null!;
		public List<MacroSpec> Macros = null!;
		public int NextIndex; // 下一个待执行宏下标
		public DateTime LastAttemptAt; // 上次执行尝试时间(间隔保护)
		public bool Started; // 是否已执行过第一个宏
	}

	public BehaviorEngine(AuraCanAiCore core) => _core = core;

	/// <summary>从配置重载所有行为(UI 新增/编辑/删除/启停后调用)。重新编译,运行时状态重置,
	/// 清空残留调度队列,并置预热标记(下次 Tick 吞掉当前已满足的条件,防止全部重跑)。</summary>
	public void Reload()
	{
		var rules = new List<BehaviorRule>();
		try
		{
			foreach (var item in _core.GetBehaviorItems())
			{
				if (!item.enabled) continue;
				var parsed = BehaviorParser.Parse(item.definition, item.id, item.comment, item.chatNotice, item.skipOnLeave, item.skipOnCombat);
				rules.AddRange(parsed.Rules);
				foreach (var err in parsed.Errors)
					Plugin.Log?.Warning($"行为#{item.id} 定义有误: {err}");
			}
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"行为加载失败: {e}");
		}
		_rules = rules;
		_queue.Clear();
		_delayed.Clear();
		_chains.Clear();
		_warmingUp = true; // 下次 Tick 预热:当前已满足的条件视为已触发(不执行动作)
	}

	/// <summary>500ms 轮询(框架线程):求值 + 触发 + 队列泵送</summary>
	public void Tick()
	{
		var warm = _warmingUp; // 本次 Tick 为预热(仅一次):当前已满足的条件视为已触发,不执行动作
		_warmingUp = false;
		var grace = InStartupGrace;
		_macroFiredThisTick = false; // 重置本 tick 宏触发名额(延迟/队列/链共用,最多一次)
		foreach (var rule in _rules)
		{
			try
			{
				var now = EvalRule(rule);
				if (now && !rule.WasTrue && DateTime.Now >= rule.CooldownUntil)
				{
					// 「need 不满足」「离开时不触发」「战斗中不触发」:命中任一 → 丢弃(不触发、不设冷却、状态照常更新)
					var skipReason = "";
					var needMiss = EvalNeed(rule);
					if (needMiss.Length > 0)
						skipReason = $"need 条件不满足({needMiss})";
					else
						skipReason = (rule.SkipOnLeave && _core.IsPlayerAway(), rule.SkipOnCombat && _core.IsInCombat()) switch
						{
							(true, _) => "你的状态为「离开」",
							(_, true) => "战斗中",
							_ => "",
						};
					if (skipReason.Length > 0)
					{
						Plugin.Log?.Information($"行为[{RuleLabel(rule)}]触发已丢弃({skipReason})");
						if (rule.ChatNotice) _core.ChatNotice($"[行为] {RuleLabel(rule)} → 丢弃({skipReason})");
					}
					else
					{
						rule.CooldownUntil = DateTime.Now.AddSeconds(rule.CooldownSec);
						// 预热 / 启动保护期内丢弃动作(不调度),但冷却照常设置、状态照常更新,
						// 避免保护期结束后因 WasTrue 未更新而立即误触发
						if (warm || grace)
						{
							Plugin.Log?.Information($"行为[{RuleLabel(rule)}]触发动作已丢弃({(warm ? "配置重载预热" : "启动保护期")})");
						}
						else
						{
							Schedule(rule);
						}
					}
				}
				rule.WasTrue = now;
			}
			catch (Exception e)
			{
				Plugin.Log?.Error($"行为规则求值异常(条目#{rule.ItemId}): {e.Message}");
				rule.WasTrue = false;
			}
		}
		PumpDelayed();
		PumpQueue();
		PumpChains();
	}

	// ==================== 动作调度与执行 ====================

	/// <summary>调度动作:say 直接执行(带延迟走延迟任务);look/移动(approach/follow/leave)或 after&gt;0 走延迟任务(不占队列);trigger 且无延迟入即时队列</summary>
	private void Schedule(BehaviorRule rule)
	{
		if (rule.ActionType == BehaviorActionType.Say)
		{
			// 发言不占宏队列,直接执行;带 after 则走延迟任务
			if (rule.AfterMin > 0 || rule.AfterMax > 0)
				AddDelayed(rule);
			else
				ExecuteAction(rule);
			return;
		}
		var isMove = rule.ActionType is BehaviorActionType.Approach or BehaviorActionType.Follow or BehaviorActionType.Leave or BehaviorActionType.Sit;
		if (rule.ActionType == BehaviorActionType.Look || isMove || rule.AfterMin > 0 || rule.AfterMax > 0)
		{
			AddDelayed(rule);
		}
		else if (rule.Macros.Count > 1)
		{
			StartChain(rule); // 多宏:入链逐个执行(不占即时队列)
		}
		else
		{
			Enqueue(rule);
		}
	}

	/// <summary>加入延迟任务(after A,B → A~B 秒随机延迟,调度时取一次随机值;after N → 固定 N 秒)</summary>
	private void AddDelayed(BehaviorRule rule)
	{
		var delay = rule.AfterMax > 0
			? rule.AfterMin + Random.Shared.NextDouble() * (rule.AfterMax - rule.AfterMin)
			: rule.AfterMin;
		_delayed.Add(new DelayedAction { Rule = rule, DueTime = DateTime.Now.AddSeconds(delay) });
	}

	/// <summary>泵送延迟任务:到期即执行(一次性;trigger 执行器忙则放弃,不排队)</summary>
	private void PumpDelayed()
	{
		for (int i = _delayed.Count - 1; i >= 0; i--)
		{
			var d = _delayed[i];
			if (DateTime.Now >= d.DueTime)
			{
				_delayed.RemoveAt(i);
				ExecuteAction(d.Rule);
			}
		}
	}

	/// <summary>执行动作:Say = 发言(变量替换/悄悄话目标/空变量跳过);Look = 选中目标;
	/// Approach/Follow/Leave = 自动移动(走近/跟随/走开,目标缺名=最近接触的人,支持 {变量});
	/// Trigger = 单宏直接触发(执行器忙则放弃),多宏入链逐个执行(trigger 1,2,3)。</summary>
	private void ExecuteAction(BehaviorRule rule)
	{
		var label = RuleLabel(rule);
		try
		{
			if (rule.ActionType == BehaviorActionType.Say)
			{
				ExecuteSay(rule);
				return;
			}
			if (rule.ActionType == BehaviorActionType.Look)
			{
				ExecuteLook(rule);
				return;
			}
			if (rule.ActionType is BehaviorActionType.Approach or BehaviorActionType.Follow or BehaviorActionType.Leave)
			{
				ExecuteMove(rule);
				return;
			}
			if (rule.ActionType == BehaviorActionType.Sit)
			{
				ExecuteSit(rule);
				return;
			}

			if (rule.Macros.Count > 1)
			{
				StartChain(rule); // 多宏 → 连发链
				return;
			}
			var spec = rule.Macros.Count == 1 ? rule.Macros[0] : null;
			if (spec == null)
			{
				Plugin.Log?.Warning($"行为[{label}]触发动作异常:宏列表为空");
				return;
			}
			FireSingleMacro(rule, spec);
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"行为[{label}]动作执行异常: {e}");
		}
	}

	/// <summary>执行坐动作(sit [座位]):选择器交给 MovementController.SitOnSeat(空=最近空座;名字/#id)。</summary>
	private void ExecuteSit(BehaviorRule rule)
	{
		var label = RuleLabel(rule);
		try
		{
			var mover = _core.Movement;
			if (mover == null) { _core.ChatNotice($"[行为] {label} → 坐下失败(引擎未就绪)"); return; }
			var msg = mover.SitOnSeat(rule.SitTarget);
			var ok = msg.Length == 0;
			if (ok)
			{
				var who = string.IsNullOrEmpty(rule.SitTarget) ? "最近的空座" : rule.SitTarget;
				Plugin.Log?.Information($"行为[{label}]开始去坐: {who}");
				if (rule.ChatNotice) _core.ChatNotice($"[行为] {label} → 去坐 {who}");
			}
			else
			{
				Plugin.Log?.Warning($"行为[{label}]去坐失败: {msg}");
				if (rule.ChatNotice) _core.ChatNotice($"[行为] {label} → 去坐失败({msg})");
			}
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"行为[{label}]坐动作异常: {e}");
		}
	}

	/// <summary>执行移动动作(approach/follow/leave):目标名支持 {变量} 替换(引用为空则留空=最近接触的人);
	/// 交给 MovementController 异步执行(结果由核心统一记录,后续 LLM 身体演出模式监听 MovementFinished)。</summary>
	private void ExecuteMove(BehaviorRule rule)
	{
		var label = RuleLabel(rule);
		try
		{
			var name = ResolveMoveTarget(rule.MoveName);
			bool ok;
			string verb;
			var mover = _core.Movement;
			if (mover == null) { _core.ChatNotice($"[行为] {label} → 移动失败(引擎未就绪)"); return; }
			switch (rule.ActionType)
			{
				case BehaviorActionType.Approach: ok = mover.Approach(name); verb = "走近"; break;
				case BehaviorActionType.Follow: ok = mover.Follow(name); verb = "跟随"; break;
				default: ok = mover.Leave(name); verb = "走开"; break;
			}
			if (ok)
			{
				var who = string.IsNullOrEmpty(name) ? "最近接触的人" : name;
				Plugin.Log?.Information($"行为[{label}]开始{verb}: {who}");
				if (rule.ChatNotice) _core.ChatNotice($"[行为] {label} → {verb} {who}");
			}
			else
			{
				Plugin.Log?.Warning($"行为[{label}]{verb}失败(已有移动/目标不存在/状态不允许/移动不可用): {name}");
				if (rule.ChatNotice) _core.ChatNotice($"[行为] {label} → {verb}失败(目标不存在或状态不允许)");
			}
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"行为[{label}]移动动作异常: {e}");
		}
	}

	/// <summary>解析移动目标名:替换 {变量}(与发言变量同源);引用变量为空 → 返回空串(引擎按"最近接触的人"处理)。</summary>
	private string ResolveMoveTarget(string raw)
	{
		var name = raw;
		foreach (var v in BehaviorSyntaxDoc.SayVars)
			name = System.Text.RegularExpressions.Regex.Replace(name, @"\{" + v.Token + @"\}", GetSayVarValue(v.Token), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		return name.Trim();
	}

	/// <summary>执行 look 动作(选中目标;不带名字 = 最近接触用户:悄悄话/注视/入场)</summary>
	private void ExecuteLook(BehaviorRule rule)
	{
		var label = RuleLabel(rule);
		try
		{
			var targetName = string.IsNullOrEmpty(rule.LookName)
				? _core.GetLatestContactUser()
				: rule.LookName;
			var ok = _core.TryLook(targetName);
			if (ok)
			{
				Plugin.Log?.Information($"行为[{label}]已选中目标: {targetName}");
				if (rule.ChatNotice) _core.ChatNotice($"[行为] {label} → 已选中 {targetName}");
			}
			else
			{
				Plugin.Log?.Warning($"行为[{label}]选中目标失败(未找到玩家): {targetName}");
			}
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"行为[{label}]选中目标异常: {e}");
		}
	}

	/// <summary>执行 say 动作(发言):替换变量 → 校验空变量跳过 → 悄悄话 t 目标(名字@服务器 或默认 last_tell) → 发送。</summary>
	private void ExecuteSay(BehaviorRule rule)
	{
		var label = RuleLabel(rule);
		try
		{
			var text = BuildSayMessage(rule, out var skipReason);
			if (text == null)
			{
				Plugin.Log?.Warning($"行为[{label}]发言已跳过: {skipReason}");
				if (rule.ChatNotice) _core.ChatNotice($"[行为] {label} → 发言已跳过({skipReason})");
				return;
			}
			var ok = _core.SendBehaviorSay(rule.SayChannel, text);
			if (ok)
			{
				var preview = text.Length > 50 ? text[..50] + "…" : text;
				Plugin.Log?.Information($"行为[{label}]已在 {rule.SayChannel} 频道发言: {preview}");
				if (rule.ChatNotice) _core.ChatNotice($"[行为] {label} → 已发言: {preview}");
			}
			else
			{
				Plugin.Log?.Warning($"行为[{label}]发言失败(未登录/频道无效): {rule.SayChannel}");
				if (rule.ChatNotice) _core.ChatNotice($"[行为] {label} → 发言失败(未登录或频道无效)");
			}
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"行为[{label}]发言异常: {e}");
		}
	}

	/// <summary>构建发言最终文本:委托 BehaviorParser.BuildSayText(纯逻辑;变量值由 GetSayVarValue 提供)。</summary>
	private string? BuildSayMessage(BehaviorRule rule, out string? skipReason)
		=> BehaviorParser.BuildSayText(rule.SayChannel, rule.SayText, GetSayVarValue, out skipReason);

	/// <summary>取发言变量值(玩家清洗名;与 BehaviorSyntaxDoc.SayVars 的 Token 对应)</summary>
	private string GetSayVarValue(string token) => token switch
	{
		"last_tell" => _core.GetLastTellUser(),
		"last_look" => _core.GetLastLookUser(),
		"last_enter" => _core.GetLastEnterUser(),
		"last_contact" => _core.GetLatestContactUser(),
		"emote_player" => _core.GetLastEmoteUser(),
		"emote_name" => _core.GetLastEmoteName(),
		_ => "",
	};

	/// <summary>触发单个宏(受「本 tick 全局最多一个宏」限制;执行器忙则放弃)。返回是否真正触发。</summary>
	private bool FireSingleMacro(BehaviorRule rule, MacroSpec spec)
	{
		var label = RuleLabel(rule);
		var macroDesc = $"{(spec.Shared ? "s" : "")}{spec.Index}";
		if (_macroFiredThisTick)
		{
			Plugin.Log?.Warning($"行为[{label}]触发宏 {macroDesc} 跳过:本帧已触发过其他宏");
			return false;
		}
		if (MacroExecutor.IsBusy())
		{
			Plugin.Log?.Warning($"行为[{label}]触发宏 {macroDesc} 失败:执行器忙");
			return false;
		}
		var ok = Plugin.TriggerMacro(spec.Index, spec.Shared);
		_macroFiredThisTick = true;
		if (ok)
		{
			Plugin.Log?.Information($"行为[{label}]已触发宏 {macroDesc}");
			if (rule.ChatNotice) _core.ChatNotice($"[行为] {label} → 已触发宏 {macroDesc}");
		}
		else
		{
			Plugin.Log?.Warning($"行为[{label}]触发宏 {macroDesc} 失败(未登录/宏为空/执行器忙)");
		}
		return ok;
	}

	/// <summary>多宏连发入链(trigger 1,2,3):加入链列表,由 PumpChains 逐个执行(执行完前一个再执行下一个)</summary>
	private void StartChain(BehaviorRule rule)
	{
		_chains.RemoveAll(c => DateTime.Now - c.LastAttemptAt > ChainTtl); // 清理卡死链
		_chains.Add(new MacroChain
		{
			Rule = rule,
			Macros = rule.Macros.ToList(),
			LastAttemptAt = DateTime.Now,
		});
		Plugin.Log?.Information($"行为[{RuleLabel(rule)}]多宏连发已入链: {MacroListDesc(rule.Macros)}");
	}

	/// <summary>泵送多宏连发链:每个 tick 最多执行一个宏;上一个宏未结束(执行器忙)则等待,
	/// 结束后间隔 ChainGapMs 再执行下一个;单个宏执行失败不阻塞,继续下一个。</summary>
	private void PumpChains()
	{
		for (int i = _chains.Count - 1; i >= 0; i--)
		{
			var c = _chains[i];
			if (c.NextIndex >= c.Macros.Count)
			{
				_chains.RemoveAt(i); // 链已执行完
				continue;
			}
			if (DateTime.Now - c.LastAttemptAt > ChainTtl)
			{
				Plugin.Log?.Warning($"行为[{RuleLabel(c.Rule)}]多宏连发超时放弃: {MacroListDesc(c.Macros)}");
				_chains.RemoveAt(i);
				continue;
			}
			if (_macroFiredThisTick) continue; // 本 tick 已触发过宏,下个 tick 再执行
			if (MacroExecutor.IsBusy()) continue; // 上一个宏还在执行(/wait 等),等待
			if (c.Started && (DateTime.Now - c.LastAttemptAt).TotalMilliseconds < ChainGapMs) continue; // 间隔保护

			var spec = c.Macros[c.NextIndex];
			c.LastAttemptAt = DateTime.Now;
			c.Started = true;
			c.NextIndex++;
			_macroFiredThisTick = true;
			var ok = Plugin.TriggerMacro(spec.Index, spec.Shared);
			var desc = $"{(spec.Shared ? "s" : "")}{spec.Index}";
			if (ok)
			{
				Plugin.Log?.Information($"行为[{RuleLabel(c.Rule)}]已触发宏 {desc}({c.NextIndex}/{c.Macros.Count})");
				if (c.NextIndex == 1 && c.Rule.ChatNotice)
					_core.ChatNotice($"[行为] {RuleLabel(c.Rule)} → 已触发宏 {MacroListDesc(c.Macros)}");
			}
			else
			{
				Plugin.Log?.Warning($"行为[{RuleLabel(c.Rule)}]触发宏 {desc} 失败(未登录/宏为空),继续下一个");
			}
		}
	}

	private static string MacroListDesc(List<MacroSpec> macros)
		=> string.Join(",", macros.Select(m => $"{(m.Shared ? "s" : "")}{m.Index}"));

	// ==================== 动作队列 ====================

	private void Enqueue(BehaviorRule rule)
	{
		_queue.RemoveAll(q => DateTime.Now - q.EnqueuedAt > QueueTtl);
		if (_queue.Count >= MaxQueue)
		{
			Plugin.Log?.Information($"行为[{RuleLabel(rule)}]触发动作被跳过:队列已满({MaxQueue})");
			return;
		}
		_queue.Add(new PendingAction { Rule = rule, EnqueuedAt = DateTime.Now });
	}

	/// <summary>泵送队列:移除超时项;执行器空闲时执行队首(每次 tick 最多一个,避免宏连发冲突)</summary>
	private void PumpQueue()
	{
		_queue.RemoveAll(q => DateTime.Now - q.EnqueuedAt > QueueTtl);
		if (_queue.Count == 0) return;
		var item = _queue[0];
		if (_macroFiredThisTick) return; // 本 tick 已触发过宏(延迟任务/连发链),下个 tick 再试
		if (MacroExecutor.IsBusy()) return; // 执行器正忙(/wait 等),下个 tick 再试,直到超时移除

		_queue.RemoveAt(0);
		var spec = item.Rule.Macros.Count == 1 ? item.Rule.Macros[0] : null;
		if (spec == null) return;
		FireSingleMacro(item.Rule, spec);
	}

	private static string RuleLabel(BehaviorRule rule)
		=> string.IsNullOrEmpty(rule.Comment) ? $"#{rule.ItemId}" : rule.Comment;

	// ==================== 条件求值 ====================

	private bool EvalRule(BehaviorRule rule)
	{
		if (rule.Conditions.Count == 0) return false;
		var vals = new List<bool>(rule.Conditions.Count);
		foreach (var c in rule.Conditions) vals.Add(EvalCond(c) ^ c.Not);
		return EvalExpr(vals, rule.Connectors);
	}

	/// <summary>need 必要条件求值:全部满足返回空字符串;否则返回第一个不满足条件的描述(供日志/聊天提示)。</summary>
	private string EvalNeed(BehaviorRule rule)
	{
		if (rule.NeedConditions.Count == 0) return "";
		var vals = new List<bool>(rule.NeedConditions.Count);
		foreach (var c in rule.NeedConditions) vals.Add(EvalCond(c) ^ c.Not);
		if (EvalExpr(vals, rule.NeedConnectors)) return "";
		for (int i = 0; i < vals.Count; i++)
			if (!vals[i]) return CondLabel(rule.NeedConditions[i]);
		return "";
	}

	/// <summary>条件中文描述(如 "not in_combat"/"in_party"),用于 need 丢弃提示</summary>
	private static string CondLabel(BehaviorCondition c)
	{
		var name = BehaviorSyntaxDoc.Conditions.FirstOrDefault(x => x.Type == c.Type)?.Name ?? c.Type.ToString();
		var suffix = c.Op != BehaviorOp.Eq || c.Value.Length == 0 ? "" : " " + c.Value;
		return $"{(c.Not ? "not " : "")}{name}{suffix}";
	}

	/// <summary>布尔表达式求值:and 优先于 or(标准优先级)。vals 长度 = conns 长度 + 1。</summary>
	private static bool EvalExpr(List<bool> vals, List<string> conns)
	{
		if (vals.Count == 0) return false;
		var groups = new List<bool>();
		var cur = vals[0];
		for (int i = 0; i < conns.Count; i++)
		{
			if (conns[i] == "and") cur = cur && vals[i + 1];
			else { groups.Add(cur); cur = vals[i + 1]; }
		}
		groups.Add(cur);
		var r = false;
		foreach (var v in groups) r = r || v;
		return r;
	}

	private bool EvalCond(BehaviorCondition c)
	{
		switch (c.Type)
		{
			case BehaviorCondType.LookingPlayer:
				return _core.GetPlayersLookingAtMe().Any(n => CompareStr(n, c.Op, c.Value));
			case BehaviorCondType.AnyoneLooking:
				return _core.GetPlayersLookingAtMe().Count > 0;
			case BehaviorCondType.LookingPlayerCount:
				return CompareNum(_core.GetPlayersLookingAtMe().Count, c.Op, c.Value);
			case BehaviorCondType.Area:
				return CompareStr(_core.GetAreaName(), c.Op, c.Value);
			case BehaviorCondType.InHousing:
				return _core.IsInHousing();
			case BehaviorCondType.RoomSize:
				return CompareStr(_core.GetRoomTypeName(), c.Op, c.Value);
			case BehaviorCondType.MyStatus:
				return CompareStr(_core.GetMyStatusName(), c.Op, c.Value);
			case BehaviorCondType.InParty:
				return _core.IsInParty();
			case BehaviorCondType.MyJob:
				return CompareStr(_core.GetMyJobName(), c.Op, c.Value);
			case BehaviorCondType.NearbyPlayerCount:
				return CompareNum(_core.GetNearbyPlayerCount(), c.Op, c.Value);
			case BehaviorCondType.TargetName:
				return CompareStr(_core.GetTargetName() ?? "", c.Op, c.Value);
			case BehaviorCondType.Time:
				return CompareTime(DateTime.Now, c.Op, c.Value);
			case BehaviorCondType.InCombat:
				return _core.IsInCombat();
			case BehaviorCondType.AnyoneEnter:
				return _core.AnyPlayerEnteredRecently();
			case BehaviorCondType.EnterPlayer:
				return CompareStr(_core.GetLastEnterUser(), c.Op, c.Value);
			case BehaviorCondType.AnyoneLeave:
				return _core.AnyPlayerLeftRecently();
			case BehaviorCondType.LeavePlayer:
				return CompareStr(_core.GetLastLeaveUser(), c.Op, c.Value);
			case BehaviorCondType.AnyoneEmoteToMe:
				return _core.AnyEmoteToMeRecently();
			case BehaviorCondType.EmoteToMePlayer:
				return CompareStr(_core.GetLastEmoteUser(), c.Op, c.Value);
			case BehaviorCondType.EmoteToMeName:
				return CompareStr(_core.GetLastEmoteName(), c.Op, c.Value);
			default:
				return false;
		}
	}

	private static bool CompareStr(string actual, BehaviorOp op, string value)
	{
		return op switch
		{
			BehaviorOp.Eq => string.Equals(actual, value, StringComparison.OrdinalIgnoreCase),
			BehaviorOp.Ne => !string.Equals(actual, value, StringComparison.OrdinalIgnoreCase),
			_ => false,
		};
	}

	private static bool CompareNum(int actual, BehaviorOp op, string value)
	{
		if (!int.TryParse(value, out var v)) return false;
		return op switch
		{
			BehaviorOp.Ge => actual >= v,
			BehaviorOp.Le => actual <= v,
			BehaviorOp.Eq => actual == v,
			BehaviorOp.Ne => actual != v,
			_ => false,
		};
	}

	private static bool CompareTime(DateTime now, BehaviorOp op, string value)
	{
		if (!TimeSpan.TryParse(value, out var t)) return false;
		var nowT = now.TimeOfDay;
		return op switch
		{
			BehaviorOp.Ge => nowT >= t,
			BehaviorOp.Le => nowT <= t,
			BehaviorOp.Eq => nowT == t,
			BehaviorOp.Ne => nowT != t,
			_ => false,
		};
	}
}
