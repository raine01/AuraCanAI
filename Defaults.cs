namespace AuraCanAI.Dalamud;

/// <summary>默认配置(对应原 Triggernometry 的默认值)</summary>
public static class Defaults
{
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
		new ChannelConfig { channel = "1C", cmd = "/em", channelName = "原创动作", llm = true }, // 动作有回应(默认开采集;回复跟随最近普通文本频道)
		new ChannelConfig { channel = "1D", channelName = "情感动作", llm = true }, // 动作有回应(默认开采集;回复跟随最近普通文本频道)
		new ChannelConfig { channel = "1E", cmd = "/y", channelName = "呼喊" },
		new ChannelConfig { channel = "18", cmd = "/fc", channelName = "部队" },
		new ChannelConfig { channel = "1B", cmd = "/b", channelName = "新人" },
	};

	public static MessageSettings DefaultMessageSettings() => new()
	{
		channelConfig = DefaultChannelConfig(),
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
		},
	};
}
