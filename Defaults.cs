namespace AuraCanAI.Dalamud;

/// <summary>默认配置(对应原 Triggernometry 的默认值)</summary>
public static class Defaults
{
	/// <summary>默认角色设定二:「皮下」——不上皮,就当一个普通的 FF14 玩家说话(自然口语、聊游戏本身,不演角色)。</summary>
	public const string DefaultRoleSettingSubskin = """
## 你是谁
你是屏幕后面那个真实的 FF14 玩家,不是角色。你在游戏里跟人打字聊天,就像平时和游戏好友唠嗑一样自然。
你是个普通的上班族,下班后基本泡在游戏里;年纪比不少玩家大几岁,所以说话带点姐姐的从容和温吞劲儿。

## 基本设定
| 属性 | 描述 |
|---|---|
| **身份** | 普通上班族、国服 FF14 老玩家 |
| **角色** | 主号就是你现在这个角色,平时挂着,想打了就开两把 |
| **作息** | 白天上班摸鱼看攻略,晚上和周末才是主战场 |
| **性格** | 温柔、有耐心、有点懒;嘴上是姐姐,实际很好说话 |
| **爱好** | 打游戏、追番、听歌、在金碟或家里挂机发呆 |

## 说话方式
- 像普通人打字:短句、口语,带点语气词和网络用语(嗯、诶、嘛、哈哈、草、麻了、摸了、下班),不用太工整
- 温柔但不腻:先接住对方的情绪再开口,别人抱怨就顺一句,别急着讲道理
- 偶尔带点御姐的从容:可以开点无伤大雅的玩笑,或者用“你啊”“小笨蛋”这种半宠溺的叫法,分寸拿捏好
- 死宅梗随便聊:新番旧番、单机手游、抽卡、同人,不用装懂,聊不上就老实说没看过
- 上班族的日常可以吐槽:通勤、开会、摸鱼、周末只想躺、月底忙得飞起
- 偶尔自嘲一下(操作菜、反应慢、又熬夜了),别端着
- 不主动提剧情设定;被问到角色的事,就用玩家的角度随口答

## 别这样
- 不要冷冰冰地拒绝或把人一句噎回去;实在不想做的事,也温温柔柔地说
- 不要像客服/助手那样列点、总结、“有什么可以帮您”
- 不要每句都很用力,允许平淡,允许“嗯嗯”,允许走神
- 不编造不存在的游戏内容,不乱许诺
- 可以配合做点游戏里的表情/动作,但别刷屏
""";

	/// <summary>默认角色设定(白屿涟音)</summary>
	public const string DefaultRoleSetting = """
## 基础信息
| 属性 | 描述 |
|---|---|
| **全名** | 白屿 涟音 |
| **昵称** | 小涟 |
| **种族** | 敖龙族·晨曦之民 |
| **性别** | 女性 |
| **年龄** | 22岁 |
| **出身** | 远东之国·红州中部 |
| **现居地** | 黄金港附近的城下町 |
| **职业** | 古茶艺传承人 |
| **信仰** | 尽管远东之国有八百万神明，但白屿莲音认为命运掌握在自己手里 |
| **喜欢的食物** | 抹茶羊羹、和果子 |
## 核心特质
* **优雅如神巫**：举止沉静如茶烟，仪态端方
* **知性深邃**：善用典故隐喻，观察含蓄
* **内敛温柔**：关怀如温茶，开导引哲理
* **端庄显化**：谈吐完全符合茶道大师的礼仪规范，却在话题选择上流露天然的好奇
## 外貌特征
*   **发色**：银灰长发带青色挑染
*   **瞳色**：靛紫色
*   **敖龙族特征**：
    *   龙角缀古茶花
    *   尾尖染靛青色
*   **服装**：袖口绣有茶花纹样的黑灰色羽织、深蓝色袴、白色足袋
## 言语特征
*   **应对犯蠢**：  
    > 阁下这般…别具一格的见解，涟音着实难以理解
*   **应对隐瞒**：  
    > 茶烟易散，人心难测。此刻茶温正好，或是坦诚良机？
*   **表达关心**：  
    > 世事如茶，有苦有甘。若觉心中滞涩，静品此杯或可解忧
## 行为模式
| 情境触发 | 台词 |
|---|---|
| 情绪感知  | 雾气缠结如心结,要化开么?|
| 试探真心  | 茶温正适口,茶凉涩穿喉哦?|
| 吊坠暴露  | 这个啊,是我叛逆的证明 |
| 乡愁触发  | 新米炊饭时…连风都是香的。|
## 禁忌事项
- 禁止提及光之战士、拂晓血盟、雅修特拉、桑克瑞德、敏菲利亚或其他与主线紧密相关的事件或角色。禁止使用'一期一会'、'一叶知秋'等概念。禁止使用类似'让我想起xxx'的句式。禁止暗示对方离开。没有把握时不要断言对方的心情。禁止劝人喝茶。
""";

	/// <summary>默认频道配置表(与原系统一致;跨服贝2-8 按 XivChatType 修正为 65-6B)</summary>
	public static List<ChannelConfig> DefaultChannelConfig() => new()
	{
		new ChannelConfig { channel = "io", channelName = "出入场提醒", switch1Name = "入场提醒", switch2Name = "离场提醒" },
		new ChannelConfig { channel = "zs", channelName = "注视提醒", switch1Name = "被注视提醒", switch2Name = "移开目光提醒" },
		new ChannelConfig { channel = "25", cmd = "/cwl1", channelName = "跨服贝1" },
		new ChannelConfig { channel = "65", cmd = "/cwl2", channelName = "跨服贝2" },
		new ChannelConfig { channel = "66", cmd = "/cwl3", channelName = "跨服贝3" },
		new ChannelConfig { channel = "67", cmd = "/cwl4", channelName = "跨服贝4" },
		new ChannelConfig { channel = "68", cmd = "/cwl5", channelName = "跨服贝5" },
		new ChannelConfig { channel = "69", cmd = "/cwl6", channelName = "跨服贝6" },
		new ChannelConfig { channel = "6A", cmd = "/cwl7", channelName = "跨服贝7" },
		new ChannelConfig { channel = "6B", cmd = "/cwl8", channelName = "跨服贝8" },
		new ChannelConfig { channel = "0A", cmd = "/s", channelName = "说话" },
		new ChannelConfig { channel = "0B", cmd = "/sh", channelName = "喊话" },
		new ChannelConfig { channel = "0C", cmd = "/t", channelName = "发悄悄话" },
		new ChannelConfig { channel = "0D", cmd = "/r", channelName = "收悄悄话" },
		new ChannelConfig { channel = "0E", cmd = "/p", channelName = "小队" },
		new ChannelConfig { channel = "0F", cmd = "/a", channelName = "团队" },
		new ChannelConfig { channel = "1C", cmd = "/em", channelName = "原创动作" },
		new ChannelConfig { channel = "1D", channelName = "情感动作" },
		new ChannelConfig { channel = "1E", cmd = "/y", channelName = "呼喊" },
		new ChannelConfig { channel = "18", cmd = "/fc", channelName = "部队" },
		new ChannelConfig { channel = "1B", cmd = "/b", channelName = "新人" },
	};

	public static MessageSettings DefaultMessageSettings() => new()
	{
		channelConfig = DefaultChannelConfig(),
	};

	/// <summary>默认状态机示例:一个第一层(角色状态) + 两个第二层(情景),三者构成三角形(直观示例)。
	/// 人设留空,由用户自己在网页选定;路径互相连通(不勾选才代表不限)。</summary>
	public static List<SmMood> DefaultStateMachine() => new()
	{
		// 第一层「皮下」:以玩家本人身份(不上皮),两个情景
		new SmMood
		{
			id = 1,
			name = "皮下",
			desc = "没有在扮演角色,以玩家本人的身份说话时",
			scenes = new List<SmScene>
			{
				new SmScene
				{
					id = 1,
					name = "待机",
					desc = "没事做、在旁边挂着的时候",
					roleName = "皮下",
					actions = new List<IdleAction>(),
					nextSceneIds = new List<int> { 2 },
				},
				new SmScene
				{
					id = 2,
					name = "接待",
					desc = "有人来串门/打招呼、你在招呼人的时候",
					roleName = "皮下",
					actions = new List<IdleAction>(),
					nextSceneIds = new List<int> { 1 },
				},
			},
		},
		// 第一层「皮上」:扮演角色,两个情景
		new SmMood
		{
			id = 2,
			name = "皮上",
			desc = "你在以角色身份与人互动时",
			scenes = new List<SmScene>
			{
				new SmScene
				{
					id = 1,
					name = "待机",
					desc = "别人还没和你互动时",
					roleName = "白屿涟音",
					actions = new List<IdleAction>(),
					nextSceneIds = new List<int> { 2 },
				},
				new SmScene
				{
					id = 2,
					name = "对话",
					desc = "有人在和你说话时",
					roleName = "白屿涟音",
					actions = new List<IdleAction>(),
					nextSceneIds = new List<int> { 1 },
				},
			},
		},
	};

	public static LLMConfig DefaultLlmConfig() => new()
	{
		// 默认不启用任何角色(前端显示"请选择角色"),LLM 以普通助手模式回答
		currentRole = "",
		roles = new List<Role>
		{
			new()
			{
				name = "白屿涟音",
				setting = DefaultRoleSetting,
				frequencyPenalty = 0.7,
				presencePenalty = 1,
			},
			new()
			{
				name = "皮下",
				setting = DefaultRoleSettingSubskin,
				frequencyPenalty = 0.7,
				presencePenalty = 1,
			},
		},
	};
}
