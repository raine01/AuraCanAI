using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace AuraCanAI.Dalamud;

/// <summary>插件配置(持久化到卫月插件配置目录)</summary>
public class Configuration : IPluginConfiguration
{
	public int Version { get; set; } = 0;

	public int HttpPort { get; set; } = 8051; // 本地网页服务端口
	public bool TtsEnabled { get; set; } = true; // TTS 总开关
	public int TtsVolume { get; set; } = 100; // TTS 音量 0-100
	public int TtsRate { get; set; } = 0; // TTS 语速 -10 ~ 10
	public int TtsWorkers { get; set; } = 5; // TTS 并行播报线程数 1-8(多人同时说话场景,默认 5)
	public bool ShowLookingDirection { get; set; } = false; // 在默语频道显示注视者方位(/e)
	// ===== LLM 回复节奏(拟真:延迟若干秒再回复;对方连续打字时顺延、攒几条一起回) =====
	public double LlmReplyDelaySecMin { get; set; } = 2; // 对方停口后最早几秒回复(随机下限)
	public double LlmReplyDelaySecMax { get; set; } = 5; // 对方停口后最晚几秒回复(随机上限)
	public double LlmReplyMaxWaitSec { get; set; } = 12; // 对方一直说不停时的最晚插话时间(秒)

	// ===== 移动(角色自动走近/跟随/走开/到点;Phase 1,2026-09) =====
	public bool MovementEnabled { get; set; } = true; // 移动总开关(签名不可用时自动忽略)
	public bool MovementUseWalkMode { get; set; } = true; // 自动移动时走路模式(直接写 Control.Instance()->IsWalking,2026-09-05 实机前待验证)
	public string SeatSitCommand { get; set; } = "/sit"; // 坐到记录点后触发的坐法指令(实机确认:走过去后 /sit 即可;个性化可改/或用宏 SeatSitMacro)
	public int SeatSitMacro { get; set; } = -1; // 优先用游戏宏触发坐法(0-99;-1=用 SeatSitCommand 指令)
	public float SeatApproachDistance { get; set; } = 0f; // 未手动校准时的座前站距(实机结论:走到记录点正上方再 /sit 最正;特殊椅仍可用 /aca seatstand)
	public float SeatArriveTolerance { get; set; } = 0.35f; // 到达站定点的判定半径(越小落点越准)
	public float MovementStopDistance { get; set; } = 1f; // 走近/跟随停在离目标几米(社交距离,2026-09-05 由 3 改 1)
	public float MovementFollowKeepDistance { get; set; } = 3f; // 跟随保持距离(目标远离超过该值+差值才动)
	public float MovementFollowResumeExtra { get; set; } = 1.5f; // 跟随重新启动的额外余量(米)
	public float MovementLeaveDistance { get; set; } = 8f; // 走开目标到几米外
	public float MovementMaxRange { get; set; } = 60f; // 目标超过此距离(米)不自动追;0 = 不限
	public float MovementStuckTimeoutSec { get; set; } = 0.6f; // 位移停滞超过此秒数 = 卡住结束(0.5s 没动基本就是卡了,2026-09-05 由 3 收紧)
	public float MovementBlockTimeoutSec { get; set; } = 8f; // 中途被状态阻断(过场等)等待秒数,超时放弃
	public float MovementMaxDurationSec { get; set; } = 90f; // 单次移动最长持续(秒),防无限移动
	public bool MovementCancelOnUserInput { get; set; } = true; // 用户按键(移动键)即打断
	public bool MovementAllowInCombat { get; set; } = false; // 战斗中允许自动移动(默认否)

	public List<Core.BehaviorItem> Behaviors { get; set; } = new(); // 行为设置列表(宏触发自动化)
	public List<Core.Playlist> Playlists { get; set; } = new(); // 歌单列表(MIDI 播放)
	public List<PlayerNote> PlayerNotes { get; set; } = new(); // 玩家备注(绑定 ContentId,防改名)
	public List<SeatPoint> Seats { get; set; } = new(); // 可坐位置清单(场景设定;手动标定,见 /aca seatadd)
	public List<ObstacleRect> Obstacles { get; set; } = new(); // 障碍物矩形清单(网页拖拽标记,避让用)
	public List<House> Houses { get; set; } = new(); // 房子分组(场景设定页签;座位归属)
	public int CurrentHouseId { get; set; } // 当前选定的房子(记录前手动选择;0=未选)

	// ===== 状态机(两层:第一层角色状态 / 第二层情景;2026-09 新增) =====
	// 独立开关(手动开启才进入角色扮演/状态机);关闭时回退旧行为(用 LLM 配置里的「当前角色」)
	public bool StateMachineEnabled { get; set; } = false;
	public int SmCurrentMoodId { get; set; } // 当前第一层(角色状态)
	public int SmCurrentSceneId { get; set; } // 当前第二层(情景)
	public List<SmMood> SmMoods { get; set; } = new(); // [旧]单状态机的第一层列表(启动时迁移进 SmSets,新代码不再用)
	public List<SmSet> SmSets { get; set; } = new(); // 状态机列表(可多套)
	public int SmCurrentSetId { get; set; } // 当前状态机(前端页签;也是运行时用的那套)
	public bool DefaultRolesV2Added { get; set; } // 迁移标记:已把默认「皮下」人设并入现有角色列表(只做一次)

	// 以下两个字段对应原 Triggernometry 持久化变量 "AuraCanAI" / "AuraCanAI_LLM"
	[JsonProperty] public string? MessageSettingsJson { get; set; }
	[JsonProperty] public string? LlmConfigJson { get; set; }

	// DeepSeek API 密钥:独立于角色配置存储,网页「还原默认配置」/重置角色不影响 Key
	[JsonProperty] public string? DeepSeekApiKey { get; set; }

	public string? GetApiKey() => DeepSeekApiKey;
	public void SetApiKey(string? key) => DeepSeekApiKey = key;

	public MessageSettings GetMessageSettings()
	{
		try
		{
			if (!string.IsNullOrEmpty(MessageSettingsJson))
				return JsonConvert.DeserializeObject<MessageSettings>(MessageSettingsJson) ?? new MessageSettings();
		}
		catch { }
		return new MessageSettings();
	}

	public void SetMessageSettings(MessageSettings settings)
		=> MessageSettingsJson = JsonConvert.SerializeObject(settings);

	public LLMConfig GetLlmConfig()
	{
		try
		{
			if (!string.IsNullOrEmpty(LlmConfigJson))
				return JsonConvert.DeserializeObject<LLMConfig>(LlmConfigJson) ?? new LLMConfig();
		}
		catch { }
		return new LLMConfig();
	}

	public void SetLlmConfig(LLMConfig config)
		=> LlmConfigJson = JsonConvert.SerializeObject(config);

	public void Save(IDalamudPluginInterface pi)
	{
		try { pi.SavePluginConfig(this); } catch { }
	}
}
