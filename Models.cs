using Newtonsoft.Json;

namespace AuraCanAI.Dalamud;

/// <summary>消息设置(原 Triggernometry 持久化变量 "AuraCanAI" 的 JSON 结构)</summary>
public class MessageSettings
{
	public bool privacyMode { get; set; } // 隐私模式
	public KeywordSettings keywords { get; set; } = new(); // 关键字设置
	public KeywordSettings blockwords { get; set; } = new(); // 屏蔽字设置
	public string defaultFilePath { get; set; } = "./chatlogs"; // 默认文件记录地址
	public LogPeriod logPeriod { get; set; } = LogPeriod.Daily; // 记录周期
	public DayOfWeek weekStartDay { get; set; } = DayOfWeek.Monday;
	public List<ChannelConfig> channelConfig { get; set; } = new();
}

/// <summary>频道设置</summary>
public class ChannelConfig
{
	public string channel { get; set; } = string.Empty; // 渠道编号(如 0E)
	public string channelName { get; set; } = string.Empty; // 频道名称(如 小队)
	public string cmd { get; set; } = string.Empty; // 文本指令(如 /p)
	public bool switch1 { get; set; } = false; // 开关1(播读)
	public string switch1Name { get; set; } = "播读";
	public bool switch2 { get; set; } = false; // 开关2(仅被注视时播读)
	public string switch2Name { get; set; } = "仅被注视时播读";
	public bool logEnabled { get; set; } = false; // 是否记录进日志
	public bool llm { get; set; } = false; // LLM 采集
}

/// <summary>关键字与屏蔽字配置</summary>
public class KeywordSettings
{
	public bool enable { get; set; } = false;
	public string words { get; set; } = "";
}

/// <summary>记录周期</summary>
public enum LogPeriod
{
	Daily = 1,
	Weekly = 2,
	Monthly = 3,
	Yearly = 4
}

public class Player
{
	public string playerId { get; set; } = "";
	public string name { get; set; } = "";
	public string worldname { get; set; } = "";
}

/// <summary>LLM 设置(原 Triggernometry 持久化变量 "AuraCanAI_LLM" 的 JSON 结构)</summary>
public class LLMConfig
{
	public string deepseekKey { get; set; } = ""; // DeepSeek API 密钥
	public string currentRole { get; set; } = ""; // 当前选中的角色名称
	public List<Role> roles { get; set; } = new(); // 角色列表
}

public class Role
{
	public string name { get; set; } = string.Empty;
	public string setting { get; set; } = string.Empty;
	public double frequencyPenalty { get; set; } = 0;
	public int maxTokens { get; set; } = 4096;
	public double presencePenalty { get; set; } = 0;
	public List<string> stop { get; set; } = new();
	public double temperature { get; set; } = 1;
	public List<RoleAction> actions { get; set; } = new(); // 供 AI 主动执行的动作列表(与角色绑定;见 RoleAction)
}

/// <summary>角色自定义动作(与角色绑定,供 AI 主动执行)。执行 = 依次发送「/动作名」与「/em 文字」两条游戏命令(相当于依次执行宏)。</summary>
public class RoleAction
{
	public string name { get; set; } = ""; // 名称:给编辑者做备注用,同时是 AI 引用该动作的标识(为空时回退用「动作」)
	public string text { get; set; } = ""; // 文字:即 /em 后面的部分(不发 /em 则留空;可用游戏原生占位符如 <t>/<me>/<pos>,发送时由游戏替换)
	public string emote { get; set; } = ""; // 动作:游戏内表情名(如 抚摸;自动补 /;已带 / 则原样用;留空只发文字)
	public int cooldown { get; set; } = 0; // 冷却:单位秒(0 = 不限制)
}

// ==================== 状态机(单层:角色状态;勾选的才能互通) ====================

/// <summary>状态机(可以有多套;前端以页签切换,像场景设定的「房子」)。每套内是自己的状态列表。</summary>
public class SmSet
{
	public int id { get; set; }
	public string name { get; set; } = ""; // 显示名(如 皮下 / 白屿涟音 / 战斗)
	public List<SmState> states { get; set; } = new(); // 状态列表(单层)

	/// <summary>⚠️ 旧版两层的 moods(仅用于启动时迁移到 states);不序列化。</summary>
	[JsonProperty("moods")] public List<SmMood>? LegacyMoods { get; set; }
	public bool ShouldSerializeLegacyMoods() => false;
}

/// <summary>角色状态(单层状态机的一个节点)。
/// roleName = 该状态用的人设;nextStateIds = 允许切换到的状态(单向;空 = 不能切到别的状态);tools = 可用工具集。</summary>
public class SmState
{
	public int id { get; set; }
	public string name { get; set; } = ""; // 显示名(皮下 / 皮上 / 心情很糟糕 …)
	public string desc { get; set; } = ""; // 给 AI 的说明:什么时候该处于这个状态
	public string roleName { get; set; } = ""; // 该状态使用的人设(角色设定里的角色名;空 = 不演角色,照常聊天)
	public List<int> nextStateIds { get; set; } = new(); // 允许切换到的状态(**单向**:只列出从这里能切过去的状态)
	public List<string> tools { get; set; } = new(); // 该状态下 AI 可用的工具名(空 = 全开);见 AiToolCatalog
}

/// <summary>AI 可用工具目录(状态机里按状态勾选“这个状态下允许用哪些工具”;空 = 全开)。
/// ⚠️ `switch_identity` **不在这里**:它由“有没有可切换到的身份”自动决定(有就给、没就不给),不需要用户勾。</summary>
public static class AiToolCatalog
{
	public const string BodyAction = "rp_body_action";
	public const string FacePlayer = "face_player";
	public const string LookupPlayer = "lookup_player";
	public const string RoleEmote = "rp_emote";
	public const string SwitchIdentity = "switch_identity"; // 自动;不占工具集
	public const string LeaveParty = "leave_party";
	public const string LeaveScene = "leave_scene";
	public const string StaySilent = "stay_silent"; // 本轮不回复(保持沉默)

	public static readonly (string Name, string Label)[] All =
	{
		(BodyAction, "身体动作（走近/跟随/走开/停下/坐）"),
		(FacePlayer, "转身看向某人"),
		(LookupPlayer, "查看在场玩家（谁在场/谁在看你）"),
		(RoleEmote, "做角色自定义动作"),
		(LeaveParty, "主动退出小队（可选退队后走开）"),
		(LeaveScene, "离开场地（退到人少处）"),
		(StaySilent, "本轮不回复（保持沉默）"),
	};

	public static List<string> AllNames() => All.Select(x => x.Name).ToList();
}


// ===== 旧版两层模型(仅用于启动迁移;新代码不再使用) =====

/// <summary>[旧] 第一层:角色状态。</summary>
public class SmMood
{
	public int id { get; set; }
	public string name { get; set; } = "";
	public string desc { get; set; } = "";
	public List<SmScene> scenes { get; set; } = new();
}

/// <summary>[旧] 第二层:情景。</summary>
public class SmScene
{
	public int id { get; set; }
	public string name { get; set; } = "";
	public string desc { get; set; } = "";
	public string roleName { get; set; } = "";
	public List<int> nextSceneIds { get; set; } = new();
}

/// <summary>DeepSeek 请求体</summary>
public class RequestBody
{
	public string model { get; set; } = "deepseek-chat";
	public bool stream { get; set; } = false;
	public double temperature { get; set; } = 1;
	public double frequency_penalty { get; set; } = 0;
	public int max_tokens { get; set; } = 4096;
	public List<Message> messages { get; set; } = new();
	public double presence_penalty { get; set; } = 0;
	public List<string>? stop { get; set; }
}

public class Message
{
	private string? _content;
	public string? content
	{
		get => _content;
		set
		{
			string msg = System.Text.RegularExpressions.Regex.Replace(value ?? "", "[\u0000-\u001F]", "");
			_content = msg.Replace("\"", "");
		}
	}
	public string? role { get; set; }
}

/// <summary>WebSocket 消息对象</summary>
public class WebSocketMessage
{
	public string action { get; set; } = ""; // sendMsg/recvMsg
	public string channel { get; set; } = "";
	public string msg { get; set; } = "";
	public string sender { get; set; } = ""; // 发送者(清洗后的玩家名),回复悄悄话用
}

/// <summary>附近玩家信息(打开面板列表用)</summary>
public class NearbyPlayerInfo
{
	public string name { get; set; } = "";
	public string world { get; set; } = "";
	public ulong contentId { get; set; } // 角色稳定 ID(ContentId,绑定备注用;0=未取到)
	public string race { get; set; } = "";
	public string gender { get; set; } = "";
	public string status { get; set; } = "";
	public uint statusId { get; set; } // OnlineStatus 表 RowId(过滤用,比名字更稳)
}

/// <summary>玩家备注(绑定 ContentId 防改名;手动创建,仅在玩家面对面(在场)时新建)</summary>
public class PlayerNote
{
	public ulong ContentId { get; set; } // 角色稳定 ID(改名不变)
	public string Name { get; set; } = ""; // 名字@服务器(添加时记录;玩家改名后编辑时更新)
	public string Note { get; set; } = ""; // 备注内容(多行文本)
	public DateTime CreatedAt { get; set; }
	public DateTime UpdatedAt { get; set; }
}

/// <summary>房子分组(场景设定页签;一个房子一组可坐位置,不与人设关联)</summary>
public class House
{
	public int Id { get; set; }
	public string Name { get; set; } = "";
}

/// <summary>障碍物矩形(场景设定;网页小地图上鼠标拖拽标记,世界坐标轴对齐矩形;移动避让用)</summary>
public class ObstacleRect
{
	public int Id { get; set; } // 房子内序号
	public int HouseId { get; set; }
	public string Name { get; set; } = ""; // 可空
	public uint TerritoryId { get; set; } // 所在房间
	public float MinX { get; set; }
	public float MinZ { get; set; }
	public float MaxX { get; set; }
	public float MaxZ { get; set; }
	public DateTime CreatedAt { get; set; }

	public string Label() => string.IsNullOrEmpty(Name) ? $"#{Id}" : Name;
}

/// <summary>可坐位置(场景设定):手动标定——先站到/坐到椅子上,执行 /aca seatadd [可选名字] 记录 坐标+面向。名字可稍后在网页补或不填。</summary>
public class SeatPoint
{
	public int Id { get; set; } // 序号(房子内唯一即可,页面/命令以(房子+id)区分)
	public int HouseId { get; set; } // 归属房子(记录前在网页/命令选定当前房子)
	public string Name { get; set; } = ""; // 名字(可空;如 窗边沙发,网页可补)
	public uint TerritoryId { get; set; } // 记录时所在地区(执行"坐"时只认同地区的记录,防跨屋错位)
	public float X { get; set; }
	public float Y { get; set; }
	public float Z { get; set; }
	public float Yaw { get; set; } // 面向(弧度,0=南方/逆时针,同角色 Rotation)
	public DateTime CreatedAt { get; set; }
	// 可选的座前站定点(手动校准:/aca seatstand 记录——站在"自然准备坐的位置",自动面向椅子)。
	// 未校准时,去坐用默认推算(座位点+沿坐姿朝向 SeatApproachDistance);校准后用它,落点更准。
	public bool HasApproach { get; set; }
	public float ApproachX { get; set; }
	public float ApproachZ { get; set; }

	/// <summary>显示名(有名字用名字,否则 #id)</summary>
	public string Label() => string.IsNullOrEmpty(Name) ? $"#{Id}" : Name;
}

/// <summary>活点地图:玩家位置(世界坐标)</summary>
public class PlayerPos
{
	public string name { get; set; } = "";
	public float worldX { get; set; }
	public float worldZ { get; set; }
	public bool isSelf { get; set; }
	public uint entityId { get; set; }
}

/// <summary>聊天记录搜索结果</summary>
public class SearchResult
{
	public string FilePath { get; set; } = "";
	public int LineNumber { get; set; }
	public string LineContent { get; set; } = "";
}

/// <summary>回忆检索:解析后的聊天日志行</summary>
public class MemoryChatLine
{
	public DateTime Time; // 消息时间
	public string Channel = ""; // 频道名(喊话/说话/小队/发悄悄话/收悄悄话/原创动作/情感动作...)
	public string Speaker = ""; // 原始发言人文本(可能含服务器名/图标;无冒号行=空)
	public string CleanName = ""; // 清洗后名字(无图标/服务器名;无冒号行=空)
	public string Content = ""; // 消息内容
	public string Raw = ""; // 原始整行(展示/复制用)
	public bool IsTarget; // 是否匹配目标玩家(搜索时标记)
}

/// <summary>回忆检索:一次搜索的结果</summary>
public class MemorySearchData
{
	public string Query = "";
	public List<string> MatchedNames = new(); // 匹配到的清洗后玩家名(去重,展示用)
	public List<MemoryChatLine> PlayerLines = new(); // 目标玩家消息(时间升序,已截断限长)
	public List<MemoryChatLine> AllLines = new(); // 扫描范围内全部日志行(时间升序,含其他玩家,上下文用)
	public bool Extended = false; // 半年无结果,已扩展搜索一年
	public int FileCount = 0; // 扫描的文件数
	public string FileRange = ""; // 文件覆盖时间范围(展示用)
}

/// <summary>回忆检索:LLM 归纳出的主题(start/end 为该玩家消息列表的下标,由 LLM 输出)</summary>
public class MemoryTheme
{
	public string title { get; set; } = "";
	public string summary { get; set; } = "";
	public int start { get; set; }
	public int end { get; set; }
	[JsonIgnore] public DateTime StartTime; // PlayerLines[start].Time
	[JsonIgnore] public DateTime EndTime; // PlayerLines[end].Time
	[JsonIgnore] public int Count; // end-start+1
}
