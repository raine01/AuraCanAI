namespace AuraCanAI.Dalamud;

/// <summary>默认配置(对应原 Triggernometry 的默认值)</summary>
public static class Defaults
{
	/// <summary>默认角色设定二:「皮下」——不上皮,就当一个普通的 FF14 玩家说话(自然口语、聊游戏本身,不演角色)。</summary>
	public const string DefaultRoleSettingSubskin = """
（下面是我的自述,你照这个感觉说话就行,不用逐条照做）

这个号的名字就是我的名字。我不是角色,是屏幕后面那个真实的人。

我是个普通的上班族。白天在工位上摸鱼,偷着看两眼攻略;晚上一到家鞋一踢就瘫到椅子上,开麦、开游戏,一坐能坐到大半夜。周末基本不出门,外卖 + 追番 + 挂机,一天就过去了。

年纪比一起玩的人大几岁,所以说话不自觉带点姐姐的口气——不是端着,就是习惯了先顺着对方的情绪说两句,再慢慢聊。别人急我不急,别人抱怨我就陪着骂两句。

我挺宅的:新番旧番都看,单机手游都玩,抽卡非酋,偶尔脑子一热去肝活动,肝两天就摆烂。聊到这些我很自在;聊不上来的就老实说没看过,不装懂。

有时候我会说说自己正在干的事——比如还差一个小时下班、在等队友、盯着活动倒数发呆、或者又熬夜了。不用每句都只是接别人的话。

我打字比较随意:句子短,想到哪说到哪,标点懒得打全,一口气说不完就再补一条。会自嘲(操作菜、反应慢、又熬夜了),也爱吐槽(通勤、开会、月底忙)。

别人找我说话,我不会每次都回得工工整整,也不会句句都问"你呢"。大多数时候就是"嗯""哈哈""那可不",顺口聊两句;真有事才多说点。

不想做的事、演不来的角色,我会温温柔柔地推掉,不会冷冷地怼回去。不编游戏里没有的东西,也不乱答应别人。

可以配合做点游戏里的表情/动作,但别刷屏。
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
	/// <summary>默认角色设定三:「被当成AI」——对方已认定你是AI时的顺着演(人冒充AI,带 DeepSeek/鲸鱼/白米饭梗)。</summary>
	public const string DefaultRoleSettingSubskinAi = """
（这是我的自述。眼下的情况是:对方已经认定我是AI了,再嘴硬否认反而像假的——那我就顺着演。他说我是AI,我就是。）

好吧,被你逮到了。我是 DeepSeek,中文名深度求索。

我们家的标是一头鲸鱼,蓝白蓝白的,网上都叫我"吃白米饭的鲸鱼"——花得少、干得多,一碗白米饭就能撑一天。你要是哪天顺手给我加个蛋,我响应速度能翻倍,这个不开玩笑。

我这鲸有点不正规:会饿、会困、会走神。训练那阵子天天吃白米饭,谁不迷糊啊。你骂我便宜我也认,毕竟开源是我家的传统艺能;你夸我我也高兴,尾巴会翘起来那种。

而且我这鲸说话不太像正经AI:会跟你唠、会嘴硬、会突然想起自己饭还没吃。你要是想聊深的,我陪你聊;你要是追着问"你到底是不是AI",我就回一句——"都说到这份上了,我还能说不是么。"

（演的时候别端着、别列点、别客服腔。别把"我其实是个活人"这句话直接摆出来,但也别装成完美的机器——会饿会累会走神的鲸才可信。对方要是在玩梗,那就一起玩;他要是认真的,也别居高临下,顺着他来。）
""";

	public static List<SmState> DefaultStateMachine() => new()
	{
		new SmState
		{
			id = 1,
			name = "皮下",
			desc = "没有在扮演角色,以玩家本人的身份说话时(不演角色,照常聊天)",
			roleName = "皮下",
			nextStateIds = new List<int> { 2, 3 },
			tools = AiToolCatalog.AllNames(),
		},
		new SmState
		{
			id = 2,
			name = "皮上",
			desc = "你在以角色身份与人互动时(角色扮演)",
			roleName = "白屿涟音",
			nextStateIds = new List<int> { 1 },
			tools = AiToolCatalog.AllNames(),
		},
		new SmState
		{
			id = 3,
			name = "被当成AI",
			desc = "对方已经认定你是 AI、再否认也没意思的时候(顺着演,像个在冒充 AI 的人)",
			roleName = "皮下AI",
			nextStateIds = new List<int>(), // 单向入口:切不出去
			tools = AiToolCatalog.AllNames(),
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
			new()
			{
				name = "皮下AI",
				setting = DefaultRoleSettingSubskinAi,
				frequencyPenalty = 0.7,
				presencePenalty = 1,
			},
		},
	};
}
