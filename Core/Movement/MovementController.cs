using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using System.Numerics;

namespace AuraCanAI.Dalamud.Core.Movement;

/// <summary>移动结束原因(供日志/LLM/行为引擎判断后续动作)</summary>
public enum MoveOutcome
{
	Arrived, // 到达(approach 社交距离 / move 目标点 / leave 距离达成)
	SatDown, // 已坐到位(记录点坐法执行后)
	SeatOccupied, // 想坐的位子有人/没空座(点名或最近都无可用)
	WrongRoom, // 座位不在当前房间/房子
	Stuck, // 卡住(长时间无位移,可能被家具/墙挡住)
	CancelledByUser, // 用户按键打断
	NoTarget, // 目标玩家找不到/离开场景
	OutOfRange, // 目标距离超过上限(放弃远距离追击)
	Blocked, // 状态不允许移动(过场/演奏/战斗等)超时
	ZoneChanged, // 切换区域
	Stopped, // 主动停止(/aca stop 或上层调用)
	NotAvailable, // 移动覆盖不可用(签名缺失)
	Failed, // 参数/其他错误
}

/// <summary>一次移动的结果(结束原因 + 目标描述)</summary>
public class MoveResult
{
	public MoveOutcome Outcome;
	public string TargetName = ""; // 目标玩家(清洗名)/点描述
	public string Detail = ""; // 补充说明(卡住原因、阻断原因等)

	public override string ToString() => Outcome switch
	{
		MoveOutcome.Arrived => TargetName.Length > 0 ? $"已走到 {TargetName} 身边" : "已到达目标点",
		MoveOutcome.SatDown => $"已在 {(TargetName.Length > 0 ? TargetName : "记录点")} 坐下",
		MoveOutcome.SeatOccupied => "座位有人/没有可用空座",
		MoveOutcome.WrongRoom => "该座位不在当前房间",
		MoveOutcome.Stuck => "走不过去了(可能被挡住)",
		MoveOutcome.CancelledByUser => "移动被你的操作打断",
		MoveOutcome.NoTarget => $"找不到目标({TargetName})",
		MoveOutcome.OutOfRange => $"目标太远({TargetName})",
		MoveOutcome.Blocked => $"当前状态不能移动({Detail})",
		MoveOutcome.ZoneChanged => "切换了区域,移动中止",
		MoveOutcome.Stopped => "已停止移动",
		MoveOutcome.NotAvailable => "移动功能不可用(签名缺失,见日志)",
		_ => Detail,
	};
}

/// <summary>
/// 移动控制器(高层):把"走近/跟随/走开/走到点/面向"翻译成对 MovementOverride 的逐帧驱动。
/// - 每个意图是一个持续到"完成"的状态机;完成时触发 MovementFinished 事件并广播给上层
///   (行为引擎 / LLM 身体演出模式 / 网页都挂在上面);
/// - 无避障:被墙/家具挡住 → 位移停滞 StuckTimeoutMs 后以 Stuck 结束(上层改说台词,不硬挤);
/// - 护栏:过场/演奏/骑乘/战斗中(可选)/加载中 不移动;用户按键 = 人工接管并结束;
/// - 面向 = 直接选中目标(游戏站定时自动面向所选目标);不引入相机/转身注入。
/// Update 由 Framework.Update 驱动(游戏主线程),与 detour 同线程。
/// </summary>
public sealed class MovementController : IDisposable
{
	// ===== 依赖 =====
	private readonly AuraCanAiCore _core;
	private readonly Configuration _config;
	private readonly MovementOverride _override;
	private readonly ICondition _condition;
	private readonly IClientState _clientState;
	private readonly IObjectTable _objectTable;
	private readonly IFramework _framework;
	private readonly IPluginLog _log;

	// ===== 当前意图状态 =====
	private enum IntentKind { None, Approach, Follow, Leave, MoveTo, Sit }


	private bool _active;
	private IntentKind _intent;
	private string _targetName = ""; // 目标玩家(清洗名;Approach/Follow/Leave 用;MoveTo 为空)
	private Vector3 _fixedDest; // MoveTo 固定目标点
	private float _stopDistance = 3f; // 走近停止距离(Approach 到达 / Follow 保持下限)
	private float _resumeDistance = 4f; // Follow 超过该距离恢复移动
	private float _leaveDistance = 8f; // Leave 走到离目标该距离
	private float _maxRange = 60f; // 超过放弃(0 = 不限)
	private bool _faceOnArrive; // 到达后选中目标(让角色自动面向对方)
	private DateTime _startedAt;
	private DateTime _blockedSince = DateTime.MinValue; // 中途被阻断的开始时间
	private Vector3? _lastPos; // 卡住检测
	private DateTime _lastPosAt;
	private float _stuckSeconds; // 无位移累计秒数
	private bool _finishedNotified = true; // 事件是否已通知(防重入)
	// 走路模式:启动时若在跑步则直接写 Control.IsWalking=true 切走路,_walkForced=true;结束恢复(false)
	private bool _walkForced;
	// 障碍绕行侧向:0=自动选近侧;±1=强制左/右侧;卡住时自动换另一侧一次
	private int _avoidSide;
	private bool _avoidFlipped;
	private readonly Queue<System.Numerics.Vector3> _avoidWaypoints = new();
	private System.Numerics.Vector3 _routeGoal; // 当前路线的终点(目标移动>0.6m 则清空路线重排)
	// ===== 坐(IntentKind.Sit):目标座位记录 + 阶段 =====
	private enum SitPhase { Walk, Pause, TriggerSit, StandUp, Confirm, Done }
	private SitPhase _sitPhase;
	private SeatPoint _sitSeat = null!; // 当前要坐的座位记录(来自 core)
	private System.Numerics.Vector3 _sitApproach; // 座前站定参考点
	private DateTime _sitStageAt;
	private int _sitAttempts; // 坐下尝试次数(最多 3;失败则起立挪位重试)
	private System.Numerics.Vector3 _standStartPos; // StandUp 阶段起点(判断是否已起立挪开)
	private System.Numerics.Vector3 _seatMoveLastPos; // 坐流程走位卡住检测
	private DateTime _seatMoveLastAt = DateTime.MinValue;
	private const float SitCheckTolerance = 0.35f; // 坐下后与记录点平面距离 ≤ 此值视为坐正
	private const int MaxSitAttempts = 2; // 初始 1 次 + 失败后再试 1 次(共 2),仍歪即放弃

	public bool IsActive => _active;
	public bool MovementAvailable => _override.WalkAvailable;
	public string IntentLabel => _active ? _intent switch
	{
		IntentKind.Approach => "走近",
		IntentKind.Follow => "跟随",
		IntentKind.Leave => "走开",
		IntentKind.MoveTo => "走到点",
		IntentKind.Sit => "去坐",
		_ => "",
	} : "";

	/// <summary>移动结束事件(outcome, 结果对象)。上层(行为引擎/LLM/网页)在此挂接"到了之后说什么/做什么"。</summary>
	public event Action<MoveResult>? MovementFinished;

	public MovementController(AuraCanAiCore core, Configuration config, MovementOverride movementOverride,
		ICondition condition, IClientState clientState, IObjectTable objectTable, IFramework framework, IPluginLog log)
	{
		_core = core;
		_config = config;
		_override = movementOverride;
		_condition = condition;
		_clientState = clientState;
		_objectTable = objectTable;
		_framework = framework;
		_log = log;
		_framework.Update += OnFrameworkUpdate;
	}

	public void Dispose()
	{
		_framework.Update -= OnFrameworkUpdate;
		Finish(MoveOutcome.Stopped, "插件卸载");
	}

	// ==================== 公共 API(游戏主线程或任意线程调用,自动切线程) ====================

	public bool Approach(string name, float stopDist = -1, bool faceOnArrive = true)
		=> BeginTarget(IntentKind.Approach, name, stopDist, faceOnArrive);

	public bool Follow(string name, float keepDist = -1)
		=> BeginTarget(IntentKind.Follow, name, keepDist, false);

	public bool Leave(string name, float leaveDist = -1)
		=> BeginTarget(IntentKind.Leave, name, leaveDist, false);

	/// <summary>走到世界坐标点。y 可传任意值(地面步行只看平面,可传 0)。</summary>
	public bool MoveToPoint(Vector3 point)
	{
		if (!PrepareStart("MoveToPoint")) return false;
		_active = true;
		_intent = IntentKind.MoveTo;
		_targetName = "";
		_fixedDest = point;
		_stopDistance = 0.6f; // 到点判定半径
		_faceOnArrive = false;
		StartRuntime();
		return true;
	}

	/// <summary>面向某人:选中目标即可(游戏站定时角色自动面向所选目标)。不带名字 = 最近接触你的人。</summary>
	public bool Face(string name)
	{
		var target = string.IsNullOrEmpty(name) ? _core.GetLatestContactUser() : name;
		if (string.IsNullOrEmpty(target)) return false;
		return _core.TryLook(target);
	}

	/// <summary>坐到记录点:selector 空=离自己最近的可用空座;"#id"=按 id(调试);其他按名字部分匹配(如 窗边沙发)。
	/// 返回空串=已开始;非空=失败原因(状态/占用/房间/找不到等)。</summary>
	public string SitOnSeat(string selector)
	{
		if (_active) return "已有进行中的移动,先 /aca stop";
		if (!_override.WalkAvailable) return $"移动不可用:{_override.UnavailableReason}";
		var block = GetBlockReason();
		if (block != null) return $"当前状态不能移动({block})";
		var seat = _core.ResolveSeatForSitting(selector, out var err);
		if (seat == null) return err;
		// 目标在别的房间/房子 → 拒绝
		if (seat.TerritoryId != _clientState.TerritoryType)
		{
			_log.Information($"坐: 座位 [{seat.Label()}] 在别的房间,拒绝");
			return "该座位在别的房间(请先进到对应房子/楼层再试)";
		}
		_active = true;
		_intent = IntentKind.Sit;
		_sitSeat = seat;
		_targetName = seat.Label();
		// 站定点:优先用该座位的手动校准点(/aca seatstand),否则默认推算(座位点+沿坐姿朝向 N 米)
		if (seat.HasApproach)
		{
			_sitApproach = new System.Numerics.Vector3(seat.ApproachX, seat.Y, seat.ApproachZ);
		}
		else
		{
			var f = seat.Yaw;
			_sitApproach = new System.Numerics.Vector3(
				seat.X + MathF.Sin(f) * _config.SeatApproachDistance,
				seat.Y,
				seat.Z + MathF.Cos(f) * _config.SeatApproachDistance);
		}
		_sitPhase = SitPhase.Walk;
		_sitAttempts = 1;
		StartRuntime();
		_log.Information($"坐: 选座 [{seat.Label()}] 站定参考点 {_sitApproach} (校准={(seat.HasApproach ? "是" : "否")})");
		return "";
	}

	/// <summary>主动停止当前移动</summary>
	public void Stop()
	{
		if (_active) Finish(MoveOutcome.Stopped, "手动停止");
	}

	/// <summary>切换区域通知(上层 OnTerritoryChanged 调用):中止一切移动</summary>
	public void NotifyZoneChanged()
	{
		if (_active) Finish(MoveOutcome.ZoneChanged, "");
	}

	/// <summary>当前状态文本(命令/UI/网页显示)</summary>
	public string StatusText()
	{
		if (!_active) return MovementAvailable ? "空闲" : $"不可用:{_override.UnavailableReason}";
		if (_intent == IntentKind.Sit)
			return _sitSeat != null ? $"去坐 {(string.IsNullOrEmpty(_sitSeat.Name) ? "#" + _sitSeat.Id : _sitSeat.Name)}" : "去坐";
		var target = _intent == IntentKind.MoveTo ? $"({_fixedDest.X:F1}, {_fixedDest.Y:F1}, {_fixedDest.Z:F1})" : _targetName;
		return $"{IntentLabel} {target}";
	}

	/// <summary>状态 JSON(网页/后续 LLM 场景上下文用)</summary>
	public object GetStatusJson() => new
	{
		active = _active,
		available = MovementAvailable,
		intent = _intent.ToString(),
		target = _targetName,
		unavailableReason = _override.UnavailableReason,
		mode = _override.IsLegacyMode ? "legacy" : "standard",
	};

	// ==================== 内部 ====================

	private bool BeginTarget(IntentKind kind, string name, float distParam, bool faceOnArrive)
	{
		var target = string.IsNullOrEmpty(name) ? _core.GetLatestContactUser() : name;
		if (string.IsNullOrEmpty(target))
		{
			_log.Warning($"移动: 未指定目标且没有最近接触的人");
			return false;
		}
		if (!PrepareStart(kind.ToString())) return false;
		_active = true;
		_intent = kind;
		_targetName = target;
		var dist = distParam > 0 ? distParam : (kind switch
		{
			IntentKind.Approach => _config.MovementStopDistance,
			IntentKind.Follow => _config.MovementFollowKeepDistance,
			_ => _config.MovementLeaveDistance,
		});
		_stopDistance = MathF.Max(0.5f, dist);
		_resumeDistance = _stopDistance + MathF.Max(1f, _config.MovementFollowResumeExtra);
		_leaveDistance = dist;
		_faceOnArrive = faceOnArrive;
		StartRuntime();
		return true;
	}

	private bool PrepareStart(string what)
	{
		if (_active)
		{
			_log.Warning($"移动: 已有进行中的移动({IntentLabel}),先 /aca stop");
			return false;
		}
		if (!_override.WalkAvailable)
		{
			_log.Error($"移动: {what} 失败 - {_override.UnavailableReason}");
			Finish(MoveOutcome.NotAvailable, _override.UnavailableReason);
			return false;
		}
		if (!_config.MovementEnabled)
		{
			_log.Information($"移动: {what} 被拒(移动总开关已关)");
			return false;
		}
		var block = GetBlockReason();
		if (block != null)
		{
			_log.Information($"移动: {what} 被拒({block})");
			return false;
		}
		return true;
	}

	private void StartRuntime()
	{
		_startedAt = DateTime.Now;
		_blockedSince = DateTime.MinValue;
		_avoidWaypoints.Clear();
		_lastPos = null;
		_stuckSeconds = 0;
		_finishedNotified = false;
		_override.Active = false;
		// 走路模式:开启且当前不在走路 → 直接写 IsWalking=true(游戏走路切换的真实状态),结束恢复
		_walkForced = _config.MovementUseWalkMode && _core.TrySetWalking(true);
		_avoidSide = 0;
		_avoidFlipped = false;
		// 诊断:本次移动的当前房间障碍数
		var obsN = _core.ObstaclesInCurrentRoom().Count;
		if (obsN > 0) _log.Information($"移动开始: 当前房间障碍 {obsN} 个");
		_log.Information($"移动开始: {IntentLabel} {(_targetName.Length > 0 ? _targetName : _fixedDest.ToString("F1"))}");
	}

	private void OnFrameworkUpdate(IFramework framework)
	{
		if (!_active || _finishedNotified) return;
		try
		{
			UpdateActive();
		}
		catch (Exception e)
		{
			_log.Error($"移动控制器异常: {e}");
			Finish(MoveOutcome.Failed, e.Message);
		}
	}

	private void UpdateActive()
	{
		var player = _objectTable.LocalPlayer;
		if (player == null)
		{
			Finish(MoveOutcome.NoTarget, "本地玩家不可用");
			return;
		}

		// 目标位置解析(Approach/Follow/Leave 每帧刷新,支持目标走动)
		Vector3? targetPos = null;
		if (_intent != IntentKind.MoveTo && _intent != IntentKind.Sit)
		{
			var target = _core.FindPlayerByName(_targetName);
			if (target == null)
			{
				Finish(MoveOutcome.NoTarget, _targetName);
				return;
			}
			targetPos = target.Position;
		}

		var myPos = player.Position;
		var dist = targetPos != null
			? PlaneDistance(myPos, targetPos.Value)
			: PlaneDistance(myPos, _fixedDest);

		// 1) 用户按键 = 人工接管
		if (!_override.IgnoreUserInput && _override.UserInput && _config.MovementCancelOnUserInput)
		{
			Finish(MoveOutcome.CancelledByUser, "");
			return;
		}

		// 2) 状态阻断(过场/演奏/战斗中...)——中途阻断:先暂停等待,超时放弃
		var block = GetBlockReason();
		if (block != null)
		{
			_override.Active = false; // 暂停移动
			if (_blockedSince == DateTime.MinValue) _blockedSince = DateTime.Now;
			if ((DateTime.Now - _blockedSince).TotalSeconds > _config.MovementBlockTimeoutSec)
			{
				Finish(MoveOutcome.Blocked, block);
			}
			_lastPos = null; // 阻断期间不算卡住
			_stuckSeconds = 0;
			return;
		}
		_blockedSince = DateTime.MinValue;

		// 2.5) 坐(IntentKind.Sit)专用流程
		if (_intent == IntentKind.Sit)
		{
			UpdateSeatFlow(myPos, DateTime.Now);
			return;
		}

		// 3) 距离控制
		switch (_intent)
		{
			case IntentKind.Approach:
				if (dist <= _stopDistance)
				{
					ArriveAtSocialDistance(); // 到社交距离
					return;
				}
				break;
			case IntentKind.Follow:
				if (dist <= _stopDistance)
				{
					_override.Active = false; // 在保持距离内,原地等待目标走远
					_lastPos = null;
					_stuckSeconds = 0;
					return;
				}
				if (dist > _resumeDistance) break; // 超距 → 继续走近
				_override.Active = false; // 在 [stop, resume] 之间微距,不动(防抖)
				_lastPos = null;
				_stuckSeconds = 0;
				return;
			case IntentKind.Leave:
				if (dist >= _leaveDistance)
				{
					Finish(MoveOutcome.Arrived, $"已走开到 {_leaveDistance:F0} 米外");
					return;
				}
				break;
			case IntentKind.MoveTo:
				if (dist <= _stopDistance)
				{
					Finish(MoveOutcome.Arrived, "已到达目标点");
					return;
				}
				break;
		}

		// 4) 距离上限(Approach/Follow 不追太远的人)
		if ((_intent is IntentKind.Approach or IntentKind.Follow) && _maxRange > 0 && dist > _maxRange)
		{
			Finish(MoveOutcome.OutOfRange, $"{dist:F0} 米");
			return;
		}

		// 5) 设定目标点并激活覆盖
		Vector3 desired;
		if (_intent == IntentKind.Leave)
		{
			// 朝远离目标方向持续迈步
			var away = PlaneNormalized(myPos - targetPos!.Value);
			desired = myPos + away * 2f; // 每帧向远处推 2 米参考点(实际到 leaveDistance 即停)
		}
		else
		{
			desired = ComputePath(myPos, targetPos ?? _fixedDest, _avoidWaypoints);
		}
		_override.DesiredPosition = desired;
		_override.Active = true;

		// 6) 卡住检测:激活移动但位置无明显前进
		var now = DateTime.Now;
		if (_lastPos == null)
		{
			_lastPos = myPos;
			_lastPosAt = now;
			_stuckSeconds = 0;
		}
		else
		{
			var delta = (float)(now - _lastPosAt).TotalSeconds;
			if (delta > 0.1f)
			{
				var moved = PlaneDistance(myPos, _lastPos.Value);
				// 每秒位移 < 0.15 米视为停滞
				if (moved / delta < 0.15f) _stuckSeconds += delta;
				else _stuckSeconds = 0;
				_lastPos = myPos;
				_lastPosAt = now;
				if (_stuckSeconds >= _config.MovementStuckTimeoutSec)
				{
					// 卡住换侧:当前侧是死路(靠墙/死角)时自动换另一侧绕一次;换过仍卡才真正停下
					var obs = _core.ObstaclesInCurrentRoom();
					if (obs.Count > 0 && !_avoidFlipped)
					{
						var ob = obs[0];
						var near = MathF.Abs(myPos.X - ob.MinX) <= MathF.Abs(myPos.X - ob.MaxX) ? -1 : 1;
						_avoidSide = -near;
						_avoidFlipped = true;
						_avoidWaypoints.Clear();
						_stuckSeconds = 0;
						_lastPos = null;
						_log.Information("避障: 当前侧走不通,换另一侧绕行一次");
						return;
					}
					Finish(MoveOutcome.Stuck, $"已停滞 {_stuckSeconds:F0} 秒(可能被挡住)");
					return;
				}
			}
		}

		// 7) 防挂机 + 超时兜底
		MovementOverride.ResetAfkTimers();
		if ((now - _startedAt).TotalSeconds > _config.MovementMaxDurationSec)
		{
			Finish(MoveOutcome.Stuck, "移动超时(可能一直到不了)");
		}
	}

	/// <summary>坐流程走路卡住检测:0.5 秒位置没动(位移 <0.12m)即判卡住并结束。返回 true = 已结束。</summary>
	private bool SeatMoveStuck(System.Numerics.Vector3 myPos, DateTime now)
	{
		var d = PlaneDistance(myPos, _seatMoveLastPos);
		if (d >= 0.12f)
		{
			_seatMoveLastPos = myPos;
			_seatMoveLastAt = now;
			return false;
		}
		if ((now - _seatMoveLastAt).TotalSeconds >= 0.5)
		{
			// 卡住换侧(坐流程走位同规则):只允许换一次
			if (_core.ObstaclesInCurrentRoom().Count > 0 && !_avoidFlipped)
			{
				_avoidSide = _avoidSide >= 0 ? -1 : 1;
				_avoidFlipped = true;
				_avoidWaypoints.Clear();
				_seatMoveLastPos = myPos;
				_seatMoveLastAt = now;
				_log.Information("避障(坐): 当前侧走不通,换另一侧绕行一次");
				return false;
			}
			Finish(MoveOutcome.Failed, "走去座位被卡住(0.5 秒未移动)");
			return true;
		}
		return false;
	}

	/// <summary>障碍避让(多跳):逐段检测 起点→终点 是否穿过任一(扩边)障碍矩形;
	/// 挡了就沿"起点→障碍中心"反方向推算出边界外的绕出点作为中间路点,继续检测 绕出点→终点…最多 6 跳。
	/// 返回绕行路点(终点前的中间点);走完返回终点。</summary>
	private System.Numerics.Vector3 ComputePath(System.Numerics.Vector3 start, System.Numerics.Vector3 goal, Queue<System.Numerics.Vector3> waypoints)
	{
		// 目标移动过远 → 丢弃旧路点重新排
		if (waypoints.Count > 0 && PlaneDistance(goal, _routeGoal) > 0.6f) waypoints.Clear();
		if (waypoints.Count > 0 && PlaneDistance(start, waypoints.Peek()) <= 0.4f) waypoints.Dequeue();
		if (waypoints.Count == 0)
		{
			_routeGoal = goal;
			var cur = start;
			for (int hop = 0; hop < 6; hop++)
			{
				if (!FindBlocking(cur, goal, out var o)) break;
				var pts = SideArc(cur, goal, o);
				var player = _objectTable.LocalPlayer;
				var posS = player != null ? $"({player.Position.X:F2},{player.Position.Z:F2})" : "?";
				if (pts == null)
				{
					_log.Information($"避障: 撞到 {o.Label()} 但绕点全在脚下,放弃 | 我{posS} cur{cur.ToString("F2")}→goal{goal.ToString("F2")} 障碍X{o.MinX:F1}~{o.MaxX:F1} Z{o.MinZ:F1}~{o.MaxZ:F1}");
					break;
				}
				foreach (var p in pts) waypoints.Enqueue(p);
				_log.Information($"避障: 撞到 {o.Label()} (X{o.MinX:F1}~{o.MaxX:F1} Z{o.MinZ:F1}~{o.MaxZ:F1}) 我{posS} cur{cur.ToString("F2")}→goal{goal.ToString("F2")} 绕行: {string.Join(" → ", pts.Select(p => p.ToString("F1")))}");
				cur = pts[^1];
			}
			if (waypoints.Count == 0) _log.Verbose("避障: 起点到终点无阻挡");
		}
		return waypoints.Count > 0 ? waypoints.Peek() : goal;
	}

	/// <summary>当前房间(扩边 margin=0.4)中,线段 a→b 撞到的第一个障碍(按线段进入顺序取 t 最小者)。</summary>
	private bool FindBlocking(System.Numerics.Vector3 a, System.Numerics.Vector3 b, out AuraCanAI.Dalamud.ObstacleRect hit)
	{
		hit = null!;
		const float margin = 0.4f;
		float bestT = 2f;
		foreach (var o in _core.ObstaclesInCurrentRoom())
		{
			var minX = o.MinX - margin; var maxX = o.MaxX + margin;
			var minZ = o.MinZ - margin; var maxZ = o.MaxZ + margin;
			float tmin = 0, tmax = 1;
			if (!Slab(a.X - b.X == 0 ? 0f : b.X - a.X, a.X, minX, maxX, ref tmin, ref tmax)) continue;
			if (!Slab(a.Z - b.Z == 0 ? 0f : b.Z - a.Z, a.Z, minZ, maxZ, ref tmin, ref tmax)) continue;
			if (tmax <= 0 || tmin >= 1) continue;
			if (tmin < bestT) { bestT = tmin; hit = o; }
		}
		return hit != null;
	}

	/// <summary>侧边绕行弧线(不贴边,外侧半米左右自然绕):挑"起点更靠近的那一侧",在矩形外侧 clear 处定义弧点——先平移到该侧通道,再沿通道升到障碍后方。起点已贴得很近(无法平移)返回 null 表示绕不出。</summary>
	private System.Collections.Generic.List<System.Numerics.Vector3>? SideArc(System.Numerics.Vector3 a, System.Numerics.Vector3 goal, AuraCanAI.Dalamud.ObstacleRect o)
	{
		const float clear = 0.55f; // 外侧余量:不贴边,半米左右弧线
		// 左右侧:挑起点更近那侧(换侧时用 _avoidSide 强制)
		var nearSide = MathF.Abs(a.X - o.MinX) <= MathF.Abs(a.X - o.MaxX) ? -1 : 1;
		var sign = _avoidSide != 0 ? _avoidSide : nearSide;
		var sideX = sign < 0 ? o.MinX - clear : o.MaxX + clear;
		// 上下绕向:目标在障碍下方则从下边绕,在上方则从上边绕,在侧面则挑较近一边
		float zExit;
		if (goal.Z < o.MinZ) zExit = o.MinZ - clear;      // 目标在下方 → 从下边过
		else if (goal.Z > o.MaxZ) zExit = o.MaxZ + clear; // 目标在上方 → 从上边过
		else zExit = (goal.Z - o.MinZ < o.MaxZ - goal.Z) ? o.MinZ - clear : o.MaxZ + clear;
		var onChannel = MathF.Abs(a.X - sideX) < 0.2f;
		var list = new System.Collections.Generic.List<System.Numerics.Vector3>();
		if (!onChannel)
		{
			// 先平移到侧向通道(保持当前 Z,不倒走)
			var p1 = new System.Numerics.Vector3(sideX, a.Y, a.Z);
			if (PlaneDistance(a, p1) >= 0.25f) list.Add(p1);
		}
		// 沿通道走到"障碍的下沿/上沿外侧",再横着进目标
		var p2 = new System.Numerics.Vector3(sideX, a.Y, zExit);
		if (PlaneDistance(a, p2) >= 0.25f) list.Add(p2);
		return list.Count > 0 ? list : null; // 路点全在脚下 → 绕不出
	}
	private static bool Slab(float dir, float origin, float lo, float hi, ref float tmin, ref float tmax)
	{
		if (MathF.Abs(dir) < 1e-6f) return origin >= lo && origin <= hi; // 平行且不在带内 = 不交
		var t1 = (lo - origin) / dir; var t2 = (hi - origin) / dir;
		if (t1 > t2) (t1, t2) = (t2, t1);
		tmin = MathF.Max(tmin, t1);
		tmax = MathF.Min(tmax, t2);
		return tmin <= tmax;
	}

	/// <summary>坐流程:走(到座前参考点)→ 稍停 → 触发坐法(宏或指令)→ 落座确认 → SatDown 结束。</summary>
	private void UpdateSeatFlow(System.Numerics.Vector3 myPos, DateTime now)
	{
		if (_sitSeat == null)
		{
			Finish(MoveOutcome.Failed, "座位信息缺失");
			return;
		}
		if ((now - _startedAt).TotalSeconds > _config.MovementMaxDurationSec)
		{
			Finish(MoveOutcome.Failed, "去坐超时");
			return;
		}
		switch (_sitPhase)
		{
			case SitPhase.Walk:
			{
				if (SeatMoveStuck(myPos, now)) return; // 走路 0.5s 没动 → 卡住(Finish 在内部已处理)
				var d = PlaneDistance(myPos, _sitApproach);
				if (d <= _config.SeatArriveTolerance)
				{
					_sitPhase = SitPhase.Pause;
					_sitStageAt = now;
					_override.Active = false;
				}
				else
				{
					_override.DesiredPosition = ComputePath(myPos, _sitApproach, _avoidWaypoints);
					_override.Active = true;
				}
				break;
			}
			case SitPhase.Pause:
				_override.Active = false;
				if ((now - _sitStageAt).TotalSeconds >= 0.6)
				{
					_sitPhase = SitPhase.TriggerSit;
					_sitStageAt = now;
				}
				break;
			case SitPhase.TriggerSit:
				_override.Active = false;
				_sitPhase = SitPhase.Confirm;
				_sitStageAt = now;
				var ok = _core.ExecuteSitMethod();
				_log.Information(ok ? "坐: 坐法已触发" : "坐: 坐法未成功触发(检查配置的宏/指令)");
				break;
			case SitPhase.Confirm:
				_override.Active = false;
				// 等坐姿生效后,校验是否坐正(与记录点平面距离 ≤0.35m);歪了且未满 3 次 → 起立挪位重试
				if ((now - _sitStageAt).TotalSeconds >= 1.4)
				{
					var dSeat = PlaneDistance(myPos, new System.Numerics.Vector3(_sitSeat.X, myPos.Y, _sitSeat.Z));
					if (dSeat <= SitCheckTolerance)
					{
						_log.Information($"坐: 第{_sitAttempts}次坐正(与记录点偏差 {dSeat:F2}m)");
						Finish(MoveOutcome.SatDown, $"座位 {_sitSeat.Label()}");
					}
					else if (_sitAttempts >= MaxSitAttempts)
					{
						_log.Warning($"坐: 尝试 {_sitAttempts} 次仍未坐正(偏差 {dSeat:F2}m),停下不动");
						Finish(MoveOutcome.Failed, "多次尝试没坐正,已停下(建议 /aca seatstand 校准或手调)");
					}
					else
					{
						// 坐歪 → 站起来(发一次坐法指令 /sit 站起),然后朝目标方向直走 2 米再重新坐下
						_sitAttempts++;
						_standStartPos = myPos;
						_core.ExecuteSitMethod(); // /sit 再触发一次 = 站起
						_sitPhase = SitPhase.StandUp;
						_sitStageAt = now;
						_log.Information($"坐: 第{_sitAttempts - 1}次坐歪(偏差 {dSeat:F2}m),站起后朝目标走 2 米重试");
					}
				}
				break;
			case SitPhase.StandUp:
			{
				if (SeatMoveStuck(myPos, now)) return; // 重试走路 0.5s 没动 → 卡住
				// 从坐歪的位置站起来(上面已 /sit 站起),朝"目标方向"直走 1 米再坐下
				var anchor = new System.Numerics.Vector3(_sitSeat.X, myPos.Y, _sitSeat.Z);
				var to = anchor - _standStartPos;
				if (MathF.Sqrt(to.X * to.X + to.Z * to.Z) < 1e-3f)
				{
					// 站起点与目标几乎重合(如正坐在目标点上):改沿坐姿朝向先走开 1 米
					var f = _sitSeat.Yaw;
					to = new System.Numerics.Vector3(MathF.Sin(f), 0, MathF.Cos(f));
				}
				to.Y = 0;
				var len = MathF.Sqrt(to.X * to.X + to.Z * to.Z);
				if (len > 1e-3f) { to.X /= len; to.Z /= len; }
				var goal = _standStartPos + new System.Numerics.Vector3(to.X * 1f, myPos.Y, to.Z * 1f);
				var dGoal = PlaneDistance(myPos, goal);
				if (dGoal <= 0.35f || (now - _sitStageAt).TotalSeconds >= 2.5f)
				{
					_sitPhase = SitPhase.Pause; // 走完 1 米 → 先站定 0.6s 再 /sit(避免刚停就坐空)
					_sitStageAt = now;
					_override.Active = false;
				}
				else
				{
					_override.DesiredPosition = ComputePath(myPos, goal, _avoidWaypoints);
					_override.Active = true;
				}
				break;
			}
		}
	}

	/// <summary>到达社交距离:停止移动;需要时选中目标(角色站定后自动转向面向对方)</summary>
	private void ArriveAtSocialDistance()
	{
		_override.Active = false;
		if (_faceOnArrive)
		{
			var ok = _core.TryLook(_targetName); // 站定时选中目标 → 角色自动转向面向
			if (!ok) _log.Verbose("移动: 到达后选中目标失败(可能已离开)");
		}
		Finish(MoveOutcome.Arrived, "已到社交距离");
	}

	private void Finish(MoveOutcome outcome, string detail)
	{
		if (_finishedNotified) return; // 防重入
		_finishedNotified = true;
		_active = false;
		_override.Active = false;
		_override.DesiredPosition = _objectTable.LocalPlayer?.Position ?? default;
		// 移动结束:若本次强切过走路,恢复原状态(玩家手动状态保留)
		if (_walkForced)
		{
			_walkForced = false;
			_core.TrySetWalking(false);
		}
		var r = new MoveResult { Outcome = outcome, TargetName = _targetName, Detail = detail };
		_log.Information($"移动结束: {r}");
		try { MovementFinished?.Invoke(r); } catch (Exception e) { _log.Error($"移动结束事件异常: {e}"); }
	}

	/// <summary>当前是否允许移动(登录 + 非过场/演奏/骑乘/加载等)。返回 null = 允许,否则返回阻断原因。</summary>
	private string? GetBlockReason()
	{
		if (!_clientState.IsLoggedIn) return "未登录";
		if (!_config.MovementEnabled) return "移动总开关已关";
		try
		{
			if (_condition[ConditionFlag.WatchingCutscene] || _condition[ConditionFlag.OccupiedInCutSceneEvent]) return "过场动画中";
			if (_condition[ConditionFlag.BetweenAreas]) return "区域切换中";
			if (_condition[ConditionFlag.Performing]) return "演奏中";
			if (_condition[ConditionFlag.Mounted]) return "骑乘中";
			if (_condition[ConditionFlag.InFlight]) return "飞行中(自动移动仅步行,不上天)";
			if (_condition[ConditionFlag.Diving]) return "潜水/游泳中";
			if (!_config.MovementAllowInCombat && _condition[ConditionFlag.InCombat]) return "战斗中";
			if (_condition[ConditionFlag.OccupiedInQuestEvent]) return "任务事件中";
			if (_condition[ConditionFlag.BetweenAreas]) return "切换区域";
			if (_condition[ConditionFlag.DutyRecorderPlayback]) return "回放中";
		}
		catch { /* 条件服务异常按允许处理(仅影响移动时机) */ }
		return null;
	}

	private static float PlaneDistance(Vector3 a, Vector3 b)
	{
		var dx = a.X - b.X;
		var dz = a.Z - b.Z;
		return MathF.Sqrt(dx * dx + dz * dz);
	}

	private static Vector3 PlaneNormalized(Vector3 v)
	{
		var len = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
		if (len < 1e-4f) return Vector3.Zero;
		return new Vector3(v.X / len, 0, v.Z / len);
	}
}
