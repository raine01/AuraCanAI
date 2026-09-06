using System.Text;

namespace AuraCanAI.Dalamud.Core;

/// <summary>
/// 行为语法单一数据源:条件列表 + 语法说明。
/// 三处共用,新增条件/语法只改这里,自动同步:
///   1. 解析器(BehaviorParser)按 Conditions 校验,新增条件立即可解析
///   2. AI 助手生成提示(BuildGenPrompt)自动包含全部条件与语法
///   3. 帮助页(help.html 的 {{behaviorSyntax}} / {{behaviorConditions}} 占位符)自动生成说明与表格
/// </summary>
public static class BehaviorSyntaxDoc
{
	/// <summary>一个条件的元数据(解析器 / AI 提示 / 帮助页共用)</summary>
	public sealed class CondInfo
	{
		public BehaviorCondType Type; // 对应解析器枚举
		public string Name = ""; // 条件关键字(英文)
		public string Desc = ""; // 中文说明
		public string Sample = ""; // 示例(完整写法)
		public bool IsBool; // 布尔条件(不带比较符)
		public bool IsNumeric; // 数值条件(值必须为整数)
		public bool IsTime; // 时间条件(值必须为 HH:mm)
	}

	/// <summary>全部可用条件。新增条件只在这里加一行,解析器/AI/帮助页自动同步。</summary>
	public static readonly List<CondInfo> Conditions = new()
	{
		new() { Type = BehaviorCondType.LookingPlayer, Name = "looking_player", Desc = "正在看你的玩家", Sample = "looking_player = 龙尾轻轻摇" },
		new() { Type = BehaviorCondType.AnyoneLooking, Name = "anyone_looking", Desc = "有没有人看你(布尔)", Sample = "anyone_looking", IsBool = true },
		new() { Type = BehaviorCondType.LookingPlayerCount, Name = "looking_player_count", Desc = "看你的玩家人数", Sample = "looking_player_count >= 2", IsNumeric = true },
		new() { Type = BehaviorCondType.Area, Name = "area", Desc = "当前地区名(中文)", Sample = "area = 穹顶皓天私人别墅" },
		new() { Type = BehaviorCondType.InHousing, Name = "in_housing", Desc = "是否在 S/M/L 房或公寓内(布尔)", Sample = "in_housing", IsBool = true },
		new() { Type = BehaviorCondType.RoomSize, Name = "room_size", Desc = "房型 S / M / L / 公寓", Sample = "room_size = L" },
		new() { Type = BehaviorCondType.MyStatus, Name = "my_status", Desc = "你的在线状态(中文)", Sample = "my_status = 角色扮演中" },
		new() { Type = BehaviorCondType.InParty, Name = "in_party", Desc = "是否在小队(布尔)", Sample = "in_party", IsBool = true },
		new() { Type = BehaviorCondType.MyJob, Name = "my_job", Desc = "你的职业(中文名或英文缩写)", Sample = "my_job = 占星术士" },
		new() { Type = BehaviorCondType.NearbyPlayerCount, Name = "nearby_player_count", Desc = "附近玩家人数", Sample = "nearby_player_count >= 3", IsNumeric = true },
		new() { Type = BehaviorCondType.TargetName, Name = "target_name", Desc = "当前选中的目标名", Sample = "target_name = 龙尾轻轻摇" },
		new() { Type = BehaviorCondType.Time, Name = "time", Desc = "现实时间(24 小时制)", Sample = "time >= 20:00", IsTime = true },
		new() { Type = BehaviorCondType.InCombat, Name = "in_combat", Desc = "是否在战斗中(布尔)", Sample = "in_combat", IsBool = true },
		new() { Type = BehaviorCondType.AnyoneEnter, Name = "anyone_enter", Desc = "有没有人刚进入附近(布尔)", Sample = "anyone_enter", IsBool = true },
		new() { Type = BehaviorCondType.EnterPlayer, Name = "enter_player", Desc = "刚进入附近的玩家名", Sample = "enter_player = 奥·乌儿" },
		new() { Type = BehaviorCondType.AnyoneLeave, Name = "anyone_leave", Desc = "有没有人刚离开附近(布尔)", Sample = "anyone_leave", IsBool = true },
		new() { Type = BehaviorCondType.LeavePlayer, Name = "leave_player", Desc = "刚离开附近的玩家名", Sample = "leave_player = 奥·乌儿" },
		new() { Type = BehaviorCondType.AnyoneEmoteToMe, Name = "anyone_emote_to_me", Desc = "有没有人刚对你做表情(布尔;表情目标为你,仅系统内置表情,em 宏不触发)", Sample = "anyone_emote_to_me", IsBool = true },
		new() { Type = BehaviorCondType.EmoteToMePlayer, Name = "emote_to_me_player", Desc = "对你做表情的玩家(表情目标为你;配合 anyone_emote_to_me 作为事件触发,单独使用为持续状态,只会触发一次)", Sample = "anyone_emote_to_me and emote_to_me_player = 龙尾轻轻摇" },
		new() { Type = BehaviorCondType.EmoteToMeName, Name = "emote_to_me_name", Desc = "对你做的表情名(游戏内中文名如 抚摸;配合 anyone_emote_to_me 作为事件触发,单独使用为持续状态)", Sample = "anyone_emote_to_me and emote_to_me_name = 抚摸" },
	};

	/// <summary>语法规则说明(纯文本;AI 提示与帮助页共用)。新增语法规则/修饰词只改这里。</summary>
	public const string SyntaxOverview = """
- 动作(when 前只能有一个,六选一): trigger N 触发个人宏 N(0-99) / trigger sN 触发共享宏;多个宏用逗号分割(如 trigger 1,2,3)会逐个执行;或 say 频道 内容 在指定频道发言(内容可用 {变量} 引用玩家名,悄悄话 t 默认发最后悄悄话你的人,指定对象用 名字@服务器);或 look [玩家名] 选中目标(不带名字 = 最近接触你的人);或 approach [玩家名] 自动走近到社交距离并面向(移动类动作,目标须在当前场景);或 follow [玩家名] 跟随保持距离;或 leave [玩家名] 自动走开到数米外;或 sit [座位] 去坐场景设定里记录过的座位(空=离你最近的空座,支持 座位名 或 #id)。移动类动作缺名字/引用空变量 = 最近接触的人移动类动作缺名字/名字引用空变量 = 最近接触你的人;移动不可用(未登录/状态禁止/被家具挡住/距离超限)时自动结束并有日志
- 可选延迟: after N 表示 N 秒后再执行动作(不占队列);after A,B 表示 A~B 秒随机延迟(支持小数,如 after 0.5,3)
- 条件连接: when 开始触发条件;多个条件用 and / or 连接;not 放在单个条件前表示取反;省略 and 直接写 not 也可以(如 looking_player = 龙尾轻轻摇 not in_combat)
- 必要过滤: need 条件(可选,固定顺序 when ... need ...,如 when anyone_enter need not in_combat):need 后的条件不满足时,本次触发直接丢弃(不触发、不设冷却、状态照常更新)。与写进 when 的 and 区别:and 参与边沿检测(如战斗中进人没触发、出战斗会补触发);need 是触发时刻的状态过滤(进人瞬间在战斗 → 丢弃,之后出战斗不会补触发)。need 语法与 when 相同(支持 and/or/not、比较符)
- 比较: = 等于、!= 不等于、>= / <= 数值或时间
- 冷却: cooldown N 秒(默认 20;cooldown 0 = 无冷却;不要写等号,直接 cooldown 30)
- 多段: 多条独立规则用半角分号 ; 分隔成一段文本(中文全角分号 ; 不支持,会报错提示)
- 发言频道: s 说话 / sh 喊话 / p 小队 / a 团队 / y 呼喊 / b 新人 / fc 部队 / cwl1~8 跨服贝 / t 悄悄话(内容开头 名字@服务器 发给固定人,否则发给最后悄悄话你的人) / r 回复 / em 表情
- 发言变量: 内容里用 {last_tell}(最后悄悄话你的) {last_look}(最后看你的) {last_enter}(最后进入附近的) {last_contact}(最新接触你的) {emote_player}(刚对你做表情的) {emote_name}(刚对你做的表情名) 替换为玩家名;引用的变量为空时本次发言自动跳过
""";

	/// <summary>AI 助手完整生成提示(由数据源动态拼接,新增条件/语法自动包含)</summary>
	public static string BuildGenPrompt() =>
		"你是《最终幻想14》AuraCanAI 插件的「行为设置」命令生成助手。\n" +
		"用户会用中文描述想实现的自动化行为(如触发宏、选中目标、指定频道发言、附加条件与冷却),你需要把它转成一条或多条行为命令,不解释、不闲聊。\n\n" +
		"# 行为命令语法\n" + SyntaxOverview + "\n" +
		"# 可用条件(值用游戏内中文)\n" + BuildAiConditionsText() + "\n" +
		"# 输出要求\n" +
		"- 调用 generate_behavior 函数,comment 填简短中文注释,rules 填行为规则数组(每条规则一个对象,**不要拼接命令文本**,程序会自动拼装)。\n" +
		"- 规则对象字段:action = trigger(触发宏)/ say(频道发言) / look(选中目标) / approach(走近) / follow(跟随) / leave(走开) / sit(去坐场景设定的座位);\n" +
		"  - trigger:macros = 宏编号数组,如 [\"1\",\"s2\"](共享宏加 s,0-99);多个宏依次执行\n" +
		"  - say:channel = 频道简写(s/sh/p/a/y/b/fc/cwl1~8/t/r/em),text = 发言内容(悄悄话 t 默认回最后悄悄话的人,指定对象在内容开头写 名字@服务器;可用 {变量} 引用玩家名)\n" +
		"  - look:target = 目标玩家名(空字符串 = 最近接触你的人)\n" +
		"  - sit:target = 座位名或 #id(空字符串 = 离你最近的空座;须在场景设定的当前房子/房间里有记录)\n" +
		"  - approach/follow/leave:target = 目标玩家名(空字符串 = 最近接触你的人)。⚠️ 目标必须是当前在场的玩家(走近你/正在看你/跟你说话的人);这仨是角色身体移动(自动走近/跟随/走开),不是传送也不是寻路去远地,别用在地名/地点上\n" +
		"  - after = 延迟秒数(0/缺省 = 不延迟);afterMax = 随机延迟上限(与 after 组成区间,如 after=3 afterMax=6 表示 3~6 秒随机)\n" +
		"  - cooldown = 冷却秒数(0 = 无冷却,缺省 20)\n" +
		"  - when = 触发条件数组(至少一个),每个元素 {cond: 条件名, op: 比较符, value: 值, not: 是否取反};布尔条件不填 op/value\n" +
		"  - connectors = when 条件间连接词数组(长度 = 条件数-1,缺省全 and;需要\"或\"时用 or)\n" +
		"  - need = 可选必要条件数组(结构同 when,不满足时本次触发丢弃);needConnectors 同理\n" +
		"- 条件名从下方列表选,值用游戏内中文;宏编号、玩家名、地区名、时间等用户给出的值原样照抄。\n" +
		"- 用户没提到的部分用合理默认:无延迟、cooldown 20。\n" +
		"- 用户要求\"依次/先做A再做B/多个动作\"时,rules 放多条(程序自动分号连接);要求\"随机/不定时\"延迟时用 after+afterMax 区间。\n" +
		"- 涉及表情触发(emote_to_me_player / emote_to_me_name / anyone_emote_to_me)时,when 里**必须包含 anyone_emote_to_me**(最近 3 秒内有人对你做表情的事件窗口),再叠加 emote_to_me_player / emote_to_me_name 过滤谁和什么表情;否则另外两个是持续状态,只会触发一次。\n" +
		"- 示例:用户说\"有人摸我头时2秒后触发宏1,冷却10秒\" → rules=[{action:\"trigger\",macros:[\"1\"],after:2,cooldown:10,when:[{cond:\"anyone_emote_to_me\"},{cond:\"emote_to_me_name\",value:\"抚摸\"}]}]。\n";

	/// <summary>AI 提示用条件说明(每行:示例 + 说明)</summary>
	public static string BuildAiConditionsText()
		=> string.Join("\n", Conditions.Select(c => $"- {c.Sample} :{c.Desc}"));

	/// <summary>帮助页用条件表格 HTML(含标题与数量,自动同步新增条件)</summary>
	public static string BuildConditionsTableHtml()
	{
		var sb = new StringBuilder();
		sb.AppendLine($"<p><b>可用条件</b>(共 {Conditions.Count} 个):</p>");
		sb.AppendLine("<table class=\"table table-sm table-bordered\">");
		sb.AppendLine("<thead><tr><th style=\"width:190px\">条件</th><th>说明</th><th>示例</th></tr></thead>");
		sb.AppendLine("<tbody>");
		foreach (var c in Conditions)
			sb.AppendLine($"<tr><td><code>{HtmlEscape(c.Name)}</code></td><td>{HtmlEscape(c.Desc)}</td><td><code>{HtmlEscape(c.Sample)}</code></td></tr>");
		sb.AppendLine("</tbody></table>");
		return sb.ToString();
	}

	/// <summary>帮助页用语法说明 HTML(ul 列表,自动同步 SyntaxOverview)</summary>
	public static string BuildSyntaxHtml()
	{
		var items = SyntaxOverview.Trim().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
		return "<ul>" + string.Concat(items.Select(l => $"<li>{HtmlEscape(l)}</li>")) + "</ul>";
	}

	/// <summary>发言频道元数据(解析器 / AI 提示 / 帮助页共用)</summary>
	public sealed class SayChannelInfo
	{
		public string Short = ""; // 简写(如 p)
		public string Cmd = ""; // 命令前缀(如 /p)
		public string Name = ""; // 中文名(如 小队)
	}

	/// <summary>发言变量元数据(占位符 token 不含花括号)</summary>
	public sealed class SayVarInfo
	{
		public string Token = ""; // 占位符(如 last_tell,使用时 {last_tell})
		public string Desc = ""; // 中文说明
	}

	/// <summary>全部发言频道(简写 = 命令去 /;与 Defaults 频道表一致)。新增频道只在这里加一行,解析器/AI/帮助页自动同步。</summary>
	public static readonly List<SayChannelInfo> SayChannels = new()
	{
		new() { Short = "s", Cmd = "/s", Name = "说话" },
		new() { Short = "sh", Cmd = "/sh", Name = "喊话" },
		new() { Short = "p", Cmd = "/p", Name = "小队" },
		new() { Short = "a", Cmd = "/a", Name = "团队" },
		new() { Short = "y", Cmd = "/y", Name = "呼喊" },
		new() { Short = "b", Cmd = "/b", Name = "新人" },
		new() { Short = "fc", Cmd = "/fc", Name = "部队" },
		new() { Short = "cwl1", Cmd = "/cwl1", Name = "跨服贝1" },
		new() { Short = "cwl2", Cmd = "/cwl2", Name = "跨服贝2" },
		new() { Short = "cwl3", Cmd = "/cwl3", Name = "跨服贝3" },
		new() { Short = "cwl4", Cmd = "/cwl4", Name = "跨服贝4" },
		new() { Short = "cwl5", Cmd = "/cwl5", Name = "跨服贝5" },
		new() { Short = "cwl6", Cmd = "/cwl6", Name = "跨服贝6" },
		new() { Short = "cwl7", Cmd = "/cwl7", Name = "跨服贝7" },
		new() { Short = "cwl8", Cmd = "/cwl8", Name = "跨服贝8" },
		new() { Short = "t", Cmd = "/t", Name = "悄悄话(默认发最后悄悄话你的人;内容开头 名字@服务器 可发给固定人)" },
		new() { Short = "r", Cmd = "/r", Name = "回复" },
		new() { Short = "em", Cmd = "/em", Name = "表情" },
	};

	/// <summary>全部发言变量。新增变量只在这里加一行,解析器/引擎/AI/帮助页自动同步。</summary>
	public static readonly List<SayVarInfo> SayVars = new()
	{
		new() { Token = "last_tell", Desc = "最后悄悄话你的玩家" },
		new() { Token = "last_look", Desc = "最后看你的玩家" },
		new() { Token = "last_enter", Desc = "最后进入你附近的玩家" },
		new() { Token = "last_contact",Desc = "最新接触你的玩家(悄悄话/注视/入场/表情任一)" },
		new() { Token = "emote_player", Desc = "刚对你做表情的玩家" },
		new() { Token = "emote_name", Desc = "刚对你做的表情名" },
	};

	/// <summary>帮助页用发言频道表格 HTML(自动同步 SayChannels)</summary>
	public static string BuildSayChannelsHtml()
	{
		var sb = new StringBuilder();
		sb.AppendLine($"<p><b>发言频道</b>(共 {SayChannels.Count} 个,简写 = 命令去 /):</p>");
		sb.AppendLine("<table class=\"table table-sm table-bordered\">");
		sb.AppendLine("<thead><tr><th style=\"width:110px\">简写</th><th style=\"width:110px\">命令</th><th>说明</th></tr></thead>");
		sb.AppendLine("<tbody>");
		foreach (var c in SayChannels)
			sb.AppendLine($"<tr><td><code>{HtmlEscape(c.Short)}</code></td><td><code>{HtmlEscape(c.Cmd)}</code></td><td>{HtmlEscape(c.Name)}</td></tr>");
		sb.AppendLine("</tbody></table>");
		return sb.ToString();
	}

	/// <summary>帮助页用发言变量说明 HTML(自动同步 SayVars)</summary>
	public static string BuildSayVarsHtml()
	{
		var sb = new StringBuilder();
		sb.AppendLine($"<p><b>发言变量</b>(共 {SayVars.Count} 个;内容里用 {{变量}} 引用,自动替换为玩家名;引用的变量为空时本次发言自动跳过):</p>");
		sb.AppendLine("<ul>");
		foreach (var v in SayVars)
			sb.AppendLine($"<li><code>{{{HtmlEscape(v.Token)}}}</code> — {HtmlEscape(v.Desc)}</li>");
		sb.AppendLine("</ul>");
		return sb.ToString();
	}

	private static string HtmlEscape(string s)
		=> s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
