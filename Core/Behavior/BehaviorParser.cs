using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AuraCanAI.Dalamud.Core;

/// <summary>
/// 行为定义文本解析器。语法(关键字/操作符英文,值中文):
///   trigger 2 after 5 when area = 穹顶皓天私人别墅 and my_status = 角色扮演中 and not in_party cooldown 20
///   trigger 1,2,3 when anyone_looking cooldown 0   (逗号分割多个宏,执行完前一个再执行下一个)
///   trigger 1 after 3,5 when anyone_looking        (after A,B = A~B 秒随机延迟,支持小数)
///   say p 欢迎{last_enter} when anyone_enter       (在指定频道发言,内容可用 {变量} 引用玩家名)
///   look 奥·乌儿 when anyone_looking cooldown 0
/// - 动作(when 前只能有一个,三选一):trigger N(0-99)/ trigger sN(共享宏)触发宏,多个宏用逗号分割(trigger 1,2,3)逐个执行;say 频道 内容 发言(如 say p 你好 / say t 你好 默认回最后悄悄话的人,指定对象用 say t 名字@服务器 内容);或 look [名字](选中目标,不带名字 = 最近接触你的人:悄悄话/注视/入场)
/// - 连接:when / need / and / or / not(not 作用于单个条件)
/// - 必要过滤:need 条件(可选,when 之后,固定顺序 when ... need ...):need 后的条件不满足时本次触发直接丢弃(不触发、不设冷却、状态照常更新),
///   与 when 的 and 区别:写进 when 的 and 参与边沿检测(战斗中进人没触发、出战斗会补触发);need 是当时状态过滤(进人瞬间在战斗 → 丢弃,出战斗不补触发)
/// - 比较:= 等于、!= 不等于、&gt;= / &lt;= 数值或时间
/// - 延迟:after N 秒后执行动作(不占队列);after A,B = A~B 秒随机延迟(支持小数,如 after 0.5,3)
/// - 冷却:cooldown N 秒(默认 20,0 = 无冷却,勿写等号)
/// - 多段:半角分号 ; 分割,每段 = 一条独立规则(中文全角分号 ; 不支持,会报错提示)
/// </summary>
public static class BehaviorParser
{
	private static readonly Regex CooldownRe = new(@"\bcooldown\s+(\d+)", RegexOptions.IgnoreCase);
	private static readonly Regex CooldownAnyRe = new(@"\bcooldown\b", RegexOptions.IgnoreCase);
	private static readonly Regex WhenRe = new(@"\bwhen\b", RegexOptions.IgnoreCase);
	private static readonly Regex NeedRe = new(@"\bneed\b", RegexOptions.IgnoreCase);
	private static readonly Regex AfterRe = new(@"\bafter\s+(\d+(?:\.\d+)?)(?:\s*[,，]\s*(\d+(?:\.\d+)?))?", RegexOptions.IgnoreCase);
	private static readonly Regex AfterBadRe = new(@"\bafter\b\s*\d+(?:\.\d+)?(?:\s*[,，](?!\s*[\d])|\s+\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
	private static readonly Regex TriggerRe = new(@"^\s*trigger\b", RegexOptions.IgnoreCase);
	private static readonly Regex SayRe = new(@"^\s*say\s+(\S+)\s+(.+)$", RegexOptions.IgnoreCase);
	private static readonly Regex MacroSpecRe = new(@"^(s?)(\d+)$", RegexOptions.IgnoreCase);
	private static readonly Regex ConnRe = new(@"\s+(and|or|not)\s+", RegexOptions.IgnoreCase);
	private static readonly Regex CondRe = new(@"^(?<name>[a-z_]+)(?:\s*(?<op>!=|>=|<=|=)\s*(?<val>.*))?$", RegexOptions.IgnoreCase);

	/// <summary>解析整段定义文本(可能含多条 ; 分隔的规则)。错误列表按段号给出,UI 据此红字提示。</summary>
	public static BehaviorParseResult Parse(string definition, int itemId, string comment, bool chatNotice, bool skipOnLeave, bool skipOnCombat)
	{
		var result = new BehaviorParseResult();
		var segments = (definition ?? "").Split(';');
		for (int i = 0; i < segments.Length; i++)
		{
			var seg = segments[i].Trim();
			if (seg.Length == 0) continue;
			var rule = ParseSegment(seg, itemId, comment, chatNotice, skipOnLeave, skipOnCombat, i + 1, result.Errors);
			if (rule != null) result.Rules.Add(rule);
		}
		return result;
	}

	/// <summary>解析单段规则。失败时向 errors 写入「第N段:...」并返回 null。</summary>
	private static BehaviorRule? ParseSegment(string seg, int itemId, string comment, bool chatNotice, bool skipOnLeave, bool skipOnCombat, int segNo, List<string> errors)
	{
		void Err(string msg) => errors.Add($"第{segNo}段: {msg}");

		// 1. 提取 cooldown(默认 20);cooldown 关键字存在但格式不对(如 cooldown = 30)直接报错,避免混进条件值
		var cooldown = 20;
		var cm = CooldownRe.Match(seg);
		if (cm.Success)
		{
			cooldown = int.Parse(cm.Groups[1].Value);
			seg = CooldownRe.Replace(seg, " ");
		}
		else if (CooldownAnyRe.IsMatch(seg))
		{
			Err("cooldown 格式错误:应为 cooldown N(如 cooldown 30,不要加等号)");
			return null;
		}

		// 2. 定位 when,分割动作/条件
		var wm = WhenRe.Match(seg);
		if (!wm.Success) { Err("缺少 when 关键字"); return null; }
		var actionPart = seg[..wm.Index].Trim();
		var condPart = seg[(wm.Index + wm.Length)..].Trim();

		// 全角分号只允许出现在 say 的内容里;其他位置 = 误用(想用中文分号分隔多条规则)
		if (condPart.Contains('；'))
		{
			Err("条件里检测到全角分号「;」:多条规则请用半角分号「;」分隔(中文输入法下切换英文输入再打分号)");
			return null;
		}
		if (!actionPart.StartsWith("say", StringComparison.OrdinalIgnoreCase) && actionPart.Contains('；'))
		{
			Err("检测到全角分号「;」:多条规则请用半角分号「;」分隔;中文分号只允许用在 say 的内容里");
			return null;
		}

		// 2.5 定位 need(可选,固定顺序 when ... need ...):need 后的条件 = 必要条件,不满足时直接丢弃
		//     (与 when 的 and 区别:need 不参与边沿检测,是触发时刻的状态过滤)
		var whenCondPart = condPart;
		var needCondPart = "";
		var nm = NeedRe.Match(condPart);
		if (nm.Success)
		{
			whenCondPart = condPart[..nm.Index].Trim();
			needCondPart = condPart[(nm.Index + nm.Length)..].Trim();
			if (whenCondPart.Length == 0) { Err("when 后面缺少条件(need 前必须有 when 条件作为触发)"); return null; }
			if (needCondPart.Length == 0) { Err("need 后面缺少条件(如 when anyone_enter need not in_combat)"); return null; }
			if (NeedRe.IsMatch(needCondPart))
			{
				Err("need 只能出现一次:多个必要条件请用 and/or 连接(如 need not in_party and not in_combat)");
				return null;
			}
		}

		// 3. 动作(when 前只能有一个):trigger N / look [名字];先提取 after N(延迟执行;after A,B = A~B 秒随机)
		var rule = new BehaviorRule
		{
			ItemId = itemId,
			Comment = comment,
			ChatNotice = chatNotice,
			SkipOnLeave = skipOnLeave,
			SkipOnCombat = skipOnCombat,
			CooldownSec = cooldown,
		};
		// after 关键字存在但区间残缺/缺逗号等格式错误,先于正常解析报错(避免残留内容混入动作解析)
		if (AfterBadRe.IsMatch(actionPart))
		{
			Err("after 格式错误:应为 after N(如 after 3)或 after A,B(如 after 3,5,支持小数)");
			return null;
		}
		var afterM = AfterRe.Match(actionPart);
		if (afterM.Success)
		{
			if (!double.TryParse(afterM.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var min) || min < 0)
			{
				Err("after 延迟值格式错误:应为非负数字(如 after 3 或 after 3,5,支持小数)");
				return null;
			}
			if (afterM.Groups[2].Success)
			{
				if (!double.TryParse(afterM.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var max) || max < min)
				{
					Err("after 区间格式错误:应为 after A,B 且 A ≤ B(如 after 3,5)");
					return null;
				}
				rule.AfterMin = min;
				rule.AfterMax = max;
			}
			else
			{
				rule.AfterMin = min;
			}
			actionPart = AfterRe.Replace(actionPart, " ").Trim();
		}
		else if (!actionPart.StartsWith("look", StringComparison.OrdinalIgnoreCase)
				 && !actionPart.StartsWith("approach", StringComparison.OrdinalIgnoreCase)
				 && !actionPart.StartsWith("follow", StringComparison.OrdinalIgnoreCase)
				 && !actionPart.StartsWith("leave", StringComparison.OrdinalIgnoreCase)
				 && Regex.IsMatch(actionPart, @"\bafter\b", RegexOptions.IgnoreCase))
		{
			// after 关键字残留(trigger 动作里出现 after 但未匹配成功 = 格式错误;
			// look/approach/follow/leave 的目标名可能含 after 单词,不在此检查)
			Err("after 格式错误:应为 after N(如 after 3)或 after A,B(如 after 3,5,支持小数)");
			return null;
		}
		actionPart = actionPart.Trim();

		if (actionPart.StartsWith("look", StringComparison.OrdinalIgnoreCase))
		{
			// look [名字]:不带名字 = 最近接触你的人(悄悄话/注视/入场)
			rule.ActionType = BehaviorActionType.Look;
			rule.LookName = actionPart.Length > 4 ? actionPart[4..].Trim() : "";
		}
		else if (actionPart.StartsWith("approach", StringComparison.OrdinalIgnoreCase)
			|| actionPart.StartsWith("follow", StringComparison.OrdinalIgnoreCase)
			|| actionPart.StartsWith("leave", StringComparison.OrdinalIgnoreCase))
		{
			// 移动动作 approach/follow/leave [名字]:走近/跟随/走开(缺名或空变量 = 最近接触的人;名字可用 {变量})
			var isApp = actionPart.StartsWith("approach", StringComparison.OrdinalIgnoreCase);
			var isFol = !isApp && actionPart.StartsWith("follow", StringComparison.OrdinalIgnoreCase);
			rule.ActionType = isApp ? BehaviorActionType.Approach : isFol ? BehaviorActionType.Follow : BehaviorActionType.Leave;
			var verbLen = isApp ? 8 : isFol ? 6 : 5;
			rule.MoveName = actionPart.Length > verbLen ? actionPart[verbLen..].Trim() : "";
			if (rule.MoveName.Length > 0)
			{
				foreach (Match m in Regex.Matches(rule.MoveName, @"\{([a-zA-Z_]+)\}"))
				{
					var tok = m.Groups[1].Value.ToLowerInvariant();
					if (!BehaviorSyntaxDoc.SayVars.Any(v => v.Token == tok))
					{
						Err($"目标里未知变量 {{{tok}}}(可用: {string.Join(" / ", BehaviorSyntaxDoc.SayVars.Select(v => "{" + v.Token + "}"))})");
						return null;
					}
				}
			}
		}
		else if (actionPart.StartsWith("sit", StringComparison.OrdinalIgnoreCase))
		{
			// sit [座位]:去坐场景设定里的座位(空 = 最近空座;支持 名字 或 #id)
			rule.ActionType = BehaviorActionType.Sit;
			rule.SitTarget = actionPart.Length > 3 ? actionPart[3..].Trim() : "";
		}
		else if (actionPart.StartsWith("say", StringComparison.OrdinalIgnoreCase))
		{
			// say 频道 内容:在指定频道发言(内容可用 {变量} 引用玩家名;悄悄话 t:内容开头 名字@服务器 发固定人,否则发 last_tell)
			var sm = SayRe.Match(actionPart);
			if (!sm.Success)
			{
				Err("say 格式错误:应为 say 频道 内容(如 say p 你好 或 say t 你好)");
				return null;
			}
			var ch = sm.Groups[1].Value;
			var info = BehaviorSyntaxDoc.SayChannels.FirstOrDefault(c => c.Short.Equals(ch, StringComparison.OrdinalIgnoreCase));
			if (info == null)
			{
				Err($"频道 \"{ch}\" 不存在(支持: {string.Join(" / ", BehaviorSyntaxDoc.SayChannels.Select(c => c.Short))})");
				return null;
			}
			rule.ActionType = BehaviorActionType.Say;
			rule.SayChannel = info.Short;
			rule.SayText = sm.Groups[2].Value.Trim();
			if (rule.SayText.Length == 0)
			{
				Err("say 内容不能为空(如 say p 你好)");
				return null;
			}
			// 变量占位符必须是已知变量
			foreach (Match m in Regex.Matches(rule.SayText, @"\{([a-zA-Z_]+)\}"))
			{
				var tok = m.Groups[1].Value.ToLowerInvariant();
				if (!BehaviorSyntaxDoc.SayVars.Any(v => v.Token == tok))
				{
					Err($"发言内容里未知变量 {{{tok}}}(可用: {string.Join(" / ", BehaviorSyntaxDoc.SayVars.Select(v => "{" + v.Token + "}"))})");
					return null;
				}
			}
		}
		else
		{
			var am = TriggerRe.Match(actionPart);
			if (!am.Success)
			{
				Err($"动作格式应为 trigger N(0-99,共享宏 sN;多个宏用逗号 trigger 1,2,3)、say 频道 内容、look [名字]、approach [玩家名]、follow [玩家名] 或 leave [玩家名],实际: \"{actionPart}\"");
				return null;
			}
			// 宏列表:逗号分割,逐个执行(trigger 1, s2, 3 → 依次触发 1、共享2、3)
			var listText = actionPart[am.Length..].Trim();
			if (listText.Length == 0)
			{
				Err("trigger 后缺少宏编号(如 trigger 2 或 trigger 1,2,3)");
				return null;
			}
			foreach (var raw in listText.Split(',', '，'))
			{
				var specText = raw.Trim();
				if (specText.Length == 0)
				{
					Err("宏列表格式错误:逗号后应有宏编号(如 trigger 1,2,3)");
					return null;
				}
				var sm = MacroSpecRe.Match(specText);
				if (!sm.Success)
				{
					Err($"宏编号 \"{specText}\" 格式错误:应为 0-99 数字,共享宏加 s 前缀(如 s2);when 前只能有一个动作(trigger 或 look)");
					return null;
				}
				if (!int.TryParse(sm.Groups[2].Value, out var macroIdx) || macroIdx < 0 || macroIdx > 99)
				{
					Err("宏编号需在 0-99 之间");
					return null;
				}
				rule.Macros.Add(new MacroSpec { Index = macroIdx, Shared = sm.Groups[1].Value.Length > 0 });
			}
		}

		// 4. 条件表达式:when 触发条件(必须) + need 必要条件(可选),语法一致(and/or/not、比较符)
		if (!TryParseCondExpr(whenCondPart, "when", Err, out var conditions, out var connectors)) return null;
		rule.Conditions.AddRange(conditions);
		rule.Connectors.AddRange(connectors);
		if (needCondPart.Length > 0)
		{
			if (!TryParseCondExpr(needCondPart, "need", Err, out var needConds, out var needConns)) return null;
			rule.NeedConditions.AddRange(needConds);
			rule.NeedConnectors.AddRange(needConns);
		}
		return rule;
	}

	/// <summary>解析一段条件表达式(when 或 need 共用):按 and/or/独立 not 分割,逐个解析条件,
	/// 返回条件列表与连接词列表;失败时调用 err 写错误并返回 false。</summary>
	private static bool TryParseCondExpr(string expr, string label, Action<string> err, out List<BehaviorCondition> conditions, out List<string> connectors)
	{
		conditions = new List<BehaviorCondition>();
		connectors = new List<string>();
		expr = expr.Trim();
		if (expr.Length == 0) { err($"{label} 后面缺少条件"); return false; }
		var parts = new List<string>();
		var partNegate = new List<bool>(); // 平行于 connectors:该连接词为 not 时,其后的条件取反
		int last = 0;
		foreach (Match m in ConnRe.Matches(expr))
		{
			parts.Add(expr[last..m.Index].Trim());
			var sep = m.Groups[1].Value.ToLowerInvariant();
			if (sep == "not") { connectors.Add("and"); partNegate.Add(true); } // not 分割 = 隐式 and + 其后条件取反
			else { connectors.Add(sep); partNegate.Add(false); }
			last = m.Index + m.Length;
		}
		parts.Add(expr[last..].Trim());

		for (int i = 0; i < parts.Count; i++)
		{
			var cond = ParseCondition(parts[i]);
			if (cond == null)
			{
				err($"{label} 条件 \"{parts[i]}\" 无法解析(支持: {string.Join(" / ", BehaviorSyntaxDoc.Conditions.Select(c => c.Name))})");
				return false;
			}
			if (i > 0 && partNegate[i - 1]) cond.Not = !cond.Not; // not 连接词:其后条件取反
			conditions.Add(cond);
		}
		return true;
	}

	/// <summary>解析单个条件 token(含 not 前缀)。非法返回 null。</summary>
	private static BehaviorCondition? ParseCondition(string token)
	{
		var t = token.Trim();
		if (t.Length == 0) return null;

		bool neg = false;
		if (t.StartsWith("not ", StringComparison.OrdinalIgnoreCase)) { neg = true; t = t[4..].Trim(); }

		var m = CondRe.Match(t);
		if (!m.Success) return null;
		var name = m.Groups["name"].Value.ToLowerInvariant();
		if (!TryGetCondType(name, out var type)) return null;

		var opText = m.Groups["op"].Value.ToLowerInvariant();
		var val = m.Groups["val"].Value.Trim();
		if (val.StartsWith('=')) val = val[1..].Trim(); // 兼容旧语法 ==(值前多余的 = 去掉)
		var op = opText switch
		{
			"!=" => BehaviorOp.Ne,
			">=" => BehaviorOp.Ge,
			"<=" => BehaviorOp.Le,
			_ => BehaviorOp.Eq,
		};

		// 操作符适用性校验(以 BehaviorSyntaxDoc 单一数据源为准)
		var info = BehaviorSyntaxDoc.Conditions.FirstOrDefault(c => c.Type == type);
		if (info == null) return null;
		if (info.IsBool && opText.Length > 0) return null; // 布尔条件不允许比较符
		if (!info.IsBool && opText.Length == 0) return null; // 非布尔条件必须带比较符

		// 值非空校验
		if (!info.IsBool && val.Length == 0) return null;

		// 值预校验:数值条件必须能解析为数字;时间条件必须能解析为 HH:mm
		if (info.IsNumeric && !int.TryParse(val, out _)) return null;
		if (info.IsTime && !TimeSpan.TryParse(val, out _)) return null;

		return new BehaviorCondition { Type = type, Not = neg, Op = op, Value = val };
	}

	private static bool TryGetCondType(string name, out BehaviorCondType type)
	{
		var info = BehaviorSyntaxDoc.Conditions.FirstOrDefault(c => c.Name == name);
		if (info == null) { type = default; return false; }
		type = info.Type;
		return true;
	}

	/// <summary>构建 say 最终文本(纯逻辑,无游戏依赖;引擎调用,变量值由 getVar 提供):
	/// 变量替换 + 空变量跳过 + 悄悄话目标解析。返回 null = 应跳过(原因在 skipReason)。</summary>
	public static string? BuildSayText(string channel, string text, Func<string, string> getVar, out string? skipReason)
	{
		skipReason = null;

		// 1. 校验:内容中出现的变量必须有值,否则跳过(避免发出“欢迎 !”半截话)
		foreach (var v in BehaviorSyntaxDoc.SayVars)
		{
			if (Regex.IsMatch(text, @"\{" + v.Token + @"\}", RegexOptions.IgnoreCase)
				&& string.IsNullOrEmpty(getVar(v.Token)))
			{
				skipReason = $"变量 {{{v.Token}}} 为空(暂无对应玩家),已跳过发言";
				return null;
			}
		}

		// 2. 悄悄话 t 目标:内容开头 "名字@服务器" 发给固定的人,否则默认发给最后悄悄话你的人
		string? target = null;
		if (channel.Equals("t", StringComparison.OrdinalIgnoreCase))
		{
			// 2a. 内容开头是 "名字@服务器" → 发给固定的人(目标必须是纯名字@服务器,不含 {变量})
			var fixedM = Regex.Match(text, @"^\s*(\S+@\S+)(?:\s+(.*))?$");
			if (fixedM.Success && !fixedM.Groups[1].Value.Contains('{') && !fixedM.Groups[1].Value.Contains('}'))
			{
				target = fixedM.Groups[1].Value;
				text = fixedM.Groups[2].Value.Trim();
			}
			// 2b. 没指定固定目标 → 默认发给最后悄悄话你的人
			if (string.IsNullOrEmpty(target))
			{
				target = getVar("last_tell");
				if (string.IsNullOrEmpty(target))
				{
					skipReason = "悄悄话没有目标:内容开头可用 名字@服务器 指定(如 say t 龙尾轻轻摇@红玉海 你好),或等有人悄悄话你";
					return null;
				}
			}
		}

		// 3. 替换其余所有变量
		foreach (var v in BehaviorSyntaxDoc.SayVars)
			text = Regex.Replace(text, @"\{" + v.Token + @"\}", getVar(v.Token), RegexOptions.IgnoreCase);

		text = text.Trim();
		if (text.Length == 0)
		{
			skipReason = "发言内容为空";
			return null;
		}
		return target != null ? $"{target} {text}".Trim() : text;
	}

	/// <summary>规范顺序重排(供 AI 生成结果格式化):把一段规则解析后按规范顺序重建
	/// 「动作 → 可选 after → when 条件 → 可选 need 条件 → cooldown」,修正 LLM 输出的组件乱序
	/// (如 cooldown 写在中间、after 写在 when 后、need 写在 when 前等)。解析失败返回原文本(保存时仍会报错提示)。</summary>
	public static string NormalizeSegment(string segment)
	{
		var seg = segment.Trim();
		if (seg.Length == 0) return "";
		// 无条件修正:after 出现在 when 条件段里 → 移到动作后
		// (条件值不可能合法包含 "after N",无条件移动安全;否则会被贪婪值吞进条件值导致语义错误)
		seg = FixAfterInConds(seg);
		var errors = new List<string>();
		// itemId/comment 等仅用于日志,重排不需要;skip 开关不影响文本重建
		var rule = ParseSegment(seg, 0, "", false, false, false, 1, errors);
		if (rule != null) return RuleToText(rule); // 解析成功 → 直接规范重建

		// 解析失败:尝试 need 在 when 前的交换(仅在修正后能解析成功才采用,
		// 避免误伤 say 内容里的英文单词,如 "I need help")
		var fixedSeg = FixNeedBeforeWhen(seg);
		if (fixedSeg != seg)
		{
			fixedSeg = FixAfterInConds(fixedSeg);
			errors.Clear();
			rule = ParseSegment(fixedSeg, 0, "", false, false, false, 1, errors);
			if (rule != null) return RuleToText(rule);
		}
		return seg; // 仍失败:保留原文,由保存时校验报错
	}

	/// <summary>after 写在 when 条件段里 → 移到动作后(when 前)。条件值不会合法包含 "after N"。</summary>
	private static string FixAfterInConds(string seg)
	{
		var wm = WhenRe.Match(seg);
		if (!wm.Success) return seg;
		var action = seg[..wm.Index].Trim();
		var conds = seg[(wm.Index + wm.Length)..].Trim();
		if (AfterRe.IsMatch(action)) return seg; // after 已在动作侧
		var am = AfterRe.Match(conds);
		if (!am.Success) return seg;
		conds = AfterRe.Replace(conds, " ").Trim();
		return $"{action} {am.Value} when {conds}";
	}

	/// <summary>need 写在 when 前 → 换为 when ... need ...(仅在原段解析失败时尝试)。</summary>
	private static string FixNeedBeforeWhen(string seg)
	{
		var wm = WhenRe.Match(seg);
		if (!wm.Success) return seg;
		var nm = NeedRe.Match(seg);
		if (!(nm.Success && nm.Index < wm.Index)) return seg;
		var actionPart = seg[..nm.Index].Trim();
		var needPart = seg[(nm.Index + nm.Length)..wm.Index].Trim();
		var condPart = seg[(wm.Index + wm.Length)..].Trim();
		return $"{actionPart} when {condPart} need {needPart}";
	}

	/// <summary>规则 → 规范文本(动作 → after → when → need → cooldown)。</summary>
	private static string RuleToText(BehaviorRule rule)
	{
		var sb = new StringBuilder();
		switch (rule.ActionType)
		{
			case BehaviorActionType.Trigger:
				sb.Append("trigger ").Append(string.Join(",", rule.Macros.Select(m => (m.Shared ? "s" : "") + m.Index)));
				break;
			case BehaviorActionType.Say:
				sb.Append("say ").Append(rule.SayChannel).Append(' ').Append(rule.SayText.Trim());
				break;
			case BehaviorActionType.Approach:
				sb.Append("approach");
				if (rule.MoveName.Length > 0) sb.Append(' ').Append(rule.MoveName.Trim());
				break;
			case BehaviorActionType.Follow:
				sb.Append("follow");
				if (rule.MoveName.Length > 0) sb.Append(' ').Append(rule.MoveName.Trim());
				break;
			case BehaviorActionType.Leave:
				sb.Append("leave");
				if (rule.MoveName.Length > 0) sb.Append(' ').Append(rule.MoveName.Trim());
				break;
		case BehaviorActionType.Sit:
			sb.Append("sit");
			if (rule.SitTarget.Length > 0) sb.Append(' ').Append(rule.SitTarget.Trim());
			break;
			default: // Look
				sb.Append("look");
				if (rule.LookName.Length > 0) sb.Append(' ').Append(rule.LookName.Trim());
				break;
		}
		if (rule.AfterMin > 0 || rule.AfterMax > 0)
		{
			sb.Append(" after ").Append(rule.AfterMin.ToString("0.#", CultureInfo.InvariantCulture));
			if (rule.AfterMax > 0) sb.Append(',').Append(rule.AfterMax.ToString("0.#", CultureInfo.InvariantCulture));
		}
		sb.Append(" when ").Append(CondExprText(rule.Conditions, rule.Connectors));
		if (rule.NeedConditions.Count > 0)
			sb.Append(" need ").Append(CondExprText(rule.NeedConditions, rule.NeedConnectors));
		sb.Append(" cooldown ").Append(rule.CooldownSec);
		return sb.ToString();
	}

	/// <summary>条件列表 → 文本(保留原顺序与连接词;布尔条件不带比较符,not 前缀还原)。</summary>
	private static string CondExprText(List<BehaviorCondition> conds, List<string> conns)
	{
		var parts = new List<string>();
		foreach (var c in conds)
		{
			var info = BehaviorSyntaxDoc.Conditions.FirstOrDefault(x => x.Type == c.Type);
			var name = info?.Name ?? c.Type.ToString();
			var neg = c.Not ? "not " : "";
			var txt = (info?.IsBool ?? false)
				? neg + name
				: $"{neg}{name} {(c.Op switch { BehaviorOp.Ne => "!=", BehaviorOp.Ge => ">=", BehaviorOp.Le => "<=", _ => "=" })} {c.Value}";
			parts.Add(txt);
		}
		var sb = new StringBuilder(parts[0]);
		for (int i = 0; i < conns.Count && i + 1 < parts.Count; i++)
			sb.Append(' ').Append(conns[i]).Append(' ').Append(parts[i + 1]);
		return sb.ToString();
	}

	/// <summary>从结构化规则 JSON(LLM function calling 返回)拼装规范命令文本。
	/// 程序端保证语法顺序与关键字正确(动作 → after → when → need → cooldown),LLM 只负责填值;
	/// 返回 null 时 errors 含原因(动作/宏/频道/条件名等逐项校验)。</summary>
	public static string? AssembleRule(JObject r, List<string> errors)
	{
		// 动作(三选一)
		var action = r["action"]?.ToString().Trim().ToLowerInvariant() ?? "";
		string actionText;
		switch (action)
		{
			case "trigger":
			{
				var macros = r["macros"] as JArray;
				if (macros == null || macros.Count == 0) { errors.Add("trigger 动作缺少 macros(宏编号数组,如 [\"1\", \"s2\"])"); return null; }
				var macroParts = new List<string>();
				foreach (var m in macros)
				{
					var spec = m.ToString().Trim();
					var sm = MacroSpecRe.Match(spec);
					if (!sm.Success || !int.TryParse(sm.Groups[2].Value, out var idx) || idx < 0 || idx > 99)
					{ errors.Add($"宏编号 \"{spec}\" 非法(应为 0-99,共享宏加 s 前缀如 s2)"); return null; }
					macroParts.Add(spec);
				}
				actionText = "trigger " + string.Join(",", macroParts);
				break;
			}
			case "say":
			{
				var ch = r["channel"]?.ToString().Trim() ?? "";
				var info = BehaviorSyntaxDoc.SayChannels.FirstOrDefault(c => c.Short.Equals(ch, StringComparison.OrdinalIgnoreCase));
				if (info == null) { errors.Add($"频道 \"{ch}\" 不存在(支持: {string.Join("/", BehaviorSyntaxDoc.SayChannels.Select(c => c.Short))})"); return null; }
				var text = r["text"]?.ToString().Trim() ?? "";
				if (text.Length == 0) { errors.Add("say 内容不能为空"); return null; }
				foreach (Match m in Regex.Matches(text, @"\{([a-zA-Z_]+)\}"))
				{
					var tok = m.Groups[1].Value.ToLowerInvariant();
					if (!BehaviorSyntaxDoc.SayVars.Any(v => v.Token == tok))
					{ errors.Add($"发言内容里未知变量 {{{tok}}}(可用: {string.Join("/", BehaviorSyntaxDoc.SayVars.Select(v => "{" + v.Token + "}"))})"); return null; }
				}
				actionText = $"say {info.Short} {text}";
				break;
			}
			case "look":
			{
				var target = r["target"]?.ToString().Trim() ?? "";
				actionText = target.Length > 0 ? $"look {target}" : "look";
				break;
			}
			case "approach":
			case "follow":
			case "leave":
			{
				var target = r["target"]?.ToString().Trim() ?? "";
				actionText = target.Length > 0 ? $"{action} {target}" : action;
				break;
			}
			case "sit":
			{
				var target = r["target"]?.ToString().Trim() ?? "";
				actionText = target.Length > 0 ? $"sit {target}" : "sit";
				break;
			}
			default:
				errors.Add($"动作 \"{action}\" 非法(应为 trigger / say / look / approach / follow / leave)");
				return null;
		}

		// after / afterMax(延迟;afterMax 与 after 组成随机区间)
		double afterMin = 0, afterMax = 0;
		var afterTxt = r["after"]?.ToString().Trim();
		if (!string.IsNullOrEmpty(afterTxt) && afterTxt != "0")
		{
			if (!double.TryParse(afterTxt, NumberStyles.Float, CultureInfo.InvariantCulture, out afterMin) || afterMin < 0)
			{ errors.Add("after 应为非负数字(秒)"); return null; }
			var afterMaxTxt = r["afterMax"]?.ToString().Trim();
			if (!string.IsNullOrEmpty(afterMaxTxt) && afterMaxTxt != "0")
			{
				if (!double.TryParse(afterMaxTxt, NumberStyles.Float, CultureInfo.InvariantCulture, out afterMax) || afterMax < afterMin)
				{ errors.Add("afterMax 应为 ≥ after 的数字(秒,随机区间上限)"); return null; }
			}
		}

		// cooldown(缺省 20,0 = 无冷却)
		var cdTxt = r["cooldown"]?.ToString().Trim();
		if (!int.TryParse(cdTxt, out var cooldown) || cooldown < 0) cooldown = 20;

		// when 触发条件(必须) + need 必要条件(可选)
		var whenArr = r["when"] as JArray;
		if (whenArr == null || whenArr.Count == 0) { errors.Add("缺少 when 条件(触发条件,至少一个)"); return null; }
		if (!AssembleCondExpr(whenArr, r["connectors"] as JArray, "when", errors, out var whenText)) return null;
		var needText = "";
		var needArr = r["need"] as JArray;
		if (needArr != null && needArr.Count > 0)
		{
			if (!AssembleCondExpr(needArr, r["needConnectors"] as JArray, "need", errors, out needText)) return null;
		}

		var sb = new StringBuilder(actionText);
		if (afterMax > 0)
			sb.Append(" after ").Append(afterMin.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(afterMax.ToString("0.#", CultureInfo.InvariantCulture));
		else if (afterMin > 0)
			sb.Append(" after ").Append(afterMin.ToString("0.#", CultureInfo.InvariantCulture));
		sb.Append(" when ").Append(whenText);
		if (needText.Length > 0) sb.Append(" need ").Append(needText);
		sb.Append(" cooldown ").Append(cooldown);
		return sb.ToString();
	}

	/// <summary>条件数组 → 文本(布尔条件不带比较符;not 前缀;连接词缺省 and,多余截断)。</summary>
	private static bool AssembleCondExpr(JArray conds, JArray? conns, string label, List<string> errors, out string text)
	{
		text = "";
		var parts = new List<string>();
		foreach (var c in conds)
		{
			var name = c["cond"]?.ToString().Trim().ToLowerInvariant() ?? "";
			var info = BehaviorSyntaxDoc.Conditions.FirstOrDefault(x => x.Name == name)
				?? BehaviorSyntaxDoc.Conditions.FirstOrDefault(x => x.Name.Replace("_", "") == name.Replace("_", "")); // 模糊:去下划线匹配
			if (info == null)
			{ errors.Add($"{label} 条件 \"{name}\" 未知(支持: {string.Join("/", BehaviorSyntaxDoc.Conditions.Select(x => x.Name))})"); return false; }
			var neg = (c["not"]?.ToObject<bool>() ?? false) ? "not " : "";
			if (info.IsBool)
			{
				parts.Add(neg + info.Name);
			}
			else
			{
				var op = c["op"]?.ToString().Trim() ?? "";
				op = op switch { "!=" => "!=", ">=" => ">=", "<=" => "<=", _ => "=" };
				var val = c["value"]?.ToString().Trim() ?? "";
				if (val.Length == 0) { errors.Add($"{label} 条件 {name} 缺少 value"); return false; }
				if (info.IsNumeric && !int.TryParse(val, out _)) { errors.Add($"{label} 条件 {name} 的值 \"{val}\" 不是整数"); return false; }
				if (info.IsTime && !TimeSpan.TryParse(val, out _)) { errors.Add($"{label} 条件 {name} 的值 \"{val}\" 不是 HH:mm"); return false; }
				parts.Add($"{neg}{info.Name} {op} {val}");
			}
		}
		// 连接词:缺省全 and;过长截断
		var connectors = new List<string>();
		if (conns != null)
			foreach (var cc in conns)
			{
				var s = cc?.ToString().Trim().ToLowerInvariant() ?? "";
				connectors.Add(s is "and" or "or" ? s : "and");
			}
		while (connectors.Count < parts.Count - 1) connectors.Add("and");
		if (connectors.Count > parts.Count - 1) connectors = connectors.Take(parts.Count - 1).ToList();
		var sb = new StringBuilder(parts[0]);
		for (int i = 0; i < connectors.Count; i++) sb.Append(' ').Append(connectors[i]).Append(' ').Append(parts[i + 1]);
		text = sb.ToString();
		return true;
	}
}
