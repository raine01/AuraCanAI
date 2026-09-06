using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Config;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AuraCanAI.Dalamud.Core.Movement;

namespace AuraCanAI.Dalamud.Core;

/// <summary>AuraCanAI 核心:聊天处理、LLM、TTS、玩家监视、HTTP 方法实现</summary>
public class AuraCanAiCore : IDisposable
{
	private readonly IChatGui _chatGui;
	private readonly IClientState _clientState;
	private readonly IPlayerState _playerState;
	private readonly IObjectTable _objectTable;
	private readonly IFramework _framework;
	private readonly IPluginLog _log;
	private readonly IDalamudPluginInterface _pi;
	private readonly IDataManager _dataManager;
	private readonly ICondition _condition;
	private readonly Configuration _config;

	public TtsService Tts { get; }
	public HttpServer? Http { get; private set; }
	public BehaviorEngine Behaviors { get; private set; } = null!; // 行为设置引擎(宏触发自动化)
	public PlaylistPlayer Playlist { get; private set; } = null!; // 歌单播放引擎(MIDI 播放)
	public MovementController Movement { get; private set; } = null!; // 移动控制器(自动走近/跟随/走开/到点,Phase 1)
	private readonly MovementOverride _movementOverride; // 移动输入 hook(底层;与 Movement 同生命周期)

	// 状态
	private MessageSettings _msgSetting;
	private LLMConfig _llmSetting;
	private readonly HashSet<Player> _targetMePlayers = new();
	private string _lastTellUser = ""; // 最近悄悄话用户(清洗名,0C发/0D收)
	private string _lastLookUser = ""; // 最近注视用户(清洗名,新增注视者)
	private string _lastEnterUser = ""; // 最近入场用户(清洗名,新玩家进入附近列表)
	private DateTime _lastEnterTime = DateTime.MinValue; // 最近入场事件时间(行为条件 anyone_enter/enter_player 用)
	private string _lastLeaveUser = ""; // 最近离场用户(清洗名,玩家离开附近列表)
	private DateTime _lastLeaveTime = DateTime.MinValue; // 最近离场事件时间(行为条件 anyone_leave/leave_player 用)
	private string _lastEmoteUser = ""; // 最近对我做表情的用户(清洗名;表情目标 == 我的玩家)
	private string _lastEmoteName = ""; // 最近对我做的表情名(Emote sheet 中文名)
	private DateTime _lastEmoteTime = DateTime.MinValue; // 最近对我做表情事件时间(行为条件 anyone_emote_to_me 用)
	private Dictionary<uint, ushort> _lastEmoteId = new(); // EntityId -> 上次扫描到的 EmoteId(边沿检测:从无到有新表情开始记一次)
	private string _lastContactUser = ""; // 最近接触用户(悄悄话/注视/入场/表情任一最新,look 动作不带名字时用)
	private readonly List<Message> _chatHistory = new();
	private readonly object _historyLock = new();
	private readonly HttpClient _client = new();
	private readonly ConcurrentDictionary<uint, string> _knownPlayers = new(); // EntityId -> 玩家名
	private readonly ConcurrentQueue<string> _newPlayers = new();
	private Timer? _timer500;
	private Timer? _timer2000;
	private volatile bool _disposed;
	private bool _webStarted;
	private string _playerId = "";
	private string _playerName = "";
	private MoveResult? _lastMoveResult; // 最近一次移动结果(LLM 场景注入用)
	private DateTime _lastMoveResultAt = DateTime.MinValue;
	// 回复调度(拟真延迟):收到触发消息 → pending;对方连续说话顺延;静默 2~5s(随机)后统一回复一次(可攒多条)
	private bool _replyPending;
	private int _replyPendingCount;
	private DateTime _replyFirstMsgAt = DateTime.MinValue; // 本次待回首条消息时间(最长等待基准)
	private DateTime _replyLastMsgAt = DateTime.MinValue; // 最近一条触发消息时间(静默判定基准)
	private double _replySilenceSec = 3; // 本次待回选定的静默秒数(随机区间取一次)
	private bool _replyBusy; // 是否有回复请求正在生成中(单飞行)
	private string _lastTriggerChannel = ""; // 最近触发消息的频道(回复跟随它)
	private string _lastTriggerAddr = ""; // 最近触发消息的回复地址(悄悄话 /t 用)
	private string _lastSpeakChannel = ""; // 最近一次普通文本频道(说话/小队/等,非动作类;1C/1D 触发回复时跟随它)
	private string _lastSpeakAddr = ""; // 最近普通文本频道对应的回复地址
	private string _lastLlmEchoContent = ""; // 最近一次实际发出的 LLM 台词(自身回显去重)
	private DateTime _lastLlmEchoAt = DateTime.MinValue;

	/// <summary>不允许 LLM 采集的频道(前端已禁勾,后端兜底强制忽略):跨服贝(25/65-6B)、部队(18)、新人(1B)。</summary>
	private static readonly HashSet<string> NoLlmChannels = new()
	{ "18", "1B", "25", "65", "66", "67", "68", "69", "6A", "6B" };
	private static bool _noPersonaWarned; // 「未选人设不触发」日志仅提示一次

	public AuraCanAiCore(IDalamudPluginInterface pi, Configuration config, IChatGui chatGui, IClientState clientState,
		IPlayerState playerState, IObjectTable objectTable, IFramework framework, IPluginLog log, IDataManager dataManager,
		ICondition condition, ISigScanner sigScanner, IGameInteropProvider hookProvider, IGameConfig gameConfig)
	{
		_pi = pi;
		_config = config;
		_chatGui = chatGui;
		_clientState = clientState;
		_playerState = playerState;
		_objectTable = objectTable;
		_framework = framework;
		_log = log;
		_condition = condition;
		_dataManager = dataManager;

		_msgSetting = config.GetMessageSettings();
		if (_msgSetting.channelConfig.Count == 0) _msgSetting = Defaults.DefaultMessageSettings();
		_llmSetting = config.GetLlmConfig();
		if (_llmSetting.roles.Count == 0) _llmSetting = Defaults.DefaultLlmConfig();
		// DeepSeek Key 独立化:旧版本 Key 存在 LLM json 里,首次迁移到独立字段(仅一次)
		if (string.IsNullOrEmpty(config.DeepSeekApiKey) && !string.IsNullOrEmpty(_llmSetting.deepseekKey))
		{
			config.SetApiKey(_llmSetting.deepseekKey);
			config.Save(pi);
		}
		SyncApiKeyFromConfig();
		EnsureValidCurrentRole();

		Tts = new TtsService(config.TtsWorkers) { Enabled = config.TtsEnabled, Volume = config.TtsVolume, Rate = config.TtsRate };

		_playerName = _playerState.CharacterName;
		_playerId = _playerState.EntityId.ToString("X");

		// 订阅事件
		_chatGui.ChatMessage += OnChatMessage;
		_clientState.TerritoryChanged += OnTerritoryChanged;
		_clientState.Login += OnLogin;

		// 定时器:500ms 注视检测 + 玩家进出 + 行为求值,2000ms 入场播报(回调切回游戏主线程访问 ObjectTable)
		_timer500 = new Timer(_ => _framework.RunOnFrameworkThread(() => SafeTick(() => { CheckLookingAndPlayers(); Behaviors?.Tick(); ReplyTick(); })), null, 0, 500);
		_timer2000 = new Timer(_ => _framework.RunOnFrameworkThread(() => SafeTick(CheckNewPlayers)), null, 2000, 2000);

		// 行为设置引擎:从配置编译规则(UI 增删改后重新 Reload)
		Behaviors = new BehaviorEngine(this);
		Behaviors.Reload();
		Playlist = new PlaylistPlayer(this);

		// 移动(Phase 1):输入 hook + 控制器。签名缺失时自动降级为不可用(不崩插件),movediag 可查。
		_movementOverride = new MovementOverride(sigScanner, hookProvider, gameConfig, objectTable, log);
		Movement = new MovementController(this, config, _movementOverride, condition, clientState, objectTable, framework, log);
		Movement.MovementFinished += OnMovementFinished; // 记录移动结果供 LLM 场景注入/日志

		ResetChatHistory();

		// 网页服务常驻(端口可在面板调整,失败可手动重启)
		StartWeb();
	}

	// ==================== 网页服务 ====================

	public bool StartWeb()
	{
		if (_webStarted) return true;
		var webDir = Path.Combine(_pi.AssemblyLocation.Directory?.FullName ?? ".", "Web");
		Http = new HttpServer(_config.HttpPort, webDir, new Dictionary<string, Func<string, object>>(), this);
		_webStarted = Http.Start();
		return _webStarted;
	}

	public void StopWeb()
	{
		try { Http?.Stop(); } catch { }
		Http = null;
		_webStarted = false;
	}

	// ==================== 网页服务信息(面板/帮助页用) ====================

	public int HttpPort => _config.HttpPort;
	public string WebUrl => $"http://localhost:{_config.HttpPort}/";
	public string ChatLogPath => ResolvePath(_msgSetting.defaultFilePath);

	/// <summary>检测网页服务是否可访问(实际请求一次首页)</summary>
	public bool CheckWeb()
	{
		try
		{
			using var client = new HttpClient();
			var task = client.GetStringAsync(WebUrl);
			return task.Wait(2000) && !string.IsNullOrEmpty(task.Result);
		}
		catch { return false; }
	}

	// ==================== 附近玩家列表(打开面板用) ====================

	/// <summary>取玩家角色稳定 ID(ContentId):通过 FFXIVClientStructs BattleChara 字段读取(接口未暴露),失败返回 0。</summary>
	private static ulong GetPlayerContentId(IPlayerCharacter pc)
	{
		try
		{
			if (pc.Address != IntPtr.Zero)
			{
				unsafe
				{
					var battle = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)pc.Address;
					return battle->ContentId;
				}
			}
		}
		catch { /* ContentId 读取失败返回 0 */ }
		return 0;
	}

	/// <summary>收集当前场景附近玩家:名字@服务器、种族/性别、在线状态</summary>
	public List<NearbyPlayerInfo> GetNearbyPlayers()
	{
		var list = new List<NearbyPlayerInfo>();
		var local = _objectTable.LocalPlayer;
		if (local == null) return list;
		var localName = GetCleanName(local.Name.TextValue); // 与进出检测一致:排除自己的镜像对象
		foreach (var obj in _objectTable)
		{
			if (obj.ObjectKind != ObjectKind.Pc) continue;
			if (obj.GameObjectId == local.GameObjectId) continue; // 排除自己
			var rawName = obj.Name.TextValue;
			if (string.IsNullOrWhiteSpace(rawName)) continue; // 镜像/未初始化对象名字为空,跳过
			if (GetCleanName(rawName) == localName) continue; // 排除自己镜像(角色信息窗口等)
			if (obj is not IPlayerCharacter pc) continue;
			list.Add(new NearbyPlayerInfo
			{
				name = GetCleanName(pc.Name.TextValue),
				world = GetWorldName(pc.HomeWorld.RowId),
				contentId = GetPlayerContentId(pc),
				race = GetRaceName(pc.Customize.Length > 0 ? pc.Customize[0] : (byte)0),
				gender = pc.Customize.Length > 1 && pc.Customize[1] == 1 ? "女" : "男",
				status = GetOnlineStatusName(pc.OnlineStatus.RowId),
				statusId = pc.OnlineStatus.RowId,
			});
		}
		return list;
	}

	private static string GetRaceName(byte raceId) => raceId switch
	{
		1 => "人族",
		2 => "精灵族",
		3 => "拉拉菲尔族",
		4 => "猫魅族",
		5 => "鲁加族",
		6 => "敖龙族",
		7 => "硌狮族",
		8 => "维埃拉族",
		_ => "未知",
	};

	private string GetWorldName(uint worldId)
	{
		if (worldId == 0) return "";
		try { return _dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRow(worldId).Name.ExtractText() ?? ""; }
		catch { return ""; }
	}

	private readonly Dictionary<string, uint> _statusIdCache = new(); // 在线状态中文名 -> RowId(按名字反查一次后缓存)

	/// <summary>按中文名反查 OnlineStatus 的 RowId(缓存;查不到返回 0)</summary>
	public uint GetOnlineStatusId(string name)
	{
		if (_statusIdCache.TryGetValue(name, out var cached)) return cached;
		uint found = 0;
		try
		{
			var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.OnlineStatus>();
			if (sheet != null)
			{
				foreach (var row in sheet)
				{
					if (row.Name.ExtractText() == name) { found = row.RowId; break; }
				}
			}
		}
		catch { }
		_statusIdCache[name] = found;
		return found;
	}

	private string GetOnlineStatusName(uint statusId)
	{
		if (statusId == 0) return "";
		try { return _dataManager.GetExcelSheet<Lumina.Excel.Sheets.OnlineStatus>()?.GetRow(statusId).Name.ExtractText() ?? ""; }
		catch { return ""; }
	}

	// ==================== 事件 ====================

	private void OnLogin()
	{
		_playerName = _playerState.CharacterName;
		_playerId = _playerState.EntityId.ToString("X");
		Log($"已登录: {_playerName}");
	}

	private void OnTerritoryChanged(uint territoryId)
	{
		_knownPlayers.Clear();
		_targetMePlayers.Clear();
		while (_newPlayers.TryDequeue(out _)) { }
		// 切换区域 = 所有人离场,清空最近接触变量
		_lastTellUser = "";
		_lastLookUser = "";
		_lastEnterUser = "";
		_lastEmoteUser = "";
		_lastEmoteId = new(); // 重置表情边沿状态,避免切区域后误触发
		_lastContactUser = "";
		Movement?.NotifyZoneChanged(); // 切换区域中止移动
	}

	private void OnChatMessage(IHandleableChatMessage message)
	{
		try
		{
			var type = message.LogKind;
			// 只处理聊天类频道(00xx 范围)
			if (type == XivChatType.Echo) return; // 0038 默语
			if (!IsChatType(type)) return;

			var channelNo = ((ushort)type).ToString("X2");
			var cleanName = GetCleanName(message.Sender.TextValue);
			// 回复地址:优先从 SeString payload 取玩家名+世界ID(游戏 /t 强制要求 名字@服务器 格式)
			var replyAddress = GetReplyAddressFromSender(message.Sender);
			var text = message.Message.TextValue;
			// 自己发言判断:优先游戏内部关系标记,对象表实时名字/状态名兜底;0C 发悄悄话的 Sender 是收件人
			var localName = _objectTable.LocalPlayer?.Name.TextValue;
			var isOwn = message.SourceKind == XivChatRelationKind.LocalPlayer
						|| (!string.IsNullOrEmpty(cleanName) && (cleanName == localName || cleanName == _playerState.CharacterName))
						|| channelNo == "0C";

			ChatVoiceHandler(channelNo, cleanName, text, isOwn);
			ChatLLMHandler(channelNo, cleanName, text, isOwn, replyAddress);
			ChatWsHandler(channelNo, cleanName, text, isOwn, replyAddress);
			// 最近接触用户:悄悄话(0C 我方发出 / 0D 收到,Sender 即对方)
			if (channelNo is "0C" or "0D" && !string.IsNullOrEmpty(cleanName))
			{
				_lastTellUser = cleanName;
				_lastContactUser = cleanName;
			}
			if (_msgSetting.privacyMode) return;
			ChatLogHandler(channelNo, cleanName, text);
		}
		catch (Exception e)
		{
			LogErr($"聊天处理异常: {e}");
		}
	}

	private static bool IsChatType(XivChatType type) => (ushort)type switch
	{
		0x0A or 0x0B or 0x0C or 0x0D or 0x0E or 0x0F or 0x18 or 0x1B or 0x1C or 0x1D or 0x1E or 0x19 or 0x25
			or >= 0x65 and <= 0x6B => true,
		_ => false,
	};

	// ==================== 聊天处理 ====================

	/// <summary>语音提示</summary>
	private void ChatVoiceHandler(string channelNo, string cleanName, string text, bool isOwn)
	{
		// 不处理自己的发言;关键字 > 屏蔽字 > 播读 > 仅被注视播读
		if (isOwn) return;
		else if (_msgSetting.keywords.enable && !string.IsNullOrEmpty(_msgSetting.keywords.words) && Regex.IsMatch(text, _msgSetting.keywords.words)) { }
		else if (_msgSetting.blockwords.enable && !string.IsNullOrEmpty(_msgSetting.blockwords.words) && Regex.IsMatch(text, _msgSetting.blockwords.words)) return;
		else if (GetChannelSwitchs(channelNo).switch1) { }
		else if (GetChannelSwitchs(channelNo).switch2 && IsLooking(cleanName)) { }
		else return;

		// 原创动作 1C 需要拼接发言人;情感动作 1D 去除分组及序号
		if (channelNo == "1C") text = cleanName + text;
		if (channelNo == "1D" && text.Length > 2) text = GetCleanName(text[..2]) + text[2..];
		Tts.Speak(text);
	}

	/// <summary>LLM 采集 + 回复。文本类频道(说话/悄悄话/小队等):进历史并触发回复(回复跟随来源频道);
	/// 情感动作/原创动作(1C/1D):进历史,若有最近普通文本频道可跟随则触发一次回复(回应动作),否则仅作上下文。
	/// 限制:跨服贝/部队/新人(NoLlmChannels)不采集;小队频道只采本队成员。
	/// replyAddress = 对方回复地址(名字@服务器),悄悄话 0D 回复时用。</summary>
	private void ChatLLMHandler(string channelNo, string cleanName, string text, bool isOwn, string replyAddress)
	{
		// 跨服贝/部队/新人:禁止 LLM 采集(前端已禁勾,后端兜底)
		if (NoLlmChannels.Contains(channelNo)) return;
		if (!GetChannelSwitchs(channelNo).llm) return;
		// 小队频道:忽略不在本队的人(他人;系统/自己不过滤)
		if (channelNo == "0E" && !isOwn && !IsPartySelf(cleanName))
		{
			Log($"LLM 采集忽略: 小队频道的 {cleanName} 不在本队");
			return;
		}
		if (isOwn) // 自己发言
		{
			// 去重:刚由 LLM 发出的原创/情感动作会由聊天事件捕获回来(own 1C/1D),跳过避免历史重复
			if ((DateTime.Now - _lastLlmEchoAt).TotalSeconds <= 25 && text == _lastLlmEchoContent)
			{
				Log($"LLM 动作自身回显已跳过(历史去重): {text}");
				return;
			}
			if (channelNo == "1C") SendMsg($"({text})", "assistant");
			else if (channelNo == "1D") SendMsg($"({text.Replace(cleanName, "")})", "assistant");
			else SendMsg(text, "assistant");
		}
		else // 他人发言
		{
			// 记录最近普通文本频道(说话/悄悄话/小队等,能回话的),供动作(1C/1D)触发回复时跟随
			// —— 用户口径:动作/表情也该有回应,台词发到最近一次普通聊天频道(说话范围只限已勾采集的频道)
			string hist;
			bool isAction = channelNo == "1C" || channelNo == "1D";
			if (channelNo == "1C") hist = $"[{DateTime.Now:yyyy-MM-dd HH:mm}]({cleanName}{text})"; // 原创动作文本无主语,补发言人
			else if (channelNo == "1D")
			{
				// 情感动作文本通常已含“谁对谁做了什么”;保险起见不含名字时补发言人
				hist = text.Contains(cleanName)
					? $"[{DateTime.Now:yyyy-MM-dd HH:mm}]({text})"
					: $"[{DateTime.Now:yyyy-MM-dd HH:mm}]({cleanName}:{text})";
			}
			else { hist = $"[{DateTime.Now:yyyy-MM-dd HH:mm}]{cleanName}:{text}"; }

			if (isAction)
			{
				// 动作类:有可跟随的普通频道 → 进历史并触发回复(回应动作);没有 → 仅进历史作上下文
				if (_lastSpeakChannel.Length > 0)
					SendMsg(hist, "user", _lastSpeakChannel, _lastSpeakAddr, true);
				else
					SendMsg(hist, "user", "", "", false); // 尚无普通文本可回(用户还没说过话/没开采集)→ 只作上下文
			}
			else
			{
				_lastSpeakChannel = channelNo; // 更新最近普通文本频道(只有 llm 采集开启的频道会走到这)
				_lastSpeakAddr = replyAddress;
				SendMsg(hist, "user", channelNo, replyAddress, true);
			}
		}
	}

	/// <summary>聊天消息记录到文件</summary>
	private void ChatLogHandler(string channelNo, string cleanName, string text)
	{
		var config = GetChannelSwitchs(channelNo);
		if (!config.logEnabled) return;
		var timeString = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
		string newLine;
		if (channelNo == "1C") newLine = $"[{timeString}][{config.channelName}]{cleanName}{text}";
		else if (channelNo == "1D") newLine = $"[{timeString}][{config.channelName}]{text}";
		else newLine = $"[{timeString}][{config.channelName}]{cleanName}:{text}";

		var now = DateTime.Now;
		string fileName = $"{now:yyyy-MM-dd}.txt";
		if (_msgSetting.logPeriod == LogPeriod.Monthly) fileName = $"{now:yyyy-MM}.txt";
		else if (_msgSetting.logPeriod == LogPeriod.Yearly) fileName = $"{now:yyyy}.txt";
		else if (_msgSetting.logPeriod == LogPeriod.Weekly)
		{
			int week = System.Globalization.CultureInfo.CurrentCulture.Calendar.GetWeekOfYear(now,
				System.Globalization.CalendarWeekRule.FirstDay, _msgSetting.weekStartDay);
			fileName = $"{now:yyyy}-第{week:D2}周.txt";
		}
		try
		{
			var dir = ResolvePath(_msgSetting.defaultFilePath);
			var wholeFileName = Path.Combine(dir, fileName);
			Directory.CreateDirectory(Path.GetDirectoryName(wholeFileName)!);
			File.AppendAllText(wholeFileName, newLine + "\n", Encoding.UTF8);
		}
		catch (Exception e) { LogErr($"聊天记录写入失败: {e.Message}"); }
	}

	/// <summary>聊天消息推送 WebSocket(网页实时聊天)</summary>
	private void ChatWsHandler(string channelNo, string cleanName, string text, bool isOwn, string replyAddress)
	{
		var config = GetChannelSwitchs(channelNo);
		if (config.channel == string.Empty) return;
		var timeString = DateTime.Now.ToString("HH:mm:ss");
		string newLine;
		if (channelNo == "1C") newLine = $"[{timeString}][{config.channelName}]{cleanName}{text}";
		else if (channelNo == "1D") newLine = $"[{timeString}][{config.channelName}]{text}";
		else newLine = $"[{timeString}][{config.channelName}]{cleanName}:{text}";
		var msgType = isOwn || channelNo == "0C" ? "sendMsg" : "recvMsg";
		Http?.PushChat(new WebSocketMessage { action = msgType, channel = channelNo, msg = newLine, sender = isOwn ? "" : replyAddress });
	}

	// ==================== 玩家监视 ====================

	private void CheckLookingAndPlayers()
	{
		var local = _objectTable.LocalPlayer;
		if (local == null) return;
		var localName = GetCleanName(local.Name.TextValue); // 角色信息/装备预览等窗口会生成自己的镜像对象(不同 GameObjectId),需按名字排除

		var current = new Dictionary<uint, string>();
		var positions = new Dictionary<uint, System.Numerics.Vector3>();
		var looking = new HashSet<uint>();
		foreach (var obj in _objectTable)
		{
			if (obj.ObjectKind != ObjectKind.Pc) continue;
			if (obj.GameObjectId == local.GameObjectId) continue; // 排除自己
			var rawName = obj.Name.TextValue;
			if (string.IsNullOrWhiteSpace(rawName)) continue; // 镜像/未初始化对象名字为空,跳过(角色信息窗口等 UI 产生的)
			if (GetCleanName(rawName) == localName) continue; // 排除自己镜像(角色信息窗口等)
			current[obj.EntityId] = rawName;
			positions[obj.EntityId] = obj.Position;
			if (obj.TargetObjectId == local.GameObjectId)
				looking.Add(obj.EntityId);
		}

		// 目光移开
		foreach (var p in _targetMePlayers.ToList())
		{
			if (uint.TryParse(p.playerId, out var pid) && looking.Contains(pid)) continue;
			_targetMePlayers.Remove(p);
			Log($"{p.name}移开了目光");
			if (GetChannelSwitchs("zs").switch2) Tts.Speak($"{p.name}移开了目光");
		}
		// 新增注视者
		foreach (var id in looking)
		{
			if (_targetMePlayers.Any(x => x.playerId == id.ToString())) continue;
			var name = current.TryGetValue(id, out var n) ? n : id.ToString("X");
			_targetMePlayers.Add(new Player { playerId = id.ToString(), name = name });
			var cleanName2 = GetCleanName(name); // 最近接触用户:注视
			_lastLookUser = cleanName2;
			_lastContactUser = cleanName2;
			Log($"{name}在看你");
			if (GetChannelSwitchs("zs").switch1) Tts.Speak($"{name}在看你");
			// 面板勾选后在默语频道显示注视者方位(仅新增注视者时发一条,避免刷屏)
			if (_config.ShowLookingDirection && positions.TryGetValue(id, out var pos))
				Echo($"{GetCleanName(name)}在看你({GetLookingDirection(pos, local.Position, local.Rotation)})");
		}

		// 玩家进出
		foreach (var kv in current)
		{
			if (_knownPlayers.TryAdd(kv.Key, kv.Value))
			{
				_newPlayers.Enqueue(kv.Value);
				var cleanName2 = GetCleanName(kv.Value); // 最近接触用户:入场(进入附近玩家列表)
				_lastEnterUser = cleanName2;
				_lastEnterTime = DateTime.Now;
				_lastContactUser = cleanName2;
			}
		}
		foreach (var id in _knownPlayers.Keys.ToList())
		{
			if (!current.ContainsKey(id) && _knownPlayers.TryRemove(id, out var gone))
			{
				_lastLeaveUser = GetCleanName(gone);
				_lastLeaveTime = DateTime.Now;
				Log($"{gone}离开了");
				if (GetChannelSwitchs("io").switch2) Tts.Speak($"{gone}离开了");
				ClearContactIfMatch(GetCleanName(gone)); // 变量中的人离场时清除对应变量
			}
		}

		ScanEmotes(); // 表情检测:附近玩家对我做系统内置表情(表情目标==我)时记录事件
	}

	/// <summary>表情检测:扫描附近玩家 EmoteController,记录「对我做表情」事件(严格:表情目标 == 我的玩家)。
	/// 仅系统内置表情会播放动画(/em 自定义文本宏不播放动画,天然不触发);
	/// 边沿检测:EmoteId 从 0/其他值变为新值 = 新表情开始,记一次(循环表情只记开始);
	/// 首次见到的实体只记录状态不触发(避免把已在进行中的表情误判为新事件)。500ms 轮询,框架线程。</summary>
	private unsafe void ScanEmotes()
	{
		var local = _objectTable.LocalPlayer;
		if (local == null) return;
		var localName = GetCleanName(local.Name.TextValue);
		var next = new Dictionary<uint, ushort>();
		try
		{
			foreach (var obj in _objectTable)
			{
				if (obj.ObjectKind != ObjectKind.Pc) continue;
				if (obj.GameObjectId == local.GameObjectId) continue; // 排除自己
				var rawName = obj.Name.TextValue;
				if (string.IsNullOrWhiteSpace(rawName)) continue; // 镜像/未初始化对象名字为空,跳过
				if (GetCleanName(rawName) == localName) continue; // 排除自己镜像(角色信息窗口等)

				var battle = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)obj.Address;
				var emoteId = battle->EmoteController.EmoteId;
				next[obj.EntityId] = emoteId;
				if (emoteId == 0) continue; // 没在做表情
				if (battle->EmoteController.Target.ObjectId != local.GameObjectId) continue; // 表情不是对我做的
				// 边沿检测:上一轮该实体没有在做这个表情才记事件;首次见到只记录不触发
				if (!_lastEmoteId.TryGetValue(obj.EntityId, out var prev) || prev == emoteId) continue;

				var cleanName2 = GetCleanName(rawName);
				var emoteName = GetEmoteName(emoteId);
				_lastEmoteUser = cleanName2;
				_lastEmoteName = emoteName;
				_lastEmoteTime = DateTime.Now;
				_lastContactUser = cleanName2; // 表情计入最近接触(look 动作不带名字时指向刚做表情的人)
				Log($"{cleanName2}对你做了表情:{emoteName}(#{emoteId})");
			}
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"表情检测异常: {e.Message}");
		}
		_lastEmoteId = next;
	}

	/// <summary>EmoteId → 表情中文名(Lumina Emote sheet;EmoteController.EmoteId 即 Emote 表 RowId)。
	/// 读取失败返回占位 "#编号" 以便排查。</summary>
	private string GetEmoteName(uint emoteId)
	{
		try
		{
			var n = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?.GetRow(emoteId).Name.ExtractText() ?? "";
			if (n.Length > 0) return n;
		}
		catch (Exception e)
		{
			Plugin.Log?.Warning($"读取表情名失败(#{emoteId}): {e.Message}");
		}
		return $"#{emoteId}";
	}

	private void CheckNewPlayers()
	{
		if (_newPlayers.TryDequeue(out var name) && !string.IsNullOrWhiteSpace(name))
		{
			Log($"{name}来了");
			if (GetChannelSwitchs("io").switch1) Tts.Speak($"{name}来了");
		}
	}

	private void SafeTick(Action action)
	{
		try { action(); }
		catch (Exception ex) { LogErr($"定时器报错了: {ex.Message}"); }
	}

	// ==================== 活点地图 ====================

	// 住宅区 TerritoryType.RowId(_clientState.TerritoryType 实测返回此体系;楼层切换不换 territory)
	// 海雾村:S=282 M=283 L=284 公寓=384
	private uint _lastLoggedTerritory;

	/// <summary>是否位于 S/M/L 房或公寓内(房屋内部,含所有内饰版本;房区外部街道不算)。
	/// 判断依据 TerritoryType.Bg 路径:含 /ind/ = 房屋内部;含 /hou/ = 房区外部街道(不算)</summary>
	public bool IsInHousing()
	{
		var tid = _clientState.TerritoryType;
		if (_lastLoggedTerritory != tid)
		{
			_lastLoggedTerritory = tid;
			var code = GetHousingCode(tid);
			Log($"当前区域 territoryId={tid}, 住宅代码={code ?? "(非住宅)"}");
		}
		return GetHousingCode(tid) != null;
	}

	/// <summary>取 TerritoryType.Bg 路径(形如 .../twn/twn_hou01/... 房区外部、.../ind/ind_s_01/... 房屋内部);取不到返回 null</summary>
	private string? GetTerritoryBg(uint territoryId)
	{
		try
		{
			var terr = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRow(territoryId);
			return terr.HasValue ? terr.Value.Bg.ExtractText() : null;
		}
		catch { return null; }
	}

	/// <summary>当前住宅房间尺寸(S=40 / M=50 / L=60 / 公寓=40,游戏单位),依据住宅代码后缀 i1/i2/i3/i4</summary>
	public float GetRoomSize()
	{
		var code = GetHousingCode(_clientState.TerritoryType);
		if (code == null) return 40f;
		if (code.EndsWith("i2", StringComparison.Ordinal)) return 50f;
		if (code.EndsWith("i3", StringComparison.Ordinal)) return 60f;
		return 40f;
	}

	/// <summary>从 TerritoryType.Bg 提取住宅代码(如 "ffxiv/sea_s1/ind/s1i1/level/s1i1" → s1i1);房屋内部含 /ind/,非住宅返回 null</summary>
	private string? GetHousingCode(uint territoryId)
	{
		var bg = GetTerritoryBg(territoryId);
		if (bg == null) return null;
		var idx = bg.IndexOf("/ind/", StringComparison.Ordinal);
		if (idx < 0) return null;
		var rest = bg[(idx + 5)..];
		var slash = rest.IndexOf('/');
		return slash < 0 ? rest : rest[..slash];
	}

	// ==================== 行为设置(条件数据源 + 管理) ====================

	/// <summary>行为列表(配置引用,UI 直接修改后调 SaveBehaviors 持久化+重载)</summary>
	public List<BehaviorItem> GetBehaviorItems() => _config.Behaviors;

	/// <summary>分配新行为序号:自动分配,复用已删除的最小空缺(从 1 开始)</summary>
	public int AllocateBehaviorId()
	{
		var used = _config.Behaviors.Select(b => b.id).ToHashSet();
		int id = 1;
		while (used.Contains(id)) id++;
		return id;
	}

	/// <summary>删除行为并持久化+重载引擎</summary>
	public void RemoveBehavior(int id)
	{
		_config.Behaviors.RemoveAll(b => b.id == id);
		SaveBehaviors();
	}

	/// <summary>持久化行为配置并重载引擎(新增/编辑/删除/启停后调用)</summary>
	public void SaveBehaviors()
	{
		try { _config.Save(_pi); } catch (Exception e) { LogErr($"行为配置保存失败: {e.Message}"); }
		Behaviors.Reload();
	}

	// ==================== 歌单(MIDI 播放) ====================

	/// <summary>歌单列表(配置引用,前端/UI 直接修改后调 SavePlaylists 持久化);无歌单时自动创建「默认歌单」。</summary>
	public List<Playlist> GetPlaylists()
	{
		if (_config.Playlists.Count == 0)
		{
			_config.Playlists.Add(new Playlist { Name = "默认歌单" });
			SavePlaylists();
		}
		return _config.Playlists;
	}

	/// <summary>持久化歌单配置</summary>
	public void SavePlaylists()
	{
		try { _config.Save(_pi); } catch (Exception e) { LogErr($"歌单配置保存失败: {e.Message}"); }
	}

	// ==================== 玩家备注(绑定 ContentId,防改名) ====================

	/// <summary>全部备注(按更新时间倒序)</summary>
	public List<PlayerNote> GetAllNotes()
		=> _config.PlayerNotes.OrderByDescending(n => n.UpdatedAt).ToList();

	/// <summary>按 ContentId 查备注(无则 null)</summary>
	public PlayerNote? GetNote(ulong contentId)
		=> _config.PlayerNotes.FirstOrDefault(n => n.ContentId == contentId);

	/// <summary>检索备注:关键字匹配 玩家名(名字@服务器) 或 备注内容(不区分大小写)</summary>
	public List<PlayerNote> SearchNotes(string keyword)
	{
		if (string.IsNullOrWhiteSpace(keyword)) return GetAllNotes();
		var kw = keyword.Trim();
		return _config.PlayerNotes
			.Where(n => n.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
					 || n.Note.Contains(kw, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(n => n.UpdatedAt)
			.ToList();
	}

	/// <summary>保存备注(新建或更新;绑定 ContentId 防改名;Name 用当前在场名字)。内容为空 = 删除该备注。</summary>
	public bool SaveNote(ulong contentId, string name, string note)
	{
		if (contentId == 0) return false; // 无 ContentId 不存(防误建)
		var text = note.Trim();
		var exist = _config.PlayerNotes.FirstOrDefault(n => n.ContentId == contentId);
		if (exist != null)
		{
			if (text.Length == 0) { _config.PlayerNotes.Remove(exist); }
			else
			{
				exist.Name = name;
				exist.Note = text;
				exist.UpdatedAt = DateTime.Now;
			}
		}
		else
		{
			if (text.Length == 0) return false; // 空内容不新建
			_config.PlayerNotes.Add(new PlayerNote
			{
				ContentId = contentId,
				Name = name,
				Note = text,
				CreatedAt = DateTime.Now,
				UpdatedAt = DateTime.Now,
			});
		}
		SaveNotes();
		return true;
	}

	/// <summary>删除备注(按 ContentId)</summary>
	public void DeleteNote(ulong contentId)
	{
		_config.PlayerNotes.RemoveAll(n => n.ContentId == contentId);
		SaveNotes();
	}

	/// <summary>持久化备注配置</summary>
	public void SaveNotes()
	{
		try { _config.Save(_pi); } catch (Exception e) { LogErr($"备注配置保存失败: {e.Message}"); }
	}

	/// <summary>当前选中目标的玩家信息(清洗名@服务器 + ContentId);非玩家/自己返回 null。备注指令用。</summary>
	public (string nameWorld, ulong contentId)? GetTargetPlayerInfo()
	{
		var t = Plugin.TargetManager.Target;
		if (t == null || t.ObjectKind != ObjectKind.Pc) return null;
		if (t.GameObjectId == _objectTable.LocalPlayer?.GameObjectId) return null;
		if (t is not IPlayerCharacter pc) return null;
		var world = GetWorldName(pc.HomeWorld.RowId);
		var name = GetCleanName(pc.Name.TextValue);
		return (string.IsNullOrEmpty(world) ? name : $"{name}@{world}", GetPlayerContentId(pc));
	}

	// ==================== 场景设定:房子 + 可坐位置(手动标定) ====================

	/// <summary>确保房子状态有效:没有房子但有座位 → 自动建"房子1"收编;当前房子失效 → 指到第一个。返回当前房子(可能 null)。</summary>
	private House? EnsureSceneState()
	{
		var changed = false;
		if (_config.Houses.Count == 0)
		{
			if (_config.Seats.Count > 0)
			{
				_config.Houses.Add(new House { Id = 1, Name = "房子1" });
				foreach (var s in _config.Seats) s.HouseId = 1;
				_config.CurrentHouseId = 1;
				changed = true;
			}
		}
		if (_config.Houses.All(h => h.Id != _config.CurrentHouseId))
		{
			_config.CurrentHouseId = _config.Houses.Count > 0 ? _config.Houses[0].Id : 0;
			changed = true;
		}
		if (changed) try { _config.Save(_pi); } catch { }
		return _config.Houses.FirstOrDefault(h => h.Id == _config.CurrentHouseId);
	}

	/// <summary>记录当前位置为可坐点(/aca seatadd [可选名字]):先坐上去再执行;归属当前房子。名字可空(稍后网页补)。返回提示文本。</summary>
	public string AddSeatPoint(string name)
	{
		if (_framework.IsInFrameworkUpdateThread) return AddSeatPointCore(name);
		return _framework.RunOnFrameworkThread(() => AddSeatPointCore(name)).GetAwaiter().GetResult();
	}

	private string AddSeatPointCore(string name)
	{
		try
		{
			var local = _objectTable.LocalPlayer;
			if (local == null) return "未登录/无玩家,无法记录";
			var house = EnsureSceneState();
			if (house == null)
				return "还没有房子:请先到网页「场景设定」新建并选择房子,再回来记录座位";
			var seatName = (name ?? "").Trim();
			var houseSeats = _config.Seats.Where(s => s.HouseId == house.Id).ToList();
			var nextId = houseSeats.Count == 0 ? 1 : houseSeats.Max(s => s.Id) + 1;
			_config.Seats.Add(new SeatPoint
			{
				Id = nextId,
				HouseId = house.Id,
				Name = seatName, // 可空:名字稍后在网页补或一直不填
				TerritoryId = _clientState.TerritoryType,
				X = local.Position.X,
				Y = local.Position.Y,
				Z = local.Position.Z,
				Yaw = local.Rotation,
				CreatedAt = DateTime.Now,
			});
			try { _config.Save(_pi); } catch { }
			var deg = local.Rotation * 180f / MathF.PI;
			var label = seatName.Length > 0 ? seatName : $"#{nextId}";
			Log($"已记录可坐点[{house.Name}] #{nextId} {label} @ ({local.Position.X:F1},{local.Position.Z:F1}) 面向 {deg:F0}°");
			return $"已记录座位[{house.Name}] #{nextId} {label} @ ({local.Position.X:F1},{local.Position.Z:F1}) 面向 {deg:F0}°";
		}
		catch (Exception e)
		{
			LogErr($"记录可坐点异常: {e.Message}");
			return $"记录失败:{e.Message}";
		}
	}

	/// <summary>场景设定整体数据:房子列表 + 当前房子 + 全部座位(前端按房子过滤)。</summary>
	public object GetSeatsJson()
	{
		var house = EnsureSceneState();
		return new
		{
			houses = _config.Houses.OrderBy(h => h.Id).ToList(),
			current = house?.Id ?? 0,
			seats = _config.Seats.OrderBy(s => s.HouseId).ThenBy(s => s.Id).ToList(),
			obstacles = _config.Obstacles.OrderBy(o => o.HouseId).ThenBy(o => o.Id).ToList(),
			result = "success",
		};
	}

	/// <summary>设置当前房子(网页点页签时调用)。返回新当前房子 id。</summary>
	public object SetCurrentHouseJson(string json)
	{
		try
		{
			var id = JObject.Parse(json)["id"]?.Value<int>() ?? 0;
			if (_config.Houses.Any(h => h.Id == id)) _config.CurrentHouseId = id;
			try { _config.Save(_pi); } catch { }
			return new { current = _config.CurrentHouseId, result = "success" };
		}
		catch (Exception e) { return new { message = e.Message, result = "error" }; }
	}

	/// <summary>保存房子列表(新增/改名/删除)。删除房子会连带删除其座位;current 指向被删则改第一个。返回(房子列表,current)。</summary>
	public object SaveHousesJson(string json)
	{
		try
		{
			var parsed = JObject.Parse(json);
			var list = parsed["houses"]?.ToObject<List<House>>() ?? new List<House>();
			list.RemoveAll(h => string.IsNullOrWhiteSpace(h.Name) || h.Id <= 0);
			// 保证 id 唯一
			var seen = new HashSet<int>();
			foreach (var h in list) if (!seen.Add(h.Id)) h.Id = 0;
			var cur = parsed["current"]?.Value<int>() ?? 0;
			_config.Houses = list.Where(h => h.Id > 0).OrderBy(h => h.Id).ToList();
			// 清掉已删房子的座位
			var keepHouse = new HashSet<int>(_config.Houses.Select(h => h.Id));
			_config.Seats.RemoveAll(s => !keepHouse.Contains(s.HouseId));
			_config.Obstacles.RemoveAll(o => !keepHouse.Contains(o.HouseId));
			if (!_config.Houses.Any(h => h.Id == cur)) cur = _config.Houses.Count > 0 ? _config.Houses[0].Id : 0;
			_config.CurrentHouseId = cur;
			try { _config.Save(_pi); } catch { }
			return new { houses = _config.Houses, current = _config.CurrentHouseId, result = "success" };
		}
		catch (Exception e) { return new { message = e.Message, result = "error" }; }
	}

	/// <summary>场景地图:当前房子的、且在当前地区的可坐点 + 自己位置/面向(小地图 2s 轮询)。
	/// 附带 houseSeatCount/maxId 供前端判断是否需要刷新表格。</summary>
	public object GetSeatMapJson()
	{
		if (_framework.IsInFrameworkUpdateThread) return GetSeatMapJsonCore();
		return _framework.RunOnFrameworkThread(GetSeatMapJsonCore).GetAwaiter().GetResult();
	}

	private object GetSeatMapJsonCore()
	{
		var house = EnsureSceneState();
		var tid = _clientState.TerritoryType;
		var houseSeats = _config.Seats.Where(s => house == null || s.HouseId == house.Id).ToList();
		var seats = houseSeats.Where(s => s.TerritoryId == tid).OrderBy(s => s.Id).ToList();
		var houseObstacles = _config.Obstacles.Where(o => house == null || o.HouseId == house.Id).ToList();
		var obstacles = houseObstacles.Where(o => o.TerritoryId == tid).OrderBy(o => o.Id).ToList();
		var local = _objectTable.LocalPlayer;
		return new
		{
			houseId = house?.Id ?? 0,
			houses = _config.Houses.OrderBy(h => h.Id).ToList(),
			seats = seats,
			obstacles = obstacles,
			houseSeatCount = houseSeats.Count,
			houseMaxId = houseSeats.Count == 0 ? 0 : houseSeats.Max(s => s.Id),
			self = local == null ? null : new { x = local.Position.X, z = local.Position.Z, yaw = local.Rotation, tid },
			tid,
			result = "success",
		};
	}

	/// <summary>前端保存某房子(houseId)的座位清单:仅替换该房子的记录,不影响其他房子。</summary>
	public object SaveSeatsJson(string json)
	{
		try
		{
			var parsed = JObject.Parse(json);
			var houseId = parsed["houseId"]?.Value<int>() ?? 0;
			var list = parsed["seats"]?.ToObject<List<SeatPoint>>() ?? new List<SeatPoint>();
			foreach (var s in list) s.HouseId = houseId;
			list.RemoveAll(s => s.Id <= 0);
			_config.Seats.RemoveAll(s => s.HouseId == houseId);
			_config.Seats.AddRange(list);
			try { _config.Save(_pi); } catch { }
			return new { result = "success", seats = list.Count };
		}
		catch (Exception e)
		{
			return new { message = e.Message, result = "error" };
		}
	}

	/// <summary>按选择器解析"要坐的座位":空=离自己最近可用空座;#N=按 id;其他=名字部分匹配。
	/// 失败时 seat=null,error 给原因。需要主线程(ObjectTable),已自动编组。</summary>
	public SeatPoint? ResolveSeatForSitting(string selector, out string error)
	{
		if (_framework.IsInFrameworkUpdateThread) return ResolveSeatForSittingCore(selector, out error);
		var r = _framework.RunOnFrameworkThread(() =>
		{
			var res = ResolveSeatForSittingCore(selector, out var e);
			return (res, e);
		}).GetAwaiter().GetResult();
		error = r.e;
		return r.res;
	}

	private SeatPoint? ResolveSeatForSittingCore(string selector, out string error)
	{
		error = "";
		var house = EnsureSceneState();
		if (house == null) { error = "还没有房子:先到网页「场景设定」新建并选择房子"; return null; }
		var houseSeats = _config.Seats.Where(s => s.HouseId == house.Id).ToList();
		if (houseSeats.Count == 0) { error = $"房子「{house.Name}」还没记录座位(去坐一下再 /aca seatadd)"; return null; }

		var sel = (selector ?? "").Trim();
		// 最近空座:同房间优先
		if (sel.Length == 0)
		{
			var local = _objectTable.LocalPlayer;
			if (local == null) { error = "未登录/无玩家"; return null; }
			var tid = _clientState.TerritoryType;
			var cand = houseSeats
				.Where(s => s.TerritoryId == tid && !IsSeatOccupied(s))
				.OrderBy(s => PlaneDist2D(local.Position, s))
				.FirstOrDefault();
			if (cand == null)
			{
				cand = houseSeats.Where(s => !IsSeatOccupied(s)).OrderBy(s => PlaneDist2D(local.Position, s)).FirstOrDefault();
				if (cand == null) { error = "当前房子没有可用空座(都在别处或被占)"; return null; }
			}
			return cand;
		}

		// #N 按 id(调试)
		if (sel[0] == '#')
		{
			var found = houseSeats.FirstOrDefault(s => s.Id == (int.TryParse(sel[1..], out var n) ? n : -1));
			if (found == null) { error = $"没找到 #{sel[1..]} 的记录(当前房子)"; return null; }
			return found;
		}

		// 玩家名 → 该玩家旁边最近的空座(语义:"坐在 XX 旁边")。按清洗名精确匹配在场玩家。
		var player = FindPlayerByName(sel);
		if (player != null)
		{
			var tid = _clientState.TerritoryType;
			var cand = houseSeats
				.Where(s => s.TerritoryId == tid && !IsSeatOccupied(s))
				.OrderBy(s => PlaneDist2D(player.Position, s))
				.FirstOrDefault();
			if (cand == null) { error = $"{sel} 旁边没有可坐的空座(当前房间的座位都有人/离太远)"; return null; }
			var dPlayer = PlaneDist2D(player.Position, cand);
			if (dPlayer > 6f)
			{
				// 最近空座都离该玩家 6m 以上,称不上"旁边":回退,让上层决定
				error = $"{sel} 旁边的空座都太远(最近 {cand.Label()} 也有 {dPlayer:F0}m),无法坐旁边";
				return null;
			}
			return cand;
		}

		// 名字部分匹配
		var byName = houseSeats.FirstOrDefault(s => s.Name.Contains(sel, StringComparison.OrdinalIgnoreCase))
			?? houseSeats.FirstOrDefault(s => s.Label().Contains(sel, StringComparison.OrdinalIgnoreCase));
		if (byName == null) { error = $"没找到名字含「{sel}」的记录(当前房子)"; return null; }
		if (IsSeatOccupied(byName)) { error = $"座位「{byName.Label()}」上已经有人了"; return null; }
		return byName;
	}

	private static float PlaneDist2D(System.Numerics.Vector3 a, SeatPoint s)
	{
		var dx = a.X - s.X; var dz = a.Z - s.Z;
		return MathF.Sqrt(dx * dx + dz * dz);
	}

	/// <summary>当前小队成员清洗名集合(含自己;通过 GroupManager 成员 EntityId 反查 ObjectTable 名字)。
	/// 无法解析(未组队/成员都不在场景)时返回空集 → 上层视为“不过滤”(避免误伤)。</summary>
	private unsafe HashSet<string> GetPartyMemberCleanNames()
	{
		var result = new HashSet<string>();
		try
		{
			var gm = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager.Instance();
			if (gm == null || gm->MainGroup.MemberCount <= 0) return result;
			var partyIds = new HashSet<uint>();
			var max = Math.Min((int)gm->MainGroup.MemberCount, 8);
			for (var i = 0; i < max; i++) partyIds.Add(gm->MainGroup.PartyMembers[i].EntityId);
			var local = _objectTable.LocalPlayer;
			if (local != null) partyIds.Add(local.EntityId);
			foreach (var o in _objectTable)
			{
				if (o.ObjectKind != ObjectKind.Pc) continue;
				if (!partyIds.Contains(o.EntityId)) continue;
				var n = GetCleanName(o.Name.TextValue);
				if (n.Length > 0) result.Add(n);
			}
		}
		catch { /* 解析失败 = 不过滤 */ }
		return result;
	}

	/// <summary>该玩家是否当前小队的“自己人”。不是成员(且能确认有成员名单)返回 false。</summary>
	private bool IsPartySelf(string cleanName)
	{
		var names = GetPartyMemberCleanNames();
		if (names.Count == 0) return true; // 不在队/名单解析不出 → 不过滤
		return names.Contains(cleanName) || string.IsNullOrEmpty(cleanName);
	}

	/// <summary>该座位记录点是否已被别的玩家占。
	/// 半径用 0.25m(用户实测定口径):一排座椅的记录点间距通常恰好 0.6m,
	/// 若占用半径 ≥0.6,坐在相邻座(7/9)的人会落入中间空座(#8)的判定圈 → 中间座被误判“有人”(2026-09-06 实机踩坑)。
	/// 0.25 只认“几乎正坐在该记录点上”的人(坐姿本体与记录点通常 <0.1m),更保守不误伤邻座;
	/// 代价:坐得稍偏(>0.25m)的同座可能被当成没人,占用漏报可接受(反正坐下前会再校)。</summary>
	private const float SeatOccupiedRadius = 0.25f;

	private bool IsSeatOccupied(SeatPoint seat)
	{
		var local = _objectTable.LocalPlayer;
		foreach (var o in _objectTable)
		{
			if (o.ObjectKind != ObjectKind.Pc) continue;
			if (local != null && o.GameObjectId == local.GameObjectId) continue;
			var dx = o.Position.X - seat.X;
			var dz = o.Position.Z - seat.Z;
			if (dx * dx + dz * dz <= SeatOccupiedRadius * SeatOccupiedRadius) return true;
		}
		return false;
	}

	/// <summary>触发"坐法":优先执行配置的游戏宏(SeatSitMacro 0-99),否则执行 SeatSitCommand 指令。返回是否触发成功。</summary>
	public bool ExecuteSitMethod()
	{
		try
		{
			if (_config.SeatSitMacro >= 0)
			{
				var ok = Plugin.TriggerMacro(_config.SeatSitMacro, false);
				Log($"坐法: 宏 #{_config.SeatSitMacro} → {(ok ? "已触发" : "触发失败(空宏/忙/未登录)")}");
				return ok;
			}
			var cmd = string.IsNullOrWhiteSpace(_config.SeatSitCommand) ? "/interact" : _config.SeatSitCommand.Trim();
			var ok2 = RunCommandPublic(cmd);
			Log($"坐法: 指令 {cmd} → {(ok2 ? "已发送" : "发送失败")}");
			return ok2;
		}
		catch (Exception e) { LogErr($"坐法触发异常: {e.Message}"); return false; }
	}

	/// <summary>手动校准某座位的"座前站定点":站到自然准备坐的位置执行;记录站定点,去坐时自动走到这里(面向椅子交给游戏)。</summary>
	public string CalibrateSeatApproach(string selector)
	{
		if (_framework.IsInFrameworkUpdateThread) return CalibrateSeatApproachCore(selector);
		return _framework.RunOnFrameworkThread(() => CalibrateSeatApproachCore(selector)).GetAwaiter().GetResult();
	}

	private string CalibrateSeatApproachCore(string selector)
	{
		try
		{
			var local = _objectTable.LocalPlayer;
			if (local == null) return "未登录/无玩家";
			var house = EnsureSceneState();
			if (house == null) return "没有房子:先到网页建/选房子";
			var houseSeats = _config.Seats.Where(s => s.HouseId == house.Id).ToList();
			var sel = (selector ?? "").Trim();
			SeatPoint? seat = null;
			if (sel.Length == 0) seat = houseSeats.OrderBy(s => PlaneDist2D(local.Position, s)).FirstOrDefault();
			else if (sel[0] == '#')
				seat = houseSeats.FirstOrDefault(s => s.Id == (int.TryParse(sel[1..], out var n) ? n : -1));
			else
				seat = houseSeats.FirstOrDefault(s => s.Name.Contains(sel, StringComparison.OrdinalIgnoreCase))
					?? houseSeats.FirstOrDefault(s => s.Label().Contains(sel, StringComparison.OrdinalIgnoreCase));
			if (seat == null) return $"没找到匹配「{sel}」的座位(当前房子)";
			if (seat.TerritoryId != _clientState.TerritoryType) return "该座位在别的房间,先过来再校准";
			seat.HasApproach = true;
			seat.ApproachX = local.Position.X;
			seat.ApproachZ = local.Position.Z;
			try { _config.Save(_pi); } catch { }
			Log($"已校准站定点[{seat.Label()}] @ ({seat.ApproachX:F1},{seat.ApproachZ:F1})");
			return $"已记录 {seat.Label()} 的站定点 @ ({seat.ApproachX:F1},{seat.ApproachZ:F1})(去坐将停在这里再 /sit)";
		}
		catch (Exception e) { LogErr($"校准站定点异常: {e.Message}"); return $"校准失败:{e.Message}"; }
	}

	/// <summary>前端保存某房子(houseId)的障碍矩形清单(替换该房子)。记录时当前房间作为 TerritoryId;坐标自动 min/max 化。</summary>
	public object SaveObstaclesJson(string json)
	{
		try
		{
			var parsed = JObject.Parse(json);
			var houseId = parsed["houseId"]?.Value<int>() ?? 0;
			var list = parsed["obstacles"]?.ToObject<List<ObstacleRect>>() ?? new List<ObstacleRect>();
			var tid = _clientState.TerritoryType;
			foreach (var o in list)
			{
				o.HouseId = houseId;
				o.TerritoryId = tid;
				var (loX, hiX) = o.MinX <= o.MaxX ? (o.MinX, o.MaxX) : (o.MaxX, o.MinX);
				var (loZ, hiZ) = o.MinZ <= o.MaxZ ? (o.MinZ, o.MaxZ) : (o.MaxZ, o.MinZ);
				o.MinX = loX; o.MaxX = hiX; o.MinZ = loZ; o.MaxZ = hiZ;
			}
			list.RemoveAll(o => o.Id <= 0);
			_config.Obstacles.RemoveAll(o => o.HouseId == houseId);
			_config.Obstacles.AddRange(list);
			try { _config.Save(_pi); } catch { }
			return new { result = "success", obstacles = list.Count };
		}
		catch (Exception e) { return new { message = e.Message, result = "error" }; }
	}

	/// <summary>当前房子 ∩ 当前房间的障碍(避让用;纯配置读取,任意线程安全)</summary>
	public List<ObstacleRect> ObstaclesInCurrentRoom()
	{
		var house = EnsureSceneState();
		var tid = _clientState.TerritoryType;
		return _config.Obstacles.Where(o => house != null && o.HouseId == house.Id && o.TerritoryId == tid).ToList();
	}

	// ==================== 歌单 Web API ====================

	public object GetPlaylistsJson() => new { playlists = GetPlaylists(), result = "success" };

	public object SavePlaylistsJson(string json)
	{
		try
		{
			var parsed = JObject.Parse(json);
			var list = parsed["playlists"]?.ToObject<List<Playlist>>() ?? new List<Playlist>();
			foreach (var p in list) p.Items.RemoveAll(i => string.IsNullOrEmpty(i.Path));
			list.RemoveAll(p => string.IsNullOrEmpty(p.Name));
			_config.Playlists = list;
			SavePlaylists();
			return new { result = "success" };
		}
		catch (Exception e) { return new { message = e.Message, result = "error" }; }
	}

	/// <summary>开始播放:传入曲目列表(前端快照)+ 起始下标 + 模式。</summary>
	public object PlaylistPlayJson(string json)
	{
		try
		{
			var j = JObject.Parse(json);
			var list = j["items"]?.ToObject<List<PlaylistItem>>() ?? new();
			var index = j["index"]?.Value<int>() ?? 0;
			var mode = j["mode"]?.ToString();
			if (!string.IsNullOrEmpty(mode) && Enum.TryParse<PlaylistMode>(mode, true, out var m)) Playlist.Mode = m;
			Playlist.PlayQueue(list, index);
			return new { result = "success" };
		}
		catch (Exception e) { return new { message = e.Message, result = "error" }; }
	}

	/// <summary>播放控制:cmd=play/pause/resume/stop/next/prev;可选 mode。</summary>
	public object PlaylistControlJson(string json)
	{
		try
		{
			var j = JObject.Parse(json);
			var cmd = j["cmd"]?.ToString() ?? "";
			var mode = j["mode"]?.ToString();
			if (!string.IsNullOrEmpty(mode) && Enum.TryParse<PlaylistMode>(mode, true, out var m)) Playlist.Mode = m;
			switch (cmd)
			{
				case "pause": Playlist.Pause(); break;
				case "resume": Playlist.Resume(); break;
				case "stop": Playlist.Stop(); break;
				case "next": Playlist.Next(); break;
				case "prev": Playlist.Prev(); break;
				case "seek": Playlist.Seek(j["ms"]?.Value<double>() ?? 0); break;
			}
			return new { result = "success" };
		}
		catch (Exception e) { return new { message = e.Message, result = "error" }; }
	}

	public object PlaylistStatusJson() => Playlist.GetStatusJson();

	/// <summary>列出目录下的 MIDI 文件(歌单选曲用):输入 {path}。</summary>
	public object ListMidiFilesJson(string json)
	{
		try
		{
			var j = JObject.Parse(json);
			var dir = j["path"]?.ToString() ?? "";
			if (!Directory.Exists(dir)) return new { message = $"目录不存在:{dir}", result = "error" };
			var files = Directory.GetFiles(dir, "*.mid")
				.Concat(Directory.GetFiles(dir, "*.midi"))
				.Select(f => new { path = f, name = Path.GetFileName(f) })
				.OrderBy(f => f.name)
				.ToList();
			return new { current = dir, files, result = "success" };
		}
		catch (Exception e) { return new { message = e.Message, result = "error" }; }
	}

	/// <summary>返回 MIDI 的轨道列表(合奏选轨用):输入 {path} → {tracks:[{index,name}]}。</summary>
	public object GetMidiTracksJson(string json)
	{
		try
		{
			var j = JObject.Parse(json);
			var path = j["path"]?.ToString() ?? "";
			using var tmp = new Midi.MidiPlayer();
			var err = tmp.Load(path);
			if (err != null) return new { message = err, result = "error" };
			var tracks = Enumerable.Range(0, tmp.TrackCount)
				.Select(i => new { index = i, name = tmp.TrackNames[i] })
				.ToList();
			return new { tracks, result = "success" };
		}
		catch (Exception e) { return new { message = e.Message, result = "error" }; }
	}

	/// <summary>正在看我的玩家名列表(清洗后;500ms 轮询更新)</summary>
	public List<string> GetPlayersLookingAtMe() => _targetMePlayers.Select(p => p.name).ToList();

	/// <summary>最近悄悄话用户(清洗名;0C/0D 事件更新)</summary>
	public string GetLastTellUser() => _lastTellUser;

	/// <summary>最近注视用户(清洗名;新增注视者时更新)</summary>
	public string GetLastLookUser() => _lastLookUser;

	/// <summary>最近入场用户(清洗名;新玩家进入附近列表时更新)</summary>
	public string GetLastEnterUser() => _lastEnterUser;

	/// <summary>是否刚有玩家进入附近(行为条件 anyone_enter;最近 3 秒内发生入场事件)</summary>
	public bool AnyPlayerEnteredRecently() => (DateTime.Now - _lastEnterTime).TotalSeconds <= 3;

	/// <summary>最近离场用户(清洗名;玩家离开附近列表时更新)</summary>
	public string GetLastLeaveUser() => _lastLeaveUser;

	/// <summary>是否刚有玩家离开附近(行为条件 anyone_leave;最近 3 秒内发生离场事件)</summary>
	public bool AnyPlayerLeftRecently() => (DateTime.Now - _lastLeaveTime).TotalSeconds <= 3;

	/// <summary>最近对我做表情的用户(清洗名;表情目标 == 我的玩家,EmoteController 检测)</summary>
	public string GetLastEmoteUser() => _lastEmoteUser;

	/// <summary>最近对我做的表情名(Emote sheet 中文名)</summary>
	public string GetLastEmoteName() => _lastEmoteName;

	/// <summary>是否刚有人对我做表情(行为条件 anyone_emote_to_me;最近 3 秒内发生表情事件)</summary>
	public bool AnyEmoteToMeRecently() => (DateTime.Now - _lastEmoteTime).TotalSeconds <= 3;

	/// <summary>最近接触用户(清洗名):悄悄话 / 注视 / 入场任一事件更新。look 动作不带名字时用;无则空串。</summary>
	public string GetLatestContactUser() => _lastContactUser;

	/// <summary>变量中的人离场时清除对应变量(全部四个变量逐一匹配)</summary>
	private void ClearContactIfMatch(string cleanName)
	{
		if (string.IsNullOrEmpty(cleanName)) return;
		if (_lastTellUser == cleanName) _lastTellUser = "";
		if (_lastLookUser == cleanName) _lastLookUser = "";
		if (_lastEnterUser == cleanName) _lastEnterUser = "";
		if (_lastEmoteUser == cleanName) _lastEmoteUser = "";
		if (_lastContactUser == cleanName) _lastContactUser = "";
	}

	/// <summary>按清洗后名字在场景中查找玩家对象(精确匹配,忽略大小写)</summary>
	public IGameObject? FindPlayerByName(string cleanName)
	{
		foreach (var o in _objectTable)
		{
			if (o.ObjectKind != ObjectKind.Pc) continue;
			if (string.Equals(GetCleanName(o.Name.TextValue), cleanName, StringComparison.OrdinalIgnoreCase))
				return o;
		}
		return null;
	}

	/// <summary>执行 look 动作:选中目标(设置游戏选中)。name 为空 = 最近一个看向自己的人。返回是否成功。</summary>
	public bool TryLook(string name)
	{
		try
		{
			if (string.IsNullOrEmpty(name)) return false;
			var obj = FindPlayerByName(name);
			if (obj == null) return false;
			Plugin.TargetManager.Target = obj;
			return true;
		}
		catch (Exception e) { LogErr($"look 选中目标失败: {e.Message}"); return false; }
	}

	/// <summary>我的在线状态中文名(如「角色扮演中」;未登录/无状态返回空串)</summary>
	public string GetMyStatusName()
	{
		var local = _objectTable.LocalPlayer;
		if (local == null) return "";
		return GetOnlineStatusName(local.OnlineStatus.RowId);
	}

	/// <summary>我的在线状态是否为「离开」(AFK 离开状态)。行为「离开时不触发」判断用。</summary>
	public bool IsPlayerAway()
	{
		var local = _objectTable.LocalPlayer;
		if (local == null) return false;
		return GetOnlineStatusName(local.OnlineStatus.RowId) == "离开";
	}

	/// <summary>演奏就绪状态检测(主声部=自己):是否诗人 + 是否演奏模式,附提示文本。
	/// 游戏对象必须在框架线程访问(前端 HTTP 轮询会跨线程,这里自动切换)。</summary>
	public (bool isBard, bool isPerforming, string hint) GetPerformStatus()
	{
		if (_framework.IsInFrameworkUpdateThread)
			return GetPerformStatusCore();
		return _framework.RunOnFrameworkThread(GetPerformStatusCore).GetAwaiter().GetResult();
	}

	private (bool isBard, bool isPerforming, string hint) GetPerformStatusCore()
	{
		var job = GetMyJobName();
		var isBard = job == "吟游诗人";
		var isPerforming = false;
		try { isPerforming = Plugin.Condition[ConditionFlag.Performing]; }
		catch { /* 条件服务异常视为未演奏 */ }
		string hint;
		if (string.IsNullOrEmpty(job))
		{
			// 诊断:定位职业名读取失败的具体环节(本地玩家/ClassJob/职业表)
			var localOk = false;
			uint rowId = 0;
			var sheetOk = false;
			try
			{
				var local = _objectTable.LocalPlayer;
				localOk = local != null;
				if (local != null) rowId = local.ClassJob.RowId;
				sheetOk = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>() != null;
			}
			catch { }
			hint = $"未检测到角色(本地玩家={(localOk ? "有" : "无")},ClassJob={rowId},职业表={(sheetOk ? "可读" : "不可读")})";
		}
		else if (!isBard) hint = $"当前职业:{job}(演奏需要吟游诗人)";
		else if (!isPerforming) hint = "请持乐器并进入演奏模式(打开弹奏界面)";
		else hint = "就绪(吟游诗人·演奏中)";
		return (isBard, isPerforming, hint);
	}

	/// <summary>我的职业中文名(ClassJob 表;失败回退英文缩写;未登录返回空串)</summary>
	public string GetMyJobName()
	{
		try
		{
			var local = _objectTable.LocalPlayer;
			if (local == null) return "";
			var rowId = local.ClassJob.RowId;
			if (rowId == 0) return "";
			var row = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>()?.GetRow(rowId);
			if (!row.HasValue) return "";
			var name = row.Value.Name.ExtractText();
			if (name.Length > 0) return name;
			return row.Value.Abbreviation.ExtractText();
		}
		catch { return ""; }
	}

	/// <summary>当前地区名(PlaceName 中文名,如「穹顶皓天私人别墅」;未登录返回空串)</summary>
	public string GetAreaName()
	{
		try
		{
			var terr = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRow(_clientState.TerritoryType);
			if (!terr.HasValue) return "";
			var pn = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>()?.GetRow(terr.Value.PlaceName.RowId);
			return pn?.Name.ExtractText() ?? "";
		}
		catch { return ""; }
	}

	/// <summary>当前房型:S/M/L/公寓(非房区返回空串)</summary>
	public string GetRoomTypeName()
	{
		var code = GetHousingCode(_clientState.TerritoryType);
		if (string.IsNullOrEmpty(code)) return "";
		return char.ToUpperInvariant(code[0]) switch
		{
			'S' => "S",
			'M' => "M",
			'L' => "L",
			'A' => "公寓",
			_ => "",
		};
	}

	/// <summary>是否在小队中(FFXIVClientStructs GroupManager)。
	/// ✅ 实测确认(2026-08-14 /aca party):单人时 MemberCount=0、PartyId=0 → 不在队。
	/// 故 `MemberCount > 0` 即为正确判断:单人=false(not in_party=true→触发),组队=true(不触发)。
	/// ⚠️ 曾误以为单人 MemberCount=1 而改成遍历+取反,实测取反方向反而错(单人变在队),已回退。</summary>
	public unsafe bool IsInParty()
	{
		try
		{
			var gm = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager.Instance();
			return gm != null && gm->MainGroup.MemberCount > 0;
		}
		catch { return false; }
	}

	/// <summary>小队状态调试(命令 /aca party):输出 MemberCount/成员 EntityId/ContentId 与判定结果,用于排查 in_party 条件。</summary>
	public unsafe string DebugPartyInfo()
	{
		try
		{
			var sb = new System.Text.StringBuilder();
			var gm = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager.Instance();
			if (gm == null) return "GroupManager=null";
			var group = gm->MainGroup;
			sb.Append($"MemberCount={group.MemberCount} PartyId=0x{group.PartyId:X}");
			var local = _objectTable.LocalPlayer;
			sb.Append($" | myEntityId=0x{(local?.EntityId ?? 0):X}");
			ulong myCid = 0;
			if (local is IPlayerCharacter pc) myCid = GetPlayerContentId(pc);
			sb.Append($" myContentId={myCid}");
			var max = Math.Min((int)group.MemberCount, 8);
			for (var i = 0; i < max; i++)
			{
				var m = group.PartyMembers[i];
				sb.Append($" | [{i}]e=0x{m.EntityId:X} cid={m.ContentId}");
			}
			sb.Append($" => IsInParty={IsInParty()}");
			return sb.ToString();
		}
		catch (Exception e) { return $"err:{e.Message}"; }
	}

	/// <summary>是否在战斗中(游戏条件状态;供行为条件 in_combat 使用)</summary>
	public bool IsInCombat()
	{
		try { return _condition[ConditionFlag.InCombat]; }
		catch { return false; }
	}

	/// <summary>附近玩家数(不含自己;ObjectTable 实时扫描)</summary>
	public int GetNearbyPlayerCount()
	{
		var local = _objectTable.LocalPlayer;
		if (local == null) return 0;
		int n = 0;
		foreach (var o in _objectTable)
			if (o.ObjectKind == ObjectKind.Pc && o.GameObjectId != local.GameObjectId) n++;
		return n;
	}

	/// <summary>当前选中目标名(清洗后;非玩家/自己返回 null)</summary>
	public string? GetTargetName()
	{
		var t = Plugin.TargetManager.Target;
		if (t == null || t.ObjectKind != ObjectKind.Pc) return null;
		if (t.GameObjectId == _objectTable.LocalPlayer?.GameObjectId) return null;
		return t is IPlayerCharacter pc ? GetCleanName(pc.Name.TextValue) : null;
	}

	/// <summary>聊天栏提示(默语频道 /e);非框架线程自动切线程。行为「聊天内提示」用。</summary>
	public void ChatNotice(string msg)
	{
		try
		{
			if (_framework.IsInFrameworkUpdateThread) { RunCommand("/e", msg); }
			else { _framework.RunOnFrameworkThread(() => RunCommand("/e", msg)).GetAwaiter().GetResult(); }
		}
		catch (Exception e) { LogErr($"聊天提示失败: {e.Message}"); }
	}

	/// <summary>所有玩家位置(含自己)</summary>
	public List<PlayerPos> GetAllPlayerPositions()
	{
		var list = new List<PlayerPos>();
		var local = _objectTable.LocalPlayer;
		if (local == null) return list;
		list.Add(new PlayerPos { name = "", worldX = local.Position.X, worldZ = local.Position.Z, isSelf = true, entityId = local.EntityId });
		foreach (var obj in _objectTable)
		{
			if (obj.ObjectKind != ObjectKind.Pc) continue;
			if (obj.GameObjectId == local.GameObjectId) continue;
			list.Add(new PlayerPos
			{
				name = GetCleanName(obj.Name.TextValue),
				worldX = obj.Position.X,
				worldZ = obj.Position.Z,
				entityId = obj.EntityId,
			});
		}
		return list;
	}

	/// <summary>该玩家是否正在注视我(基于 500ms 轮询的注视检测)</summary>
	public bool IsLookingAtMe(uint entityId) => _targetMePlayers.Any(p => p.playerId == entityId.ToString());


	// ==================== 大模型(DeepSeek) ====================

	private void SendMsg(string msg, string msgRole, string channelNo = "", string replyAddress = "", bool triggerReply = true)
	{
		if (string.IsNullOrEmpty(_llmSetting.deepseekKey)) return;
		msg = msg.Replace("\"", "");
		var message = new Message { content = msg, role = msgRole };
		lock (_historyLock) _chatHistory.Add(message);
		if (msgRole != "user") return;
		if (!triggerReply) return; // 仅上下文采集(情感动作等),不触发回复
		// ⚠️ 无人设(currentRole 无设定)时 AI 完全不触发(不回话、不请求),消息仅留在历史/网页。
		var roleNow = GetLLMRole(_llmSetting.currentRole);
		if (string.IsNullOrEmpty(roleNow.setting))
		{
			if (!_noPersonaWarned)
			{
				_noPersonaWarned = true;
				Log($"LLM 未触发:未选择人设(currentRole={(string.IsNullOrEmpty(_llmSetting.currentRole) ? "空" : _llmSetting.currentRole)});后续不再提示,选好角色即自动启用");
			}
			return;
		}
		NotifyChatTurnPending(channelNo, replyAddress); // 进待回队列,由调度器攒消息后统一回复
	}

	// ==================== LLM 回复调度(拟真节奏:静默延迟 + 攒多条一起回) ====================

	private void OnMovementFinished(MoveResult r)
	{
		_lastMoveResult = r;
		_lastMoveResultAt = DateTime.Now;
	}

	/// <summary>收到一条会触发回复的消息:标记待回、记录频道与时间;对方继续说话时自然顺延。</summary>
	private void NotifyChatTurnPending(string channelNo, string replyAddress)
	{
		_lastTriggerChannel = channelNo;
		_lastTriggerAddr = replyAddress;
		var now = DateTime.Now;
		if (!_replyPending)
		{
			_replyPending = true;
			_replyFirstMsgAt = now;
			// 本次待回的静默时长:首次收到时在随机区间取定(连续打字只顺延不复抽,避免越等越久)
			_replySilenceSec = _config.LlmReplyDelaySecMin
				+ Random.Shared.NextDouble() * Math.Max(0, _config.LlmReplyDelaySecMax - _config.LlmReplyDelaySecMin);
		}
		_replyPendingCount++;
		_replyLastMsgAt = now;
	}

	/// <summary>回复调度泵(500ms 框架线程):对方已静默足够久 / 或说得太久该插话 → 触发一次回复。</summary>
	private void ReplyTick()
	{
		if (_replyBusy || !_replyPending) return;
		var now = DateTime.Now;
		var silentSec = (now - _replyLastMsgAt).TotalSeconds;
		var sinceFirstSec = (now - _replyFirstMsgAt).TotalSeconds;
		if (silentSec >= _replySilenceSec || sinceFirstSec >= _config.LlmReplyMaxWaitSec)
			FireReply();
	}

	/// <summary>触发一次回复(后台线程生成台词;完成后若又有新消息会重新积攒)。</summary>
	private void FireReply()
	{
		_replyPending = false;
		_replyBusy = true;
		var count = _replyPendingCount;
		_replyPendingCount = 0;
		var ch = _lastTriggerChannel;
		var addr = _lastTriggerAddr;
		Log($"LLM 回复触发: 攒了 {count} 条,频道 {ch}");
		_ = Task.Run(async () =>
		{
			try
			{
				var role = GetLLMRole(_llmSetting.currentRole);
				await RunChatTurnAsync(role, ch, addr, count > 1);
			}
			catch (Exception e)
			{
				LogErr($"LLM异常：{e}");
			}
			finally
			{
				_replyBusy = false;
			}
		});
	}

	/// <summary>执行一轮回复:选了人设 → 身体演出(可动作);无人设 → 纯文字。回复频道跟随最后一条触发消息。</summary>
	private async Task RunChatTurnAsync(Role role, string channelNo, string replyAddress, bool manyMsgs)
	{
		if (!string.IsNullOrEmpty(role.setting))
		{
			await ProcessBodyReplyAsync(role, channelNo, replyAddress, manyMsgs);
			return;
		}
		RequestBody requestBody;
		lock (_historyLock)
		{
			requestBody = new RequestBody
			{
				messages = BuildRequestMessages(manyMsgs),
				temperature = role.temperature,
				frequency_penalty = role.frequencyPenalty,
				max_tokens = role.maxTokens,
				presence_penalty = role.presencePenalty,
				stop = role.stop,
			};
		}
		var json = JsonConvert.SerializeObject(requestBody);
		Log($"LLMREQ:{json}");
		using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
		request.Headers.Add("Accept", "application/json");
		request.Headers.Add("Authorization", $"Bearer {_llmSetting.deepseekKey}");
		request.Content = new StringContent(json, null, "application/json");
		var response = await _client.SendAsync(request);
		var rspMsgString = await response.Content.ReadAsStringAsync();
		if (!response.IsSuccessStatusCode)
			throw new HttpRequestException($"错误码:{(int)response.StatusCode} rspMsg:{rspMsgString}");
		Log($"LLMRSP:{rspMsgString}");
		var rspMsg = JObject.Parse(rspMsgString)["choices"]?[0]?["message"]?.ToObject<Message>();
		var content = rspMsg?.content;
		if (!string.IsNullOrEmpty(content))
			AppendAssistantAndEcho(content, channelNo, replyAddress);
	}

	/// <summary>历史快照副本;若本次攒了多条(manyMsgs),在最后一条消息前插提示(让模型综合回应、不逐条机械回复)。</summary>
	private List<Message> BuildRequestMessages(bool manyMsgs)
	{
		List<Message> msgs;
		lock (_historyLock) msgs = _chatHistory.ToList();
		if (manyMsgs && msgs.Count > 0)
			msgs.Insert(Math.Max(0, msgs.Count - 1), new Message
			{
				role = "system",
				content = "(对方刚才短时间内连续发了几条消息:请把它们当成一件事自然地回应,不要逐条机械回复,不要复述每条)",
			});
		return msgs;
	}

	/// <summary>身体演出回复流程(可多轮工具循环):模型可并行/顺序查信息(lookup_player/list_seats),再决定台词/动作(rp_body_action)。
	/// 回复频道跟随最后一条触发消息来源频道(channelNo/replyAddress),见采集与身份链路契约。</summary>
	private async Task ProcessBodyReplyAsync(Role role, string channelNo, string replyAddress, bool manyMsgs)
	{
		var msgs = SnapshotHistoryWithScene(role, manyMsgs); // List<JObject>
		for (int round = 0; round < 6; round++)
		{
			var (content, calls, asst) = await SendBodyChatAsync(msgs, role, allowTools: true);
			if (calls.Count == 0)
			{
				if (!string.IsNullOrEmpty(content)) AppendAssistantAndEcho(content, channelNo, replyAddress);
				return;
			}
			var actionCall = calls.FirstOrDefault(c => c.name == "rp_body_action");
			bool hasAction = !string.IsNullOrEmpty(actionCall.name);
			// 信息/轻动作工具(查询 或 纯转身看向):face_player 也走回填循环,让模型决定之后说/动什么
			var infoCalls = calls.Where(c => c.name is "lookup_player" or "list_seats" or "face_player").ToList();
			if (hasAction && infoCalls.Count == 0)
			{
				// 纯动作轮:解析并执行
				string action = "", target = "";
				try { var a = JObject.Parse(actionCall.args ?? "{}"); action = (a["action"]?.ToString() ?? "").Trim().ToLowerInvariant(); target = (a["target"]?.ToString() ?? "").Trim(); } catch { }
				if (string.IsNullOrEmpty(action))
				{
					if (!string.IsNullOrEmpty(content)) AppendAssistantAndEcho(content, channelNo, replyAddress);
					return;
				}
				var actedDesc = ExecuteBodyAction(action, target);
				if (!string.IsNullOrEmpty(content))
				{
					AppendAssistantAndEcho(content, channelNo, replyAddress);
					return; // 台词已说,动作并行进行
				}
				if (actedDesc != null)
				{
					// 只做了动作没给台词 → 补一轮"说台词"
					var msgs2 = new List<JObject>(msgs)
					{
						new JObject { ["role"] = "system", ["content"] = $"(你刚才执行了身体动作:{actedDesc}。现在轮到你的角色开口,直接输出此刻该说的台词。严禁调用任何工具,严禁输出尖括号/标签/tool_calls 之类格式,只给可以开口说的话)" }
					};
					var (c2, _, _) = await SendBodyChatAsync(msgs2, role, allowTools: false);
					if (!string.IsNullOrEmpty(c2)) AppendAssistantAndEcho(c2, channelNo, replyAddress);
				}
				return;
			}

			// 有查询(可能并行多个,甚至混了动作):assistant 整条入库,再对每个 tool_call_id 逐个回填结果,让模型继续决策
			msgs.Add(asst);
			foreach (var c in calls)
			{
				var ans = c.name == "rp_body_action"
					? "(本轮先完成查询再决定动作)"
					: RunInfoTool(c.name, c.args ?? "{}");
				msgs.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = c.id, ["content"] = ans });
			}
		}
	}

	/// <summary>历史快照(转 JObject)+ 场景注入;攒了多条时插"综合回应"提示。后续信息工具轮会把 assistant(tool_calls)+tool 结果追加进来。</summary>
	private List<JObject> SnapshotHistoryWithScene(Role role, bool manyMsgs)
	{
		List<JObject> copy;
		lock (_historyLock) copy = _chatHistory.Select(m => new JObject { ["role"] = m.role ?? "user", ["content"] = m.content ?? "" }).ToList();
		if (manyMsgs && copy.Count > 0)
			copy.Insert(Math.Max(0, copy.Count - 1), new JObject { ["role"] = "system", ["content"] = "(对方刚才短时间内连续发了几条消息:请把它们当成一件事自然地回应,不要逐条机械回复,不要复述每条)" });
		var scene = BuildSceneSnippet();
		if (scene.Length > 0)
			copy.Insert(Math.Max(0, copy.Count - 1), new JObject { ["role"] = "system", ["content"] = scene });
		return copy;
	}

	/// <summary>调用 DeepSeek(带工具)。返回 (content, 本回合全部 tool_calls(name/args/id), assistant原始消息JObject)。</summary>
	private async Task<(string? content, List<(string name, string? args, string id)> calls, JObject asst)> SendBodyChatAsync(List<JObject> messages, Role role, bool allowTools)
	{
		try
		{
			var jm = new JArray();
			foreach (var m in messages) jm.Add(m);
			var body = new JObject
			{
				["model"] = "deepseek-chat",
				["stream"] = false,
				["temperature"] = role.temperature,
				["frequency_penalty"] = role.frequencyPenalty,
				["max_tokens"] = role.maxTokens,
				["presence_penalty"] = role.presencePenalty,
				["messages"] = jm,
			};
			if (role.stop != null && role.stop.Count > 0) body["stop"] = new JArray(role.stop.Select(s => JToken.FromObject(s)));
			if (allowTools) body["tools"] = BuildBodyActionTools();
			var json = body.ToString(Formatting.None);
			Log($"BODYREQ:{json}");
			using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
			request.Headers.Add("Accept", "application/json");
			request.Headers.Add("Authorization", $"Bearer {_llmSetting.deepseekKey}");
			request.Content = new StringContent(json, null, "application/json");
			var response = await _client.SendAsync(request);
			var rsp = await response.Content.ReadAsStringAsync();
			if (!response.IsSuccessStatusCode)
				throw new HttpRequestException($"错误码:{(int)response.StatusCode} rsp:{rsp}");
			Log($"BODYRSP:{rsp}");
			var msg = JObject.Parse(rsp)?["choices"]?[0]?["message"] as JObject;
			var content = msg?["content"]?.ToString();
			var calls = new List<(string name, string? args, string id)>();
			if (msg?["tool_calls"] is JArray tc)
			{
				foreach (var t in tc)
				{
					calls.Add((
						(t["function"]?["name"]?.ToString() ?? "").Trim(),
						t["function"]?["arguments"]?.ToString(),
						t["id"]?.ToString() ?? ""));
				}
			}
			// DeepSeek(v4-flash)偶发把工具调用输出成 XML 文本而非标准 tool_calls(如
			// <tool_calls><invoke name="rp_body_action"><parameter name="action" string="true">sit</parameter>…</invoke></tool_calls>)。
			// 此时标准解析 calls 为空 → 整条被当垃圾丢弃 → 动作不执行、前置台词也丢。
			// 修复:识别并恢复为结构化调用(剥 XML 块,保留前缀文本当台词)。
			if (calls.Count == 0 && !string.IsNullOrEmpty(content) && LeakedToolCallHint(content))
			{
				var restored = TryParseLeakedToolCalls(content, out var rest);
				if (restored.Count > 0)
				{
					content = rest; // 剥掉 XML 块后的剩余(通常为前置台词;纯动作轮为空)
					calls = restored;
					if (msg != null)
					{
						msg["content"] = rest; // 同步 assistant 消息本体
						var arr = new JArray();
						foreach (var (n, a, id) in restored)
							arr.Add(new JObject
							{
								["id"] = id,
								["type"] = "function",
								["function"] = new JObject { ["name"] = n, ["arguments"] = a ?? "{}" },
							});
						msg["tool_calls"] = arr;
					}
					Log($"LLM 工具调用泄漏已恢复: 解析到 {restored.Count} 个调用,剩余台词 {(string.IsNullOrEmpty(content) ? "(空,纯动作)" : $"'{content.Substring(0, Math.Min(content.Length, 40))}…'")}");
				}
			}
			return (content, calls, msg ?? new JObject());
		}
		catch (Exception e)
		{
			LogErr($"LLM 身体演出请求异常: {e.Message}");
			return (null, new List<(string, string?, string)>(), new JObject());
		}
	}

	/// <summary>执行信息型工具(模型需要时才调用;含游戏数据读取,框架线程编组)。返回纯文本结果。</summary>
	private string RunInfoTool(string toolName, string argsJson)
	{
		if (_framework.IsInFrameworkUpdateThread) return RunInfoToolCore(toolName, argsJson);
		return _framework.RunOnFrameworkThread(() => RunInfoToolCore(toolName, argsJson)).GetAwaiter().GetResult();
	}

	private string RunInfoToolCore(string toolName, string argsJson)
	{
		try
		{
			var local = _objectTable.LocalPlayer;
			switch (toolName)
			{
				case "face_player":
				{
					// 轻动作:转身看向某玩家(选中目标,不移动)。由模型在“对谁说话就看谁”时主动调用
					var target = "";
					try { target = (JObject.Parse(argsJson)["target"]?.ToString() ?? "").Trim(); } catch { }
					var who = string.IsNullOrEmpty(target) ? GetLatestContactUser() : target;
					if (string.IsNullOrEmpty(who)) return "face_player:不知道看谁(没有 target 也没有最近接触的人)";
					return TryLook(who) ? $"已转身看向 {who}" : $"看 {who} 失败(不在当前场景或找不到)";
				}
				case "lookup_player":
				{
					var name = "";
					try { name = (JObject.Parse(argsJson)["name"]?.ToString() ?? "").Trim(); } catch { }
					// name 空 = 列出当前场景在场的玩家(名字/种族/性别/在线状态/距你方位),给模型“谁在场、长什么样、在哪”
					if (string.IsNullOrEmpty(name))
					{
						if (local == null) return "尚未登录";
						var sb0 = new System.Text.StringBuilder("在场玩家:");
						var any0 = false;
						foreach (var obj in _objectTable)
						{
							if (obj.ObjectKind != ObjectKind.Pc || obj is not IPlayerCharacter pc0) continue;
							if (obj.GameObjectId == local.GameObjectId) continue;
							var n0 = GetCleanName(obj.Name.TextValue);
							if (string.IsNullOrWhiteSpace(n0)) continue;
							var d0 = MathF.Sqrt((obj.Position.X - local.Position.X) * (obj.Position.X - local.Position.X) + (obj.Position.Z - local.Position.Z) * (obj.Position.Z - local.Position.Z));
							if (d0 > 40f) continue;
							any0 = true;
							var race0 = GetRaceName(pc0.Customize.Length > 0 ? pc0.Customize[0] : (byte)0);
							var gender0 = pc0.Customize.Length > 1 && pc0.Customize[1] == 1 ? "女" : "男";
							var status0 = GetOnlineStatusName(pc0.OnlineStatus.RowId);
							var pos0 = GetLookingDirection(obj.Position, local.Position, local.Rotation);
							sb0.Append($" {n0}({race0}{gender0}{(string.IsNullOrEmpty(status0) ? "" : "," + status0)})在你{pos0}");
						}
						if (!any0) return "周围没有其他玩家";
						sb0.Append("; 想细看某人用 lookup_player 带名字");
						return sb0.ToString();
					}
					var p = FindPlayerByName(name);
					if (p == null || local == null) return $"{name}:当前场景里看不到(可能已离开或离太远),别当作在你身边";
					var dx = p.Position.X - local.Position.X; var dz = p.Position.Z - local.Position.Z;
					var d = MathF.Sqrt(dx * dx + dz * dz);
					var look = IsLookingAtMe(p.EntityId);
					// 合并“附近玩家识别出的种族/性别/职业/在线状态 + 活点地图方位”:让模型知道对方长什么样、在哪个方位
					string meta = "";
					if (p is IPlayerCharacter pc)
					{
						var race = GetRaceName(pc.Customize.Length > 0 ? pc.Customize[0] : (byte)0);
						var gender = pc.Customize.Length > 1 && pc.Customize[1] == 1 ? "女" : "男";
						var status = GetOnlineStatusName(pc.OnlineStatus.RowId);
						var dir = GetLookingDirection(p.Position, local.Position, local.Rotation);
						meta = $",{race}{gender}{(string.IsNullOrEmpty(status) ? "" : "," + status)},在你{dir}";
					}
					return $"{name}{meta}{(look ? ",正在看你" : "")}。" + (d > 5 ? "(在房间另一侧/较远,别按很近处理)" : "");
				}
				case "list_seats":
				{
					var nearName = "";
					try { nearName = (JObject.Parse(argsJson)["near"]?.ToString() ?? "").Trim(); } catch { }
					// 参考中心:给了 near 玩家名 → 以该玩家为中心列距离;否则以自己为中心(不带名字就是本房间全部座位)
					IGameObject? near = null;
					if (nearName.Length > 0)
					{
						near = FindPlayerByName(nearName);
						if (near == null) return $"list_seats:附近找不到玩家 {nearName}(可能已离开),换个名字或别传 near";
					}
					var house = EnsureSceneState();
					var tid = _clientState.TerritoryType;
					var seats = _config.Seats.Where(s => house != null && s.HouseId == house.Id && s.TerritoryId == tid).ToList();
					if (seats.Count == 0) return "当前房间没有记录的可坐座位(先去 /aca seatadd)";
					var sb = new System.Text.StringBuilder();
					if (near != null)
					{
						// 按到该玩家的距离升序,方便模型挑"旁边的座"
						sb.Append($"{nearName} 附近的座位(按距离):");
						foreach (var s in seats.OrderBy(s => PlaneDist2D(near.Position, s)))
							sb.Append($" {s.Label()}距{nearName}{PlaneDist2D(near.Position, s):F1}m{(IsSeatOccupied(s) ? "(有人)" : "(空)")}");
					}
					else
					{
						sb.Append("当前房间可坐座位:");
						foreach (var s in seats.OrderBy(s => s.Id))
							sb.Append($" {s.Label()}{(IsSeatOccupied(s) ? "(有人)" : "(空)")}");
					}
					sb.Append("; 坐别人旁边:用 sit 且 target 填那个玩家名(自动找其最近的空座),或按上面距离挑一个 #id/名字");
					return sb.ToString();
				}
				default:
					return "未知工具";
			}
		}
		catch (Exception e)
		{
			LogErr($"信息工具执行异常: {e.Message}");
			return $"查询失败:{e.Message}";
		}
	}

	/// <summary>身体工具集:rp_body_action(动作)+ 按需信息查询(lookup_player/list_seats,模型视情况主动调,避免每轮全量塞入)</summary>
	private static JArray BuildBodyActionTools()
	{
		JObject Func(string name, string desc, JObject props, string[] required)
		{
			return new JObject
			{
				["type"] = "function",
				["function"] = new JObject
				{
					["name"] = name,
					["description"] = desc,
					["parameters"] = new JObject
					{
						["type"] = "object",
						["properties"] = props,
						["required"] = new JArray(required.Select(r => JToken.FromObject(r))),
					},
				},
			};
		}
		return new JArray
		{
			Func("rp_body_action", "让角色做身体动作(走近/跟随/走开/转身面向/停止移动/坐)。approach/follow/leave/face 只能对当前在场的玩家;先想清楚目标离你多远再决定动不动,拿不准用 lookup_player。sit 目标:座位名 / #id / 玩家名(坐那个玩家旁边最近的空座,如对方邀你坐身边就用玩家名)/ 空=自己最近的空座。动作绝不写进台词。",
				new JObject
				{
					["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "approach", "follow", "leave", "face", "stop", "sit" } },
					["target"] = new JObject { ["type"] = "string", ["description"] = "approach/follow/leave/face 填玩家名(空=最近接触的人);sit 填座位名/#id,或填玩家名=坐 TA 旁边最近的空座,空=自己最近的空座;不确定哪个座空/近先调 list_seats(near=玩家名)" },
				}, new[] { "action" }),
			Func("face_player", "让角色转身看向(面向)某个在场的玩家——多人对话时,你要对谁说话、回应谁,就先 face 他;也可用于表达正在注意/看着某人。动作绝不会移动。",
				new JObject
				{
					["target"] = new JObject { ["type"] = "string", ["description"] = "要看向的玩家名(空=最近接触/最近和你说话的人)" },
				}, Array.Empty<string>()),
			Func("lookup_player", "查看在场玩家:不填 name 列出现在所有在场玩家(名字/种族/性别/在线状态/在你这边的方位);填 name 细查某一位(同上+是否正在看你)。多人时想确认现场有谁、谁长什么样、谁在看你,用它",
				new JObject
				{
					["name"] = new JObject { ["type"] = "string", ["description"] = "可选:玩家名;不填=列出所有在场玩家" },
				}, Array.Empty<string>()),
			Func("list_seats", "列出当前房间可坐的座位(是否空/距某玩家多远)。要坐下但不知道哪里有座位,或要坐在某玩家旁边时调用(near=那个玩家名,会按距离列出)",
				new JObject
				{
					["near"] = new JObject { ["type"] = "string", ["description"] = "可选:想坐在哪个玩家旁边,就填其名字;不填则列全部" },
				}, Array.Empty<string>()),
		};
	}

	/// <summary>执行身体动作(框架线程调度),返回中文结果描述(供补台词请求引用);动作未知返回 null(不补台词)。</summary>
	private string? ExecuteBodyAction(string action, string target)
	{
		if (action is not ("approach" or "follow" or "leave" or "face" or "stop" or "sit")) return null;
		if (_framework.IsInFrameworkUpdateThread) return ExecuteBodyActionCore(action, target);
		return _framework.RunOnFrameworkThread(() => ExecuteBodyActionCore(action, target)).GetAwaiter().GetResult();
	}

	private string ExecuteBodyActionCore(string action, string target)
	{
		var m = Movement;
		var who = string.IsNullOrEmpty(target) ? "最近接触的人" : target;
		try
		{
			switch (action)
			{
				case "stop":
					if (m.IsActive) { m.Stop(); return "停止了移动"; }
					return "本来就没有在移动";
				case "face":
					return m.Face(target) ? $"转身面向了 {who}" : $"面向失败(找不到 {target})";
				case "sit":
				{
					var sitMsg = m.SitOnSeat(target);
					return sitMsg.Length == 0
						? $"开始去找{(string.IsNullOrEmpty(target) ? "最近的空座" : $"座位「{target}」")}坐下"
						: $"没法坐下:{sitMsg}";
				}
				default:
					// 更换动作前先停掉旧移动
					if (m.IsActive) m.Stop();
					bool ok = action switch
					{
						"approach" => m.Approach(target),
						"follow" => m.Follow(target),
						_ => m.Leave(target),
					};
					if (ok) return action switch
					{
						"approach" => $"开始走近 {who}",
						"follow" => $"开始跟随 {who}",
						_ => $"开始走开远离 {who}",
					};
					// 失败:区分目标不存在与状态不允许
					var failDesc = !string.IsNullOrEmpty(target) && FindPlayerByName(target) == null
						? $"无法{action}:{target}不在当前场景"
						: $"无法{action}:当前状态不允许移动或已在移动中";
					Log($"身体动作失败: action={action} target={target} | {failDesc} | 移动可用={m.MovementAvailable} 总开关={_config.MovementEnabled}");
					return failDesc;
			}
		}
		catch (Exception e)
		{
			LogErr($"执行身体动作异常: {e.Message}");
			return $"动作执行出错:{e.Message}";
		}
	}

	/// <summary>内容是否像"工具调用被当台词输出"(乱码防护:命中则不发送)</summary>
	private static bool LooksLikeToolLeak(string text)
		=> text.Contains("<tool_calls", StringComparison.OrdinalIgnoreCase)
		|| text.Contains("<invoke", StringComparison.OrdinalIgnoreCase)
		|| text.Contains("<parameter", StringComparison.OrdinalIgnoreCase)
		|| text.Contains("rp_body_action", StringComparison.OrdinalIgnoreCase)
		|| text.Contains("tool_calls", StringComparison.OrdinalIgnoreCase);

	/// <summary>是否含可尝试恢复的 XML 工具块(标准 <invoke> 结构)。宽松判定,避免把正常台词误伤。</summary>
	private static bool LeakedToolCallHint(string text)
		=> text.Contains("<invoke", StringComparison.OrdinalIgnoreCase)
		|| text.Contains("<tool_calls", StringComparison.OrdinalIgnoreCase);

	/// <summary>把模型误写成文本的 XML 工具调用恢复为结构化列表。
	/// 兼容格式(带/不带引号、参数可带 string="true" 等属性、可跨行):
	///   &lt;tool_calls&gt;&lt;invoke name="rp_body_action"&gt;
	///     &lt;parameter name="action" string="true"&gt;sit&lt;/parameter&gt;
	///     &lt;parameter name="target" string="true"&gt;#8&lt;/parameter&gt;
	///   &lt;/invoke&gt;&lt;/tool_calls&gt;
	/// 返回解析出的调用(name, argsJson, 生成的唯一 id);剩余文本(rest)已把 XML 块剥掉(保留前置台词)。
	/// 解析不到任何 invoke → 返回空列表(调用方按原逻辑丢弃)。</summary>
	private static List<(string name, string? args, string id)> TryParseLeakedToolCalls(string content, out string rest)
	{
		var result = new List<(string, string?, string)>();
		// 1) 剥掉 <tool_calls> 外壳与所有 <invoke>…</invoke> 块,剩纯文本
		rest = Regex.Replace(content, @"<\s*tool_calls\s*>.*?<\s*/\s*tool_calls\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
		rest = Regex.Replace(rest, @"<\s*invoke\b.*?<\s*/\s*invoke\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
		rest = Regex.Replace(rest, @"<\s*parameter\b.*?<\s*/\s*parameter\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
		rest = Regex.Replace(rest, @"<\s*/?\s*(?:tool_calls|invoke|parameter)\s*>", "", RegexOptions.IgnoreCase);
		rest = rest.Trim();
		// 2) 逐个提取 invoke 块
		var invokeRe = new Regex(@"<\s*invoke\s+name\s*=\s*[""']?([A-Za-z0-9_]+)[""']?[^>]*>([\s\S]*?)<\s*/\s*invoke\s*>", RegexOptions.IgnoreCase);
		var paramRe = new Regex(@"<\s*parameter\s+name\s*=\s*[""']?([A-Za-z0-9_]+)[""']?[^>]*>([\s\S]*?)<\s*/\s*parameter\s*>", RegexOptions.IgnoreCase);
		var idx = 0;
		foreach (Match inv in invokeRe.Matches(content))
		{
			var name = inv.Groups[1].Value.Trim();
			if (name.Length == 0) continue;
			var j = new JObject();
			foreach (Match p in paramRe.Matches(inv.Groups[2].Value))
			{
				var pn = p.Groups[1].Value.Trim();
				var pv = System.Net.WebUtility.HtmlDecode(p.Groups[2].Value.Trim());
				if (pn.Length == 0) continue;
				j[pn] = pv;
			}
			result.Add((name, j.Count > 0 ? j.ToString(Formatting.None) : "{}", $"leak_txt_{idx++}"));
		}
		return result;
	}

	/// <summary>发送助手台词:回复跟随触发消息的来源频道(channelNo),无任何 /s 兜底;
	/// 悄悄话 0D 用 /t 回复对方(replyAddress=名字@服务器);频道无对应命令/缺回复地址时丢弃该台词(不入历史)。
	/// 历史与网页同步。</summary>
	private void AppendAssistantAndEcho(string content, string channelNo = "", string replyAddress = "")
	{
		var clean = System.Text.RegularExpressions.Regex.Replace(content, "[\u0000-\u001F]", "").Replace("\"", "");
		if (clean.Length == 0) return;

		// 0) 安全过滤:模型把工具调用当文本输出时(如 <tool_calls><invoke rp_body_action>…),丢弃不发送
		if (LooksLikeToolLeak(clean))
		{
			Log($"LLM 回复丢弃:疑似工具调用文本泄漏,不发送 | {clean.Substring(0, Math.Min(clean.Length, 80))}");
			return;
		}

		// 1) 频道路由:只回对应频道,不用 /s 兜底;悄悄话回 /t
		string cmd;
		string payload;
		if (channelNo == "0D")
		{
			if (string.IsNullOrEmpty(replyAddress))
			{
				Log("LLM 回复丢弃:悄悄话缺少回复地址(无法 /t)");
				return;
			}
			cmd = "/t";
			payload = $"{replyAddress} {clean}";
		}
		else
		{
			cmd = GetChannelSwitchs(channelNo).cmd;
			if (string.IsNullOrEmpty(cmd) || cmd == "/e")
			{
				Log($"LLM 回复丢弃:频道 {channelNo} 无对应回复命令(不兜底),台词不入历史");
				return;
			}
			payload = clean;
		}

		// 2) 发送(回复节奏已由调度器控制,此处不再二次节流)

		lock (_historyLock) _chatHistory.Add(new Message { content = clean, role = "assistant" });
		var ok = RunCommand(cmd, payload);
		Log($"LLM 台词已发({cmd}): {(ok ? "成功" : "失败(未登录等)")} | {clean}");
		if (ok)
		{
			_lastLlmEchoContent = clean; // 供自身回显去重
			_lastLlmEchoAt = DateTime.Now;
		}
	}

	/// <summary>当前场景上下文文本(身体演出模式注入;框架线程安全)。含:地区 / 在场玩家(距离,是否在看你)/ 你的移动状态。</summary>
	private string BuildSceneSnippet()
	{
		if (_framework.IsInFrameworkUpdateThread) return BuildSceneSnippetCore();
		return _framework.RunOnFrameworkThread(BuildSceneSnippetCore).GetAwaiter().GetResult();
	}

	private string BuildSceneSnippetCore()
	{
		var sb = new System.Text.StringBuilder();
		try
		{
			sb.Append("[场景] ");
			var area = GetAreaName();
			sb.Append(string.IsNullOrEmpty(area) ? "当前地区" : area).Append("; ");
			var local = _objectTable.LocalPlayer;
			if (local != null)
			{
				var localName = GetCleanName(local.Name.TextValue);
				var list = new List<(string name, float dist, bool looking)>();
				foreach (var o in _objectTable)
				{
					if (o.ObjectKind != ObjectKind.Pc) continue;
					if (o.GameObjectId == local.GameObjectId) continue;
					var raw = o.Name.TextValue;
					if (string.IsNullOrWhiteSpace(raw)) continue;
					if (GetCleanName(raw) == localName) continue; // 排除自己镜像
					var dx = o.Position.X - local.Position.X;
					var dz = o.Position.Z - local.Position.Z;
					var d = MathF.Sqrt(dx * dx + dz * dz);
					if (d > 40f) continue;
					list.Add((GetCleanName(raw), d, IsLookingAtMe(o.EntityId)));
				}
				list.Sort((a, b) => a.looking != b.looking ? (a.looking ? -1 : 1) : a.dist.CompareTo(b.dist));
				if (list.Count > 0)
				{
					sb.Append("在场:");
					foreach (var (n, d, lk) in list.Take(5))
						sb.Append(' ').Append(n).Append('(').Append(d.ToString("F0")).Append("米").Append(lk ? ",正在看你" : "").Append(')');
					sb.Append("; ");
				}
				else sb.Append("身边没有其他人; ");
			}
			sb.Append("你:").Append(Movement.StatusText()).Append(". ");
			// 最近一次移动结果(25 秒内),供模型理解刚才动作的成败
			if (_lastMoveResult != null && (DateTime.Now - _lastMoveResultAt).TotalSeconds <= 25)
				sb.Append("(刚结束的移动:").Append(_lastMoveResult).Append(")");
			sb.Append("要确认某人/自己距离用 lookup_player(不带名字=列在场玩家,含种族/性别/在线状态/在你哪边);想坐哪可 list_seats(可传 near=某人看其旁座位);移动/坐下用 rp_body_action(approach/follow/leave/face/sit/stop);对谁说话/回应谁时可用 face_player 转身看向对方(多人时尤其适用);坐某人旁边 = sit 且 target 填那个玩家名(自动找其最近空座)或按 list_seats 的距离挑 #id。距离永远以当前情况为准——对方可能已走开,别以为还在原位。动作绝不写进台词。");
		}
		catch (Exception e)
		{
			LogErr($"场景注入生成失败: {e.Message}");
		}
		return sb.ToString();
	}

	/// <summary>输出格式硬规则:追加到所有角色 system 提示末尾,禁止动作描写等(最高优先级)</summary>
	private const string OutputFormatRule =
		"## 输出规则(最高优先级,违反即不合格)\\n" +
		"- 只输出角色台词本身,禁止任何动作描写、神态描写、括号/星号/引号括注、表情符号\\n" +
		"- 禁止\"说着、笑道、点头、递茶、轻叹\"等叙述词开头或结尾\\n" +
		"- 禁止描述表情、动作、心理活动;你的每一条回复都会被直接作为游戏内台词发送\\n" +
		"- 输出必须是可以直接说出口的话,不加任何修饰";

	private void ResetChatHistory()
	{
		lock (_historyLock)
		{
			_chatHistory.Clear();
			var role = GetLLMRole(_llmSetting.currentRole);
			if (!string.IsNullOrEmpty(role.setting))
			{
				var sys = Regex.Replace(role.setting.Replace("\n", "\\n"), "[\u0000-\u001F]", " ");
				// 追加输出硬规则:所有角色统一生效,防止模型输出动作描写
				sys += "\\n" + OutputFormatRule;
				_chatHistory.Add(new Message { content = sys, role = "system" });
			}
		}
	}

	// ==================== 发消息 ====================

	private void Echo(string msg) => RunCommand("/e", msg);

	/// <summary>公开发送完整命令(如 "/e 测试"),供面板/外部调用,返回是否成功</summary>
	public bool RunCommandPublic(string fullCommand)
	{
		var idx = fullCommand.IndexOf(' ');
		var cmd = idx > 0 ? fullCommand[..idx] : fullCommand;
		var msg = idx > 0 ? fullCommand[(idx + 1)..] : "";
		return RunCommand(cmd, msg);
	}

	/// <summary>发消息(纯内置发送)。返回是否成功;异常已捕获,不会崩游戏。</summary>
	private bool RunCommand(string cmd, string msg)
	{
		try
		{
			var full = string.IsNullOrEmpty(cmd) ? msg : cmd + " " + msg;
			// 前置检查:未登录/过场中不发送,避免干扰
			if (!_clientState.IsLoggedIn) { LogErr("未登录,不发送消息"); return false; }
			if (_framework.IsInFrameworkUpdateThread)
				return GameCommandSender.Send(full);
			return _framework.RunOnFrameworkThread(() => GameCommandSender.Send(full)).GetAwaiter().GetResult();
		}
		catch (Exception e)
		{
			LogErr($"发送消息异常(已捕获): {e}");
			return false;
		}
	}

	private bool SendMessageToChannel(string channel, string msg)
	{
		var cmd = GetChannelSwitchs(channel).cmd;
		return RunCommand(cmd, msg);
	}

	/// <summary>行为发言:按频道简写发送(变量已在引擎替换;悄悄话 t 的 content 已含 "目标 内容")。返回是否成功。</summary>
	public bool SendBehaviorSay(string channelShort, string content)
	{
		var cmd = ResolveChannelCmd(channelShort);
		if (string.IsNullOrEmpty(cmd))
		{
			LogErr($"行为发言:频道 \"{channelShort}\" 无效");
			return false;
		}
		return RunCommand(cmd, content);
	}

	/// <summary>频道简写 → 命令前缀(如 p → /p):优先当前配置(用户改过频道表),兜底默认表。</summary>
	private string ResolveChannelCmd(string shortName)
	{
		foreach (var c in _msgSetting.channelConfig)
			if (!string.IsNullOrEmpty(c.cmd) && c.cmd.TrimStart('/').Equals(shortName, StringComparison.OrdinalIgnoreCase))
				return c.cmd;
		return BehaviorSyntaxDoc.SayChannels.FirstOrDefault(c => c.Short.Equals(shortName, StringComparison.OrdinalIgnoreCase))?.Cmd ?? "";
	}

	/// <summary>取其他 FFXIV 游戏窗口句柄(排除当前进程=卫月端;同机双开唯一的另一个客户端)。无则 IntPtr.Zero。</summary>
	/// <remarks>不能用 Process.MainWindowHandle:它有内部缓存,双开时可能返回过期/非游戏窗口句柄(已踩坑,实测返回已失效句柄导致 PostMessage 失败)。改用 EnumWindows 按类名 FFXIVGAME 精确查找。</remarks>
	public IntPtr GetOtherFfxivWindowHandle()
	{
		var me = Environment.ProcessId;
		var found = new List<(int pid, IntPtr hwnd)>();
		try
		{
			EnumWindows((h, l) =>
			{
				GetWindowThreadProcessId(h, out var pid);
				if (pid == 0) return true;
				var cls = GetWindowClass(h);
				if (cls != "FFXIVGAME") return true;
				if (!IsWindowVisible(h)) return true; // 隐藏窗口(如过场/加载)不算
				found.Add(((int)pid, h));
				return true;
			}, IntPtr.Zero);
		}
		catch (Exception e) { LogErr($"合奏: 枚举辅端窗口失败: {e.Message}"); return IntPtr.Zero; }

		foreach (var (pid, hwnd) in found)
		{
			if (pid == me) continue;
			Log($"合奏: 辅端窗口 PID={pid} HWND=0x{hwnd.ToInt64():X} 可见={IsWindowVisible(hwnd)}");
			return hwnd;
		}
		Log($"合奏: 未找到辅端游戏窗口(可见 FFXIVGAME 窗口 {found.Count} 个,均为自己)");
		return IntPtr.Zero;
	}

	private static string GetWindowClass(IntPtr hWnd)
	{
		var sb = new System.Text.StringBuilder(256);
		GetClassName(hWnd, sb, sb.Capacity);
		return sb.ToString();
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

	private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool IsWindowVisible(IntPtr hWnd);

	// ==================== 走路模式(自动移动直接写 Control.Instance()->IsWalking) ====================

	/// <summary>尝试把走路状态设为 walking。返回是否发生了改变(仅 false→true 算改变,供结束恢复判断)。
	/// ⚠️ 参考:FFXIVClientStructs 已公开 Control.IsWalking(玩家走路切换键改的就是它);直接写它最干净。</summary>
	public bool TrySetWalking(bool walking)
	{
		try
		{
			unsafe
			{
				var ctrl = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Instance();
				if (ctrl == null) return false;
				if (ctrl->IsWalking == walking) return false; // 状态已是目标,无变化
				ctrl->IsWalking = walking;
				Log(walking ? "走路模式已开启(IsWalking=true)" : "走路模式已恢复(IsWalking=false)");
				return true;
			}
		}
		catch (Exception e)
		{
			LogErr($"设置走路模式失败: {e.Message}");
			return false;
		}
	}

	// ==================== HTTP 方法 ====================

	public object GetConfigJson() => _msgSetting;

	public object SaveConfigJson(string configJson)
	{
		_msgSetting = JObject.Parse(configJson).ToObject<MessageSettings>() ?? new MessageSettings();
		// 跨服贝/部队/新人:强制关 LLM 采集(前端已禁,这里防手改/旧配置残留)
		foreach (var c in _msgSetting.channelConfig)
			if (c != null && NoLlmChannels.Contains(c.channel)) c.llm = false;
		_config.SetMessageSettings(_msgSetting);
		_config.Save(_pi);
		Log($"已保存配置数据: {configJson}");
		return new { message = "配置保存成功", result = "success" };
	}

	public object DeleteConfigJson(string _)
	{
		_msgSetting = Defaults.DefaultMessageSettings();
		_config.SetMessageSettings(_msgSetting);
		_config.Save(_pi);
		return new { message = "配置已重置为默认", result = "success" };
	}

	public object GetLlmConfigJson()
	{
		SyncApiKeyFromConfig(); // Key 以独立字段为准
		EnsureValidCurrentRole(); // 仅无效角色兜底;currentRole 为空=不使用人设,保持
		return _llmSetting;
	}

	public object SaveLlmConfigJson(string configJson)
	{
		_llmSetting = JObject.Parse(configJson).ToObject<LLMConfig>() ?? new LLMConfig();
		SyncApiKeyFromConfig(); // Key 以独立字段为准,忽略网页回传(防止旧 Key 覆盖)
		EnsureValidCurrentRole();
		_config.SetLlmConfig(StripKey(_llmSetting)); // LLM json 不再存 Key(独立存储)
		_config.Save(_pi);
		Log($"已保存LLM配置数据: {configJson}");
		ResetChatHistory();
		return new { message = "LLM配置保存成功", result = "success" };
	}

	public object DeleteLlmConfigJson(string _)
	{
		_llmSetting = Defaults.DefaultLlmConfig();
		SyncApiKeyFromConfig(); // 还原角色配置,DeepSeek Key 保留
		_config.SetLlmConfig(StripKey(_llmSetting));
		_config.Save(_pi);
		ResetChatHistory();
		return new { message = "LLM配置已重置为默认(DeepSeek Key 保留)", result = "success" };
	}

	/// <summary>DeepSeek API Key 独立存储(Configuration.DeepSeekApiKey),同步到运行时 LLM 配置。</summary>
	private void SyncApiKeyFromConfig() => _llmSetting.deepseekKey = _config.DeepSeekApiKey ?? "";

	/// <summary>保存 DeepSeek API Key(独立存储,不影响角色配置;网页还原默认也不清)。</summary>
	public void SaveApiKey(string key)
	{
		_config.SetApiKey(key.Trim());
		_config.Save(_pi);
		_llmSetting.deepseekKey = key.Trim();
		Log("DeepSeek API Key 已保存");
	}

	/// <summary>当前角色校验:仅当 currentRole 指向已删除的角色时,自动指向第一个角色并持久化;currentRole 为空表示"不使用人设",保持为空。</summary>
	private void EnsureValidCurrentRole()
	{
		try
		{
			if (_llmSetting.roles.Count == 0) return;
			if (!string.IsNullOrEmpty(_llmSetting.currentRole) && _llmSetting.roles.All(r => r.name != _llmSetting.currentRole))
			{
				_llmSetting.currentRole = _llmSetting.roles[0].name;
				_config.SetLlmConfig(StripKey(_llmSetting));
				_config.Save(_pi);
			}
		}
		catch (Exception e) { LogErr($"当前角色校验失败: {e.Message}"); }
	}

	/// <summary>去除 LLM 配置中的 Key 字段(Key 独立存储于 Configuration.DeepSeekApiKey,LLM json 不存 Key)。</summary>
	private static LLMConfig StripKey(LLMConfig cfg)
		=> new() { currentRole = cfg.currentRole, roles = cfg.roles };

	public object GetChatsJson(string _)
	{
		var channels = _msgSetting.channelConfig
			.Where(c => !string.IsNullOrEmpty(c.cmd))
			.Where(c => c.cmd != "/t" && c.cmd != "/r") // 发悄悄话、收悄悄话
			.Where(c => c.cmd != "/em") // 重命名原创动作
			.Concat(new[]
			{
				new ChannelConfig { channel = "1C", channelName = "/em" },
				new ChannelConfig { channel = "", channelName = "自由" },
			}).ToList();
		return new
		{
			channels,
			chats = Http?.MessageQueue.ToList() ?? new List<WebSocketMessage>(),
		};
	}

	public object SendMessageJson(string messageJson)
	{
		try
		{
			var json = JObject.Parse(messageJson);
			var channel = json["channel"]?.ToString();
			var msg = json["msg"]?.ToString();
			var ok = SendMessageToChannel(channel ?? "", msg ?? "");
			return ok
				? new { message = "发送成功", result = "success" }
				: new { message = "发送失败:游戏未就绪或消息发送异常(详见游戏日志 /xllog)", result = "error" };
		}
		catch (Exception e)
		{
			LogErr($"SendMessage 接口异常(已捕获): {e}");
			return new { message = $"发送失败:参数或通道异常", result = "error" };
		}
	}

	/// <summary>回复悄悄话:向指定玩家发送悄悄话(/t 名字@服务器 内容)。网页点"回复"按钮调用。</summary>
	public object SendTellJson(string messageJson)
	{
		try
		{
			var json = JObject.Parse(messageJson);
			var name = json["name"]?.ToString();
			var msg = json["msg"]?.ToString();
			if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(msg))
				return new { message = "回复失败:缺少玩家名或内容", result = "error" };
			// 国服 /t 强制要求 名字@服务器 格式;缺服务器名时补上本地服务器名(同服玩家兜底)
			if (!name.Contains('@'))
			{
				var world = GetLocalWorldName();
				if (!string.IsNullOrEmpty(world)) name = $"{name}@{world}";
			}
			var ok = RunCommand("/t", $"{name} {msg}");
			return ok
				? new { message = "发送成功", result = "success" }
				: new { message = "发送失败:游戏未就绪或消息发送异常(详见游戏日志 /xllog)", result = "error" };
		}
		catch (Exception e)
		{
			LogErr($"SendTell 接口异常(已捕获): {e}");
			return new { message = "回复失败:参数异常", result = "error" };
		}
	}

	/// <summary>本地玩家所在服务器名(如 红玉海),用于 /t 兜底补全</summary>
	private string GetLocalWorldName()
	{
		try
		{
			if (_playerState.HomeWorld.RowId == 0) return "";
			return _dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRow(_playerState.HomeWorld.RowId).Name.ExtractText() ?? "";
		}
		catch (Exception e)
		{
			LogErr($"获取本地服务器名失败: {e.Message}");
			return "";
		}
	}

	/// <summary>列出目录内容(网页目录选择器用):输入 {path},返回 {current, parent, directories}</summary>
	public object ListDirectoryJson(string messageJson)
	{
		try
		{
			var json = JObject.Parse(messageJson);
			var path = json["path"]?.ToString() ?? "";
			// 空路径 = 从当前日志目录(解析后)开始;无效路径退回插件目录
			var dir = string.IsNullOrEmpty(path) ? ResolvePath(_msgSetting.defaultFilePath) : Path.GetFullPath(path);
			if (!Directory.Exists(dir)) dir = ResolvePath("");
			var current = Path.GetFullPath(dir);
			var parent = Directory.GetParent(current)?.FullName ?? "";
			var directories = Directory.GetDirectories(current)
				.Select(d => Path.GetFileName(d))
				.OrderBy(d => d)
				.ToList();
			return new { current, parent, directories, result = "success" };
		}
		catch (Exception e)
		{
			LogErr($"ListDirectory 异常(已捕获): {e}");
			return new { message = e.Message, result = "error" };
		}
	}

	public object SearchChatHistoryJson(string messageJson)
	{
		var startTime = DateTime.Now;
		var json = JObject.Parse(messageJson);
		var directory = json["directory"]?.ToString();
		var keyword = json["keyword"]?.ToString();
		var dir = ResolvePath(directory ?? "./chatlogs");
		var msgs = SearchFiles(dir, keyword ?? "");
		var elapsed = (DateTime.Now - startTime).TotalSeconds;
		Log($"关键字{keyword}搜索完成，找到 {msgs.Length} 条结果，耗时 {elapsed:F2} 秒");
		return new { key = keyword, message = msgs, result = "success", length = msgs.Length, time = elapsed };
	}

	private static SearchResult[] SearchFiles(string directory, string keyword, string searchPattern = "*.txt")
	{
		try
		{
			if (!Directory.Exists(directory)) return Array.Empty<SearchResult>();
			var files = Directory.GetFiles(directory, searchPattern, SearchOption.AllDirectories);
			if (files.Length == 0) return Array.Empty<SearchResult>();
			var results = new ConcurrentBag<SearchResult>();
			Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, file =>
			{
				try
				{
					string[] lines = File.ReadAllLines(file);
					for (int i = 0; i < lines.Length; i++)
					{
						if (lines[i].IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
						results.Add(new SearchResult { FilePath = file, LineNumber = i + 1, LineContent = lines[i] });
					}
				}
				catch { }
			});
			return results.OrderBy(r => r.FilePath).ThenBy(r => r.LineNumber).ToArray();
		}
		catch { return Array.Empty<SearchResult>(); }
	}

	// ==================== 回忆检索 ====================

	private static readonly Regex MemoryLineRegex =
		new(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]\[([^\]]+)\](.*)$", RegexOptions.Compiled);

	/// <summary>搜索某玩家的聊天记录(名字部分匹配;先搜最近半年,无结果自动扩展一年)</summary>
	public MemorySearchData SearchPlayerHistory(string nameInput)
	{
		var data = new MemorySearchData { Query = nameInput };
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var dir = ResolvePath(_msgSetting.defaultFilePath);
		if (!Directory.Exists(dir)) return data;

		var cutoff = DateTime.Now.AddMonths(-6);
		var (recent, older) = PartitionLogFiles(dir, cutoff);

		foreach (var file in recent) ScanLogFile(file, nameInput, data);
		if (data.PlayerLines.Count == 0 && older.Count > 0)
		{
			data.Extended = true; // 最近半年一条都没有 → 再往前搜半年
			foreach (var file in older) ScanLogFile(file, nameInput, data);
		}

		// 按时间排序(全部行 + 该玩家行)
		data.AllLines.Sort((a, b) => a.Time.CompareTo(b.Time));
		data.PlayerLines.Sort((a, b) => a.Time.CompareTo(b.Time));

		// 截断:最多 2000 条,且总字符不超限(优先保留最近的;每行含序号/时间/频道约25字符开销)
		if (data.PlayerLines.Count > 2000)
			data.PlayerLines = data.PlayerLines.Skip(data.PlayerLines.Count - 2000).ToList();
		var totalChars = data.PlayerLines.Sum(l => l.Content.Length);
		while (data.PlayerLines.Count > 0 && totalChars > 70_000)
		{
			totalChars -= data.PlayerLines[0].Content.Length;
			data.PlayerLines.RemoveAt(0);
		}

		if (data.AllLines.Count > 0)
			data.FileRange = $"{data.AllLines[0].Time:yyyy-MM-dd} ~ {data.AllLines[^1].Time:yyyy-MM-dd}";
		sw.Stop();
		Log($"回忆检索[{nameInput}] 命中 {data.MatchedNames.Count} 个玩家 {data.PlayerLines.Count} 条消息,扫描 {data.FileCount} 个文件(扩展={(data.Extended ? "是" : "否")}),耗时 {sw.Elapsed.TotalSeconds:F1}s");
		return data;
	}

	/// <summary>扫描单个日志文件:解析全部行,过滤出目标玩家的发言</summary>
	private void ScanLogFile(string file, string nameInput, MemorySearchData data)
	{
		data.FileCount++;
		foreach (var line in ParseLogFile(file))
		{
			data.AllLines.Add(line);
			// 无冒号行(原创动作/情感动作)没有发言人,不参与匹配
			if (line.CleanName.Length > 0 && line.CleanName.Contains(nameInput, StringComparison.OrdinalIgnoreCase))
			{
				line.IsTarget = true;
				data.PlayerLines.Add(line);
				if (!data.MatchedNames.Contains(line.CleanName)) data.MatchedNames.Add(line.CleanName);
			}
		}
	}

	/// <summary>按文件名时间把日志文件分成 最近(>=cutoff 或解析不出时间) 与 更早(<cutoff) 两组</summary>
	private static (List<string> recent, List<string> older) PartitionLogFiles(string dir, DateTime cutoff)
	{
		var recent = new List<string>();
		var older = new List<string>();
		try
		{
			foreach (var file in Directory.GetFiles(dir, "*.txt", SearchOption.TopDirectoryOnly))
			{
				if (TryParseLogFileDate(file, out var date))
					(date >= cutoff ? recent : older).Add(file);
				else
					recent.Add(file); // 文件名解析不出时间的文件一律包含(宁可多搜)
			}
		}
		catch { }
		return (recent, older);
	}

	/// <summary>解析日志文件名的时间:支持 yyyy-MM-dd(日)/ yyyy-第N周(周)/ yyyy-MM(月)/ yyyy(年),解析失败返回 false</summary>
	private static bool TryParseLogFileDate(string filePath, out DateTime date)
	{
		date = default;
		var stem = Path.GetFileNameWithoutExtension(filePath);
		if (DateTime.TryParseExact(stem, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.None, out var d)) { date = d; return true; }
		var m = Regex.Match(stem, @"^(\d{4})-第(\d+)周$");
		if (m.Success)
		{
			var year = int.Parse(m.Groups[1].Value);
			var week = int.Parse(m.Groups[2].Value);
			// 与写入逻辑近似:每周一起始;这里按 1月1日+7*(周-1) 再对齐到周一
			var start = new DateTime(year, 1, 1).AddDays((week - 1) * 7);
			date = start.AddDays(-(((int)start.DayOfWeek + 6) % 7));
			return true;
		}
		if (DateTime.TryParseExact(stem, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture,
				System.Globalization.DateTimeStyles.None, out var dm)) { date = dm; return true; }
		if (int.TryParse(stem, out var y) && y is >= 2000 and <= 2100) { date = new DateTime(y, 1, 1); return true; }
		return false;
	}

	/// <summary>解析日志文件全部行:格式 [时间][频道]发言人:内容;无冒号行(原创/情感动作)无发言人</summary>
	private List<MemoryChatLine> ParseLogFile(string file)
	{
		var list = new List<MemoryChatLine>();
		try
		{
			foreach (var raw in File.ReadLines(file, Encoding.UTF8))
			{
				var m = MemoryLineRegex.Match(raw);
				if (!m.Success) continue;
				var line = new MemoryChatLine
				{
					Time = DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
						System.Globalization.CultureInfo.InvariantCulture),
					Channel = m.Groups[2].Value.Trim(),
					Raw = raw,
				};
				var rest = m.Groups[3].Value;
				var idx = rest.IndexOf(':');
				if (idx > 0)
				{
					line.Speaker = rest[..idx];
					line.Content = rest[(idx + 1)..];
					line.CleanName = GetCleanName(line.Speaker);
				}
				else
				{
					// 原创动作/情感动作等无冒号行:没有发言人,不参与玩家匹配,但保留作上下文
					line.Content = rest;
				}
				list.Add(line);
			}
		}
		catch (Exception e) { LogErr($"读取日志文件失败 {file}: {e.Message}"); }
		return list;
	}

	/// <summary>调用大模型把该玩家的消息归纳为主题(普通摘要提示词,不走角色扮演人设)</summary>
	public List<MemoryTheme> SummarizePlayerMessages(MemorySearchData data)
	{
		var result = new List<MemoryTheme>();
		if (data.PlayerLines.Count == 0) return result;
		if (string.IsNullOrEmpty(_llmSetting.deepseekKey))
		{
			LogErr("回忆检索:未配置 DeepSeek API Key,无法总结主题");
			return result;
		}

		var sb = new StringBuilder();
		for (int i = 0; i < data.PlayerLines.Count; i++)
		{
			var l = data.PlayerLines[i];
			var isMyTell = l.Channel == "发悄悄话"; // 发悄悄话 = 我方发给对方(日志里 Speaker 是收件人)
			sb.Append('[').Append(i).Append("] ").Append(l.Time.ToString("yyyy-MM-dd HH:mm"))
			  .Append(" [").Append(l.Channel).Append(isMyTell ? "·我方发" : "").Append("] ")
			  .Append(l.Content).Append('\n');
		}

		const string system = """
            你是 FF14(最终幻想14)聊天记录整理助手。下面是一个玩家的聊天记录片段,共 N 条,每条格式为:
            [序号] 时间 [频道] 内容
            其中「发悄悄话」=该玩家发出的私聊(收件人是对方),「收悄悄话」=该玩家收到的私聊,其余为公开频道发言。
            请把该玩家的发言归纳为若干大话题,最多 10 个。
            要求:
            1. 每个话题必须对应记录中一段连续的消息区间(序号连续),话题按时间顺序排列,并且话题区间必须首尾相接、覆盖全部消息:第 1 个话题必须从序号 0 开始,最后 1 个话题必须包含最后一条消息(序号 N-1)。
            2. 可以忽略纯刷屏、无意义的重复内容(如"1111""222""签到"等),但忽略后前后相邻话题的区间仍要连续覆盖,不能留下大段未覆盖的消息;若某时段没有突出话题,可用"日常闲聊"等标题兜底覆盖,但标题尽量具体。
            3. 特别注意:最后一条消息(序号 N-1)是最近的消息,必须被最后一个话题覆盖,绝对不许遗漏最新消息。
            4. 话题标题要具体简短(10 字内),让人一眼看出聊了什么(如"筹备狩猎车""讨论职业改动"),不要用"闲聊""聊天"这类空泛标题。
            5. 只输出严格 JSON,不要输出任何其他文字,格式:
            {"themes":[{"title":"话题标题","summary":"一句话概述","start":起始序号,"end":结束序号}]}
            6. start 和 end 必须是 0 到 N-1 之间的整数,end 不小于 start,话题按 start 升序排列。
            7. 如果没有可归纳的对话,输出 {"themes":[]}
            """;

		var userContent = sb.ToString();
		var prompt = system.Replace("共 N 条", $"共 {data.PlayerLines.Count} 条");
		var raw = CallDeepSeekText(prompt, userContent, 0.3, 2000);
		var themes = ParseThemeJson(raw);
		if (themes == null && raw != null)
		{
			// 输出格式不对,重试一次
			raw = CallDeepSeekText(prompt, userContent, 0.3, 2000);
			themes = ParseThemeJson(raw);
		}
		if (themes == null)
		{
			LogErr("回忆检索:LLM 返回内容无法解析为主题 JSON");
			return result;
		}

		var n = data.PlayerLines.Count;
		foreach (var t in themes.Take(10))
		{
			t.start = Math.Clamp(t.start, 0, n - 1);
			t.end = Math.Clamp(t.end, 0, n - 1);
			if (t.end < t.start) (t.start, t.end) = (t.end, t.start);
			t.StartTime = data.PlayerLines[t.start].Time;
			t.EndTime = data.PlayerLines[t.end].Time;
			t.Count = t.end - t.start + 1;
			result.Add(t);
		}
		result = result.OrderBy(t => t.start).ToList();

		// 兜底 1:一条主题都没有但确有消息 → 给一个「全部对话」主题,保证能点进去看
		if (result.Count == 0 && n > 0)
		{
			result.Add(new MemoryTheme
			{
				title = "全部对话",
				summary = "未归纳出具体主题,直接查看全部对话",
				start = 0,
				end = n - 1,
				StartTime = data.PlayerLines[0].Time,
				EndTime = data.PlayerLines[n - 1].Time,
				Count = n,
			});
		}
		// 兜底 2:最后一个主题没有覆盖到最新消息(LLM 偷懒遗漏尾部) → 自动补一个「最新动态」主题
		else if (result[^1].end < n - 1)
		{
			var startIdx = result[^1].end + 1;
			result.Add(new MemoryTheme
			{
				title = "最新动态",
				summary = "最近时间的其余聊天内容(自动补全,确保最新消息可查看)",
				start = startIdx,
				end = n - 1,
				StartTime = data.PlayerLines[startIdx].Time,
				EndTime = data.PlayerLines[n - 1].Time,
				Count = n - startIdx,
			});
			Log($"回忆检索[{data.Query}] 末尾 {n - startIdx} 条消息未被主题覆盖,已自动补充「最新动态」");
		}
		Log($"回忆检索[{data.Query}] 大模型归纳出 {result.Count} 个主题(共 {n} 条消息)");
		return result;
	}

	/// <summary>调用 DeepSeek 获取纯文本回复(同步等待;供后台任务使用;不走角色扮演设置)</summary>
	private string? CallDeepSeekText(string systemPrompt, string userContent, double temperature, int maxTokens)
	{
		try
		{
			var body = new JObject
			{
				["model"] = "deepseek-chat",
				["stream"] = false,
				["temperature"] = temperature,
				["max_tokens"] = maxTokens,
				["frequency_penalty"] = 0,
				["presence_penalty"] = 0,
				["messages"] = new JArray
				{
					new JObject { ["role"] = "system", ["content"] = systemPrompt },
					new JObject { ["role"] = "user", ["content"] = userContent },
				},
			};
			using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
			request.Headers.Add("Accept", "application/json");
			request.Headers.Add("Authorization", $"Bearer {_llmSetting.deepseekKey}");
			request.Content = new StringContent(body.ToString(Formatting.None), null, "application/json");
			var response = _client.SendAsync(request).GetAwaiter().GetResult();
			var rsp = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
			if (!response.IsSuccessStatusCode)
			{
				LogErr($"回忆检索 LLM 请求失败: 错误码 {(int)response.StatusCode} {rsp}");
				return null;
			}
			return JObject.Parse(rsp)?["choices"]?[0]?["message"]?["content"]?.ToString();
		}
		catch (Exception e)
		{
			LogErr($"回忆检索 LLM 调用异常: {e.Message}");
			return null;
		}
	}

	// ==================== AI 助手:行为命令生成(function calling) ====================

	/// <summary>是否已配置 DeepSeek API Key(未配置时 AI 生成不可用)</summary>
	public bool HasApiKey => !string.IsNullOrEmpty(_llmSetting.deepseekKey);

	/// <summary>AI 生成行为命令(function calling 单轮 + 本地解析器校验)。同步阻塞,须在后台任务中调用。</summary>
	public BehaviorGenerateResult GenerateBehavior(string description)
	{
		var result = new BehaviorGenerateResult();
		if (!HasApiKey)
		{
			result.Error = "未配置 DeepSeek API Key,请在游戏内「设置」面板(AuraCanAI 控制面板)配置后再试";
			return result;
		}
		try
		{
			var arguments = CallDeepSeekFunction(BehaviorSyntaxDoc.BuildGenPrompt(), description);
			if (string.IsNullOrWhiteSpace(arguments))
			{
				result.Error = "AI 未返回有效结果,请重试";
				return result;
			}
			JObject? args;
			try { args = JObject.Parse(arguments); }
			catch { args = null; }
			if (args == null)
			{
				// 容错:LLM 偶尔直接输出文本而非 JSON,尝试整段当 definition(再走文本规范化)
				result.Comment = "";
				result.Definition = FormatBehaviorDefinition(arguments.Trim());
			}
			else
			{
				result.Comment = args["comment"]?.ToString() ?? "";
				var rulesArr = args["rules"] as JArray;
				if (rulesArr != null && rulesArr.Count > 0)
				{
					// 结构化模式(推荐):LLM 传参数、程序拼装 → 语法顺序/关键字/条件名全部由程序保证,
					// 杜绝文本乱序/拼错;任一规则校验失败则整体报错重试(不静默丢规则)
					var errors = new List<string>();
					var segs = new List<string>();
					for (int i = 0; i < rulesArr.Count; i++)
					{
						if (rulesArr[i] is not JObject jo) { errors.Add($"第{i + 1}条规则格式非法"); continue; }
						var seg = BehaviorParser.AssembleRule(jo, errors);
						if (seg != null) segs.Add(seg);
					}
					if (errors.Count > 0 || segs.Count == 0)
					{
						result.Error = "AI 返回的规则有误,请重试: " + string.Join("; ", errors);
						return result;
					}
					result.Definition = string.Join(";\n", segs);
				}
				else
				{
					// 兼容旧格式:definition 直接给文本
					result.Definition = FormatBehaviorDefinition(args["definition"]?.ToString() ?? "");
				}
			}

			var def = result.Definition.Trim();
			if (def.Length == 0)
			{
				result.Error = "AI 返回的命令为空,请换种描述重试";
				return result;
			}

			// 不做语法校验:生成结果直接填入,统一在「保存」时校验
			// (避免 AI 生成后因校验被拒,用户无从下手;保存时出错可改描述重新生成)
			result.Success = true;
			Log($"行为AI生成成功: {result.Comment} | {def}");
			return result;
		}
		catch (Exception e)
		{
			LogErr($"行为AI生成异常: {e.Message}");
			result.Error = $"生成失败: {e.Message}";
			return result;
		}
	}

	/// <summary>AI 生成结果格式化:分号与换行都视为规则分隔,逐段做规范顺序重排(动作 → after → when → need → cooldown),
	/// 修正 LLM 输出组件乱序(如 cooldown 在中间、after 在 when 后);解析失败的段保留原文(保存时仍会报错提示)。
	/// 不做宽度折行:折行会把条件关键字/值拦腰截断导致保存时解析失败(曾踩坑:emote_to_me_name = 抚摸 被折成 抚⏎摸,
	/// 报"无法解析");单条长规则在输入框内横向滚动即可。</summary>
	private static string FormatBehaviorDefinition(string definition)
	{
		if (string.IsNullOrWhiteSpace(definition)) return definition;
		var outSegs = new List<string>();
		// 分号 → 分号+换行(规则分隔);同时保留 AI 输出的换行作为备选分隔
		foreach (var rawLine in definition.Replace(";", ";\n").Split('\n'))
		{
			var seg = CollapseWhitespace(rawLine);
			if (seg.Length == 0) continue;
			outSegs.Add(BehaviorParser.NormalizeSegment(seg));
		}
		return string.Join(";\n", outSegs);
	}

	/// <summary>空白归一化:换行/制表符/连续空格压成一个空格,去首尾空白。</summary>
	private static string CollapseWhitespace(string s)
	{
		var sb = new StringBuilder();
		var prevSpace = false;
		foreach (var ch in s)
		{
			if (char.IsWhiteSpace(ch))
			{
				if (!prevSpace) { sb.Append(' '); prevSpace = true; }
			}
			else
			{
				sb.Append(ch);
				prevSpace = false;
			}
		}
		return sb.ToString().Trim();
	}

	/// <summary>调用 DeepSeek function calling,强制调用 generate_behavior,返回函数 arguments(JSON 字符串)。</summary>
	private string? CallDeepSeekFunction(string systemPrompt, string userContent)
	{
		var body = new JObject
		{
			["model"] = "deepseek-chat",
			["stream"] = false,
			["temperature"] = 0.2,
			["max_tokens"] = 1024,
			["messages"] = new JArray
			{
				new JObject { ["role"] = "system", ["content"] = systemPrompt },
				new JObject { ["role"] = "user", ["content"] = userContent },
			},
			["tools"] = new JArray
			{
				new JObject
				{
					["type"] = "function",
					["function"] = new JObject
					{
						["name"] = "generate_behavior",
						["description"] = "生成 AuraCanAI 行为设置命令",
						["parameters"] = new JObject
						{
							["type"] = "object",
							["properties"] = new JObject
							{
								["comment"] = new JObject { ["type"] = "string", ["description"] = "行为注释,简短中文描述" },
								["rules"] = new JObject
								{
									["type"] = "array",
									["description"] = "行为规则列表(每条规则一个对象,程序自动拼接成命令文本;多条规则放多个元素)",
									["items"] = new JObject
									{
										["type"] = "object",
										["properties"] = new JObject
										{
											["action"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "trigger", "say", "look", "approach", "follow", "leave", "sit" }, ["description"] = "动作类型:trigger 触发宏 / say 频道发言 / look 选中目标 / approach 走近 / follow 跟随 / leave 走开 / sit 去坐场景设定的座位" },
											["macros"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "trigger 专用:宏编号数组,如 [\"1\",\"s2\"](共享宏加 s 前缀,0-99)" },
											["channel"] = new JObject { ["type"] = "string", ["description"] = "say 专用:频道简写 s/sh/p/a/y/b/fc/cwl1~8/t/r/em" },
											["text"] = new JObject { ["type"] = "string", ["description"] = "say 专用:发言内容(悄悄话 t 默认回最后悄悄话的人,指定对象在内容开头写 名字@服务器;可用 {变量})" },
											["target"] = new JObject { ["type"] = "string", ["description"] = "look/approach/follow/leave 填目标玩家名(空=最近接触的人);sit 填座位名或 #id(空=最近的空座)" },
											["after"] = new JObject { ["type"] = "number", ["description"] = "延迟秒数;0/缺省 = 不延迟" },
											["afterMax"] = new JObject { ["type"] = "number", ["description"] = "随机延迟上限秒(与 after 组成区间,如 after=3 afterMax=6 表示 3~6 秒随机)" },
											["cooldown"] = new JObject { ["type"] = "integer", ["description"] = "冷却秒数;0 = 无冷却;缺省 20" },
											["when"] = new JObject
											{
												["type"] = "array",
												["description"] = "触发条件列表(至少一个);每个条件对象:{cond 条件名, op 比较符, value 值, not 取反};布尔条件不填 op/value",
												["items"] = new JObject
												{
													["type"] = "object",
													["properties"] = new JObject
													{
														["cond"] = new JObject { ["type"] = "string" },
														["op"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "=", "!=", ">=", "<=" } },
														["value"] = new JObject { ["type"] = "string" },
														["not"] = new JObject { ["type"] = "boolean" },
													},
												},
											},
											["connectors"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "and", "or" } }, ["description"] = "when 条件间连接词,长度 = 条件数-1;缺省全 and" },
											["need"] = new JObject
											{
												["type"] = "array",
												["description"] = "可选:必要条件列表(结构同 when;不满足时本次触发直接丢弃)",
												["items"] = new JObject
												{
													["type"] = "object",
													["properties"] = new JObject
													{
														["cond"] = new JObject { ["type"] = "string" },
														["op"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "=", "!=", ">=", "<=" } },
														["value"] = new JObject { ["type"] = "string" },
														["not"] = new JObject { ["type"] = "boolean" },
													},
												},
											},
											["needConnectors"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray { "and", "or" } }, ["description"] = "need 条件间连接词,长度 = need 数-1;缺省全 and" },
										},
										["required"] = new JArray { "action", "when" },
									},
								},
							},
							["required"] = new JArray { "comment", "rules" },
						},
					},
				},
			},
			["tool_choice"] = new JObject
			{
				["type"] = "function",
				["function"] = new JObject { ["name"] = "generate_behavior" },
			},
		};
		using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions");
		request.Headers.Add("Accept", "application/json");
		request.Headers.Add("Authorization", $"Bearer {_llmSetting.deepseekKey}");
		request.Content = new StringContent(body.ToString(Formatting.None), null, "application/json");
		var response = _client.SendAsync(request).GetAwaiter().GetResult();
		var rsp = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
		if (!response.IsSuccessStatusCode)
		{
			LogErr($"行为AI请求失败: 错误码 {(int)response.StatusCode} {rsp}");
			return null;
		}
		var args = JObject.Parse(rsp)?["choices"]?[0]?["message"]?["tool_calls"]?[0]?["function"]?["arguments"]?.ToString();
		if (string.IsNullOrWhiteSpace(args)) LogErr($"行为AI未返回 tool_calls: {rsp}");
		return args;
	}


	/// <summary>解析 LLM 返回的主题 JSON(容错:只取首尾花括号之间的内容)</summary>
	private static List<MemoryTheme>? ParseThemeJson(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) return null;
		try
		{
			var s = text.IndexOf('{');
			var e = text.LastIndexOf('}');
			if (s < 0 || e <= s) return null;
			var obj = JObject.Parse(text.Substring(s, e - s + 1));
			if (obj["themes"] is not JArray arr) return null;
			var list = new List<MemoryTheme>();
			foreach (var item in arr)
			{
				var title = item["title"]?.ToString()?.Trim();
				if (string.IsNullOrEmpty(title)) continue;
				list.Add(new MemoryTheme
				{
					title = title,
					summary = item["summary"]?.ToString()?.Trim() ?? "",
					start = SafeThemeIndex(item["start"]),
					end = SafeThemeIndex(item["end"]),
				});
			}
			return list;
		}
		catch { return null; }
	}

	/// <summary>安全读取主题序号(容忍字符串形式的数字)</summary>
	private static int SafeThemeIndex(JToken? token)
	{
		if (token == null) return -1;
		if (token.Type == JTokenType.Integer) return token.Value<int>();
		if (token.Type == JTokenType.String && int.TryParse(token.Value<string>(), out var v)) return v;
		return -1;
	}

	// ==================== 工具 ====================

	/// <summary>解析路径:相对路径基于插件目录</summary>
	public string ResolvePath(string path)
	{
		if (string.IsNullOrEmpty(path)) path = "./chatlogs";
		if (Path.IsPathRooted(path)) return path;
		var baseDir = Path.Combine(_pi.AssemblyLocation.Directory?.FullName ?? ".", path);
		return Path.GetFullPath(baseDir);
	}

	private ChannelConfig GetChannelSwitchs(string channel)
		=> _msgSetting.channelConfig.Find(c => c.channel == channel) ?? new ChannelConfig();

	private Role GetLLMRole(string name)
		=> _llmSetting.roles.Find(r => r.name == name) ?? new Role();

	private bool IsLooking(string playerName) => _targetMePlayers.Any(c => c.name == playerName);

	/// <summary>计算目标相对本地玩家的 8 方位 + 距离(如 "左前 5米")。FFXIV 坐标 X=东 Z=南,Rotation 0=面向南(+Z),逆时针增加。</summary>
	private static string GetLookingDirection(System.Numerics.Vector3 targetPos, System.Numerics.Vector3 localPos, float localRotation)
	{
		var dx = targetPos.X - localPos.X;
		var dz = targetPos.Z - localPos.Z;
		var dist = MathF.Sqrt(dx * dx + dz * dz);
		// 本地玩家前方向量(面向南时 = +Z)
		var fx = MathF.Sin(localRotation);
		var fz = MathF.Cos(localRotation);
		// 目标相对方向与前方夹角:正值 = 右侧(FFXIV XZ 平面叉积符号需取负)
		var angle = -MathF.Atan2(dx * fz - dz * fx, dx * fx + dz * fz);
		var deg = angle * 180f / MathF.PI;
		string dir = deg switch
		{
			>= -22.5f and < 22.5f => "前方",
			>= 22.5f and < 67.5f => "右前",
			>= 67.5f and < 112.5f => "右侧",
			>= 112.5f and < 157.5f => "右后",
			>= 157.5f or < -157.5f => "后方",
			>= -157.5f and < -112.5f => "左后",
			>= -112.5f and < -67.5f => "左侧",
			_ => "左前",
		};
		return $"{dir} {dist:F0}米";
	}

	private static readonly string[] ServerNames =
	{
		"红玉海", "神意之地", "拉诺西亚", "幻影群岛", "萌芽池", "宇宙和音", "沃仙曦染", "晨曦王座", "白银乡",
		"白金幻象", "神拳痕", "潮风亭", "旅人栈桥", "拂晓之间", "龙巢神殿", "梦羽宝境", "紫水栈桥", "延夏",
		"静语庄园", "摩杜纳", "海猫茶屋", "柔风海湾", "琥珀原", "水晶塔", "银泪湖", "太阳海岸", "伊修加德", "红茶川",
	};

	/// <summary>清洗玩家名(去名字前的分组图标/序号/服务器名,显示用);public 供指令等外部取目标玩家名用</summary>
	public static string GetCleanName(string name)
	{
		var result = Regex.Replace(name, "[\ue0e1\ue071\ue072\ue073\ue090\ue091\ue092\ue093\ue094\ue095\ue096\ue097★●▲♦♥♠♣]", "");
		foreach (var s in ServerNames) result = result.Replace(s, "");
		// 跨服玩家名字形如 名字@服务器,删掉服务器名后把残留的 @ 也去掉
		return result.Trim().TrimEnd('@');
	}

	/// <summary>回复地址:从 SeString payload 取玩家名+世界ID,拼成 名字@服务器(游戏 /t 强制要求此格式);无 payload 时退回文本清洗</summary>
	private string GetReplyAddressFromSender(SeString? sender)
	{
		if (sender != null)
		{
			var payload = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
			if (payload != null && !string.IsNullOrEmpty(payload.PlayerName))
			{
				// 名字文本里已带 @服务器 则直接用
				if (payload.PlayerName.Contains('@')) return payload.PlayerName.Trim();
				var worldName = "";
				if (payload.World.RowId != 0)
					worldName = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRow(payload.World.RowId).Name.ExtractText() ?? "";
				return string.IsNullOrEmpty(worldName) ? payload.PlayerName : $"{payload.PlayerName}@{worldName}";
			}
		}
		return GetReplyAddress(sender?.TextValue ?? "");
	}

	/// <summary>回复地址(文本版兜底):只删图标/序号前缀,保留服务器名(同服=名字,跨服=名字@服务器),可直接用于 /t 指令</summary>
	private static string GetReplyAddress(string name)
	{
		var result = Regex.Replace(name, "[\ue0e1\ue071\ue072\ue073\ue090\ue091\ue092\ue093\ue094\ue095\ue096\ue097★●▲♦♥♠♣]", "");
		return result.Trim();
	}

	private void Log(string msg) => _log.Information(msg);
	private void LogErr(string msg) => _log.Error(msg);

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_chatGui.ChatMessage -= OnChatMessage;
		_clientState.TerritoryChanged -= OnTerritoryChanged;
		_clientState.Login -= OnLogin;
		_timer500?.Dispose();
		_timer2000?.Dispose();
		Movement?.Dispose();
		if (Movement != null) Movement.MovementFinished -= OnMovementFinished;
		_movementOverride?.Dispose();		StopWeb();
		Tts.Dispose();
		Playlist.Dispose();
		try { _client.Dispose(); } catch { }
	}
}
