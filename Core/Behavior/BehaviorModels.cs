using Newtonsoft.Json;

namespace AuraCanAI.Dalamud.Core;

/// <summary>行为设置:一个列表条目(持久化到配置)。序号 id 在列表中唯一,自动分配(删除后复用最小空缺)。
/// 用 public 字段以便 ImGui ref 绑定;Json.NET 不默认序列化字段,故每个字段加 [JsonProperty]。</summary>
public class BehaviorItem
{
	[JsonProperty] public int id; // 序号(列表唯一,自动分配)
	[JsonProperty] public string comment = ""; // 注释(中文,单独字段)
	[JsonProperty] public bool enabled = true; // 启停开关
	[JsonProperty] public bool skipOnLeave = true; // 离开时不触发(我的在线状态为「离开」时不触发,默认勾选)
	[JsonProperty] public bool skipOnCombat = true; // 战斗中不触发(战斗状态时不触发,默认勾选)
	[JsonProperty] public bool chatNotice = false; // 聊天内提示(触发时在聊天栏 /e 提示)
	[JsonProperty] public string definition = ""; // 行为定义文本(多行;半角分号 ; 分割多段=多条规则)
}

/// <summary>条件类型(第一版条件库,数据源均来自游戏实时状态)</summary>
public enum BehaviorCondType
{
	LookingPlayer, // 看我的玩家名称(== / contains)
	AnyoneLooking, // 有没有人看我(布尔)
	LookingPlayerCount, // 看我的玩家数(数值)
	Area, // 地区名 PlaceName(== / contains)
	InHousing, // 是否在房区(布尔)
	RoomSize, // 房型 S/M/L/公寓(==)
	MyStatus, // 我的在线状态(==)
	InParty, // 是否在小队(布尔)
	MyJob, // 我的职业(==,中文名或英文缩写)
	NearbyPlayerCount, // 附近玩家数(数值)
	TargetName, // 当前目标名(== / contains)
	Time, // 现实时间 HH:mm(>= / <= / ==)
	InCombat, // 是否战斗中(布尔)
	AnyoneEnter, // 是否刚有人进入附近(布尔)
	EnterPlayer, // 进入附近的玩家名(==)
	AnyoneLeave, // 是否刚有人离开附近(布尔)
	LeavePlayer, // 离开附近的玩家名(==)
	AnyoneEmoteToMe, // 是否刚有人对我做表情(布尔;表情目标==我,仅系统内置表情,em 宏不触发)
	EmoteToMePlayer, // 刚对我做表情的玩家名(==)
	EmoteToMeName, // 刚对我做的表情名(游戏内中文名,==)
}

/// <summary>比较操作符</summary>
public enum BehaviorOp
{
	Eq, // =
	Ne, // !=
	Ge, // >=
	Le, // <=
}

/// <summary>一个原子条件</summary>
public class BehaviorCondition
{
	public BehaviorCondType Type;
	public bool Not; // not 前缀(仅作用于本条件)
	public BehaviorOp Op; // 布尔条件无操作符
	public string Value = ""; // 比较值(字符串;数值/时间也用字符串存,求值时解析)
}

/// <summary>动作类型:when 前只能有一个动作(trigger / say / look / approach / follow / leave 六选一)</summary>
public enum BehaviorActionType
{
	Trigger, // trigger N[,N...]:触发宏(0-99 / sN 共享宏);逗号分割多个宏,执行完前一个再执行下一个
	Say, // say 频道 内容:在指定频道发言(内容可用 {变量} 引用玩家名;悄悄话 t:内容开头 名字@服务器 发固定人,否则发 last_tell)
	Look, // look [名字]:选中目标(不带名字 = 最近接触你的人(悄悄话/注视/入场))
	Approach, // approach [名字]:走近目标到社交距离并面向(缺名/空变量 = 最近接触的人)
	Follow, // follow [名字]:跟随目标保持距离(缺名 = 最近接触的人)
	Leave, // leave [名字]:走开到目标数米外(缺名 = 最近接触的人)
	Sit, // sit [座位]:去坐场景设定里的座位(空=最近空座;#id 或名字均可)
}

/// <summary>一个宏引用(个人宏或共享宏 sN)</summary>
public class MacroSpec
{
	public int Index; // 宏编号 0-99(游戏有 0 号宏)
	public bool Shared; // true = 共享宏(s 前缀)
}

/// <summary>一条编译后的规则(动作 + 条件表达式 + 冷却 + 运行时状态)</summary>
public class BehaviorRule
{
	public int ItemId = 0; // 所属条目序号(日志/提示用)
	public string Comment = ""; // 条目注释快照
	public bool ChatNotice; // 条目聊天提示快照
	public bool SkipOnLeave; // 条目「离开时不触发」快照(我的在线状态为「离开」时不触发)
	public bool SkipOnCombat; // 条目「战斗中不触发」快照(战斗状态时不触发)
	public BehaviorActionType ActionType = BehaviorActionType.Trigger;
	public List<MacroSpec> Macros = new(); // Trigger:宏列表(trigger 1,2,3 逐个执行;单宏 = 原 trigger N)
	public string SayChannel = ""; // Say:频道简写(如 p/sh/y/t,见 BehaviorSyntaxDoc.SayChannels)
	public string SayText = ""; // Say:发言内容(可含 {last_tell} 等变量占位符)
	public string LookName = ""; // Look:目标名(空 = 最近接触你的人(悄悄话/注视/入场))
	public string MoveName = ""; // Approach/Follow/Leave:目标玩家名(空 = 最近接触的人;可用 {变量} 引用最近进入/悄悄话等玩家)
	public string SitTarget = ""; // Sit:座位选择器(空=最近空座;支持 名字 或 #id)
	public double AfterMin = 0; // after N:延迟 N 秒执行(不占队列,0 = 立即);after A,B → 随机区间下限
	public double AfterMax = 0; // after A,B:随机延迟区间上限(支持小数;0 = 非随机,固定 AfterMin 秒)
	public int CooldownSec = 20; // 冷却(秒,0=无冷却,默认 20)
	public List<BehaviorCondition> Conditions = new();
	public List<string> Connectors = new(); // and/or,长度 = Conditions.Count - 1
	public List<BehaviorCondition> NeedConditions = new(); // need 必要条件:不满足时直接丢弃(不触发、不设冷却、状态照常更新)
	public List<string> NeedConnectors = new(); // need 条件连接词 and/or,长度 = NeedConditions.Count - 1
	// ---- 运行时状态(边沿触发 + 冷却) ----
	public bool WasTrue;
	public DateTime CooldownUntil;
}

/// <summary>解析结果:规则列表 + 错误列表(错误含段号与原因,UI 红字显示)</summary>
public class BehaviorParseResult
{
	public List<BehaviorRule> Rules = new();
	public List<string> Errors = new();
}

/// <summary>AI 助手生成行为命令的结果(function calling 单轮 + 本地校验)</summary>
public class BehaviorGenerateResult
{
	public bool Success; // 生成成功且本地语法校验通过
	public string Comment = ""; // 建议注释(中文)
	public string Definition = ""; // 生成的命令文本(可含 ; 多段)
	public List<string> Errors = new(); // 本地语法校验错误(校验失败时给出,前端红字显示)
	public string Error = ""; // 顶层错误(未配 Key / API 异常等)
}
