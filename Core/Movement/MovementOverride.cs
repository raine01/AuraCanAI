using System.Runtime.InteropServices;
using Dalamud.Game.Config;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AuraCanAI.Dalamud.Core.Movement;

/// <summary>
/// 角色移动输入覆盖(底层):hook 游戏 PlayerMoveController 的输入读取函数(RMIWalk / RMIFly),
/// 在"玩家自己没有按键"时把移动方向覆盖为"朝向 DesiredPosition",让角色自己朝目标走。
/// 实现思路与社区通用做法一致(签名地址同 vnavmesh/visland 等公开实现多年验证),代码自写。
///
/// 健壮性设计(全部来自社区实机教训,必须保留):
/// - 签名扫描全部 Fallible:任一签名在游戏更新后失效 → 该项降级为不可用,绝不抛异常拖垮插件;
/// - detour 内 try/catch:托管异常绝不能从 detour 逃逸回原生代码(会崩游戏);
/// - walk 覆盖依赖两个 RMIWalkIsInputEnabled(游戏判定"当前能否读输入"),任一缺失 → 整个 walk 覆盖禁用;
/// - legacy(传统移动)模式参考朝向取相机方位;相机 API 不可用时回退角色朝向(方向略错但不致命);
/// - detour 日志节流:每帧触发,最多 30 秒记一条。
/// </summary>
public sealed unsafe class MovementOverride : IDisposable
{
	// ===== 状态(均由游戏主线程访问:detour 与 Framework.Update 同线程,无需加锁) =====

	/// <summary>覆盖是否生效(控制器在"需要移动"时置 true,到达/暂停/打断时置 false)</summary>
	public bool Active { get; set; }

	/// <summary>目标世界坐标(控制器每帧更新;平面距离在 Precision 内时 detour 输出零输入 = 停)</summary>
	public System.Numerics.Vector3 DesiredPosition;

	/// <summary>到达判定半径(平面)。控制器一般在更大半径就主动停,这里只是 detour 级兜底。</summary>
	public float Precision = 0.05f;

	/// <summary>是否忽略用户输入(用户按键也继续覆盖)。一般 false:用户按键 = 人工接管,控制器应停。</summary>
	public bool IgnoreUserInput;

	/// <summary>玩家(或其他插件)最近一帧是否在按键(RMIWalk/RMIFly 读到非零输入)</summary>
	public bool UserInput;

	/// <summary>不可用原因(诊断/UI 显示);空串 = 可用</summary>
	public string UnavailableReason = "";

	/// <summary>walk 覆盖可用(RMIWalk hook + 两个 IsInputEnabled 全就绪)</summary>
	public bool WalkAvailable { get; private set; }

	/// <summary>fly 覆盖可用(RMIFly hook 就绪;地面步行不需要)</summary>
	public bool FlyAvailable { get; private set; }

	/// <summary>是否传统移动模式(设置里 MoveMode=1);影响参考朝向换算</summary>
	public bool IsLegacyMode { get; private set; }

	// ===== 原生结构 =====

	[StructLayout(LayoutKind.Explicit, Size = 0x18)]
	private struct PlayerMoveControllerFlyInput
	{
		[FieldOffset(0x0)] public float Forward;
		[FieldOffset(0x4)] public float Left;
		[FieldOffset(0x8)] public float Up;
		[FieldOffset(0xC)] public float Turn;
		[FieldOffset(0x10)] public float Unk10;
		[FieldOffset(0x14)] public byte DirMode;
		[FieldOffset(0x15)] public byte HaveBackwardOrStrafe;
	}

	[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
	private struct CameraEx
	{
		[FieldOffset(0x140)] public float DirH; // 相机水平方位角(弧度;0 = 朝北,顺时针增大)
	}

	// ===== 输入可用性判定函数(直接调用,不 hook) =====
	private delegate bool RMIWalkIsInputEnabled(void* self);

	private RMIWalkIsInputEnabled? _isInputEnabled1;
	private RMIWalkIsInputEnabled? _isInputEnabled2;

	// ===== RMIWalk(地面步行输入汇总):(self, sumLeft*, sumForward*, sumTurnLeft*, haveBackwardOrStrafe*, a6, bAdditiveUnk) =====
	private delegate void RMIWalkDelegate(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);
	// 以下两个字段由 IGameInteropProvider.InitializeFromAttributes 反射赋值(签名命中才非 null),非手写赋值 → 抑制 CS0649
#pragma warning disable CS0649
	[Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D", Fallibility = Fallibility.Fallible)]
	private Hook<RMIWalkDelegate>? _rmiWalkHook;

	// ===== RMIFly(飞行输入汇总):(self, PlayerMoveControllerFlyInput*) =====
	private delegate void RMIFlyDelegate(void* self, PlayerMoveControllerFlyInput* result);
	[Signature("E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8", Fallibility = Fallibility.Fallible)]
	private Hook<RMIFlyDelegate>? _rmiFlyHook;
#pragma warning restore CS0649

	// ===== 依赖服务 =====
	private readonly ISigScanner _sigScanner;
	private readonly IGameInteropProvider _hookProvider;
	private readonly IGameConfig _gameConfig;
	private readonly IObjectTable _objectTable;
	private readonly IPluginLog _log;

	public MovementOverride(ISigScanner sigScanner, IGameInteropProvider hookProvider, IGameConfig gameConfig,
		IObjectTable objectTable, IPluginLog log)
	{
		_sigScanner = sigScanner;
		_hookProvider = hookProvider;
		_gameConfig = gameConfig;
		_objectTable = objectTable;
		_log = log;

		var reasons = new List<string>();
		try
		{
			// 两个 IsInputEnabled 判定函数都要;任一缺失 → 放弃 walk 覆盖(否则 detour 每帧 NRE)
			if (_sigScanner.TryScanText("E8 ?? ?? ?? ?? 84 C0 75 10 38 43 3C", out var addr1) &&
				_sigScanner.TryScanText("E8 ?? ?? ?? ?? 84 C0 75 03 88 47 3F", out var addr2))
			{
				_isInputEnabled1 = Marshal.GetDelegateForFunctionPointer<RMIWalkIsInputEnabled>(addr1);
				_isInputEnabled2 = Marshal.GetDelegateForFunctionPointer<RMIWalkIsInputEnabled>(addr2);
			}
			else
			{
				reasons.Add("IsInputEnabled 签名未找到");
			}
		}
		catch (Exception e)
		{
			reasons.Add($"IsInputEnabled 扫描异常: {e.Message}");
		}

		try
		{
			_hookProvider.InitializeFromAttributes(this);
		}
		catch (Exception e)
		{
			reasons.Add($"hook 初始化异常: {e.Message}");
		}

		WalkAvailable = _rmiWalkHook != null && _isInputEnabled1 != null && _isInputEnabled2 != null;
		if (!WalkAvailable && _rmiWalkHook != null)
		{
			_rmiWalkHook.Dispose(); // 缺 IsInputEnabled 的 walk detour 没意义,释放
			_rmiWalkHook = null;
			reasons.Add("walk 覆盖不可用(缺 IsInputEnabled)");
		}
		FlyAvailable = _rmiFlyHook != null;
		if (!FlyAvailable)
		{
			reasons.Add("fly 覆盖不可用(RMIFly 签名未找到)");
		}

		// hook 常驻(不反复 Enable/Disable):detour 用 Active 开关决定是否写输入,避免状态机竞态
		try
		{
			if (_rmiWalkHook != null) _rmiWalkHook.Enable();
			if (_rmiFlyHook != null) _rmiFlyHook.Enable();
		}
		catch (Exception e)
		{
			reasons.Add($"hook 启用异常: {e.Message}");
		}

		if (!WalkAvailable && !FlyAvailable) UnavailableReason = string.Join("; ", reasons);

		try { _gameConfig.UiControlChanged += OnUiConfigChanged; } catch { }
		UpdateLegacyMode();
	}

	public void Dispose()
	{
		try { _gameConfig.UiControlChanged -= OnUiConfigChanged; } catch { }
		try { _rmiWalkHook?.Disable(); } catch { }
		try { _rmiFlyHook?.Disable(); } catch { }
		try { _rmiWalkHook?.Dispose(); } catch { }
		try { _rmiFlyHook?.Dispose(); } catch { }
	}

	private void OnUiConfigChanged(object? sender, ConfigChangeEvent evt) => UpdateLegacyMode();

	private void UpdateLegacyMode()
	{
		var legacy = false;
		try { legacy = _gameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1; } catch { }
		IsLegacyMode = legacy;
	}

	// ===== detour =====

	private long _detourErrors;
	private DateTime _lastDetourErrorLog = DateTime.MinValue;

	private void OnDetourError(Exception ex)
	{
		++_detourErrors;
		var now = DateTime.UtcNow;
		if (now - _lastDetourErrorLog < TimeSpan.FromSeconds(30)) return; // detour 每帧触发,日志必须节流
		_lastDetourErrorLog = now;
		_log.Information($"移动覆盖 detour 异常(已放行原始输入,累计 {_detourErrors} 次): {ex.Message}");
	}

	private void RMIWalkDetour(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
	{
		_rmiWalkHook!.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
		try
		{
			UserInput = *sumLeft != 0 || *sumForward != 0;
			if (!Active) return;
			// bAdditiveUnk==0 且游戏判定可读输入时才覆盖(某些状态 readInput 会跳过读输入,输出非零会出问题)
			var movementAllowed = bAdditiveUnk == 0 && _isInputEnabled1!(self) && _isInputEnabled2!(self);
			if (!movementAllowed) return;
			if (!IgnoreUserInput && (*sumLeft != 0 || *sumForward != 0)) return; // 用户在按键 → 不抢
			if (DirectionToDestination(false) is not { } relDir) return;
			var dir = ToDirection(relDir.h);
			*sumLeft = dir.X;
			*sumForward = dir.Y;
		}
		catch (Exception ex) { OnDetourError(ex); }
	}

	private void RMIFlyDetour(void* self, PlayerMoveControllerFlyInput* result)
	{
		_rmiFlyHook!.Original(self, result);
		try
		{
			UserInput = result->Forward != 0 || result->Left != 0 || result->Up != 0;
			if (!Active) return;
			if (!IgnoreUserInput && (result->Forward != 0 || result->Left != 0 || result->Up != 0)) return;
			if (DirectionToDestination(true) is not { } relDir) return;
			var dir = ToDirection(relDir.h);
			result->Forward = dir.Y;
			result->Left = dir.X;
			result->Up = relDir.v;
		}
		catch (Exception ex) { OnDetourError(ex); }
	}

	/// <summary>相对参考朝向的 (左, 前) 分量(角度 0 = 正前方;与游戏把按键换算成输入的坐标系一致)</summary>
	private static System.Numerics.Vector2 ToDirection(float relativeHeading)
		=> new(MathF.Sin(relativeHeading), MathF.Cos(relativeHeading));

	/// <summary>返回 (水平相对角, 垂直仰角);平面已在 Precision 内或玩家不存在 → null(detour 输出零输入 = 停)。</summary>
	private (float h, float v)? DirectionToDestination(bool allowVertical)
	{
		var player = _objectTable.LocalPlayer;
		if (player == null) return null;

		var dist = DesiredPosition - player.Position;
		if (dist.LengthSquared() <= Precision * Precision) return null;

		// 世界 heading:0 = 南方(+Z),逆时针增大(与玩家 Rotation 弧度约定一致)
		var heading = MathF.Atan2(dist.X, dist.Z);
		var vertical = allowVertical ? MathF.Atan2(dist.Y, MathF.Sqrt(dist.X * dist.X + dist.Z * dist.Z)) : 0f;

		var refDir = IsLegacyMode ? LegacyCameraHeading() : player.Rotation;
		return (NormalizeAngle(heading - refDir), vertical);
	}

	/// <summary>传统移动模式参考朝向 = 相机方位 + 180°(社区验证过的换算;相机 API 不可用时回退角色朝向)</summary>
	private float LegacyCameraHeading()
	{
		try
		{
			if (CameraManager.Addresses.GetActiveCamera.Value != 0)
			{
				var mgr = CameraManager.Instance();
				if (mgr != null)
				{
					var cam = (CameraEx*)mgr->GetActiveCamera();
					if (cam != null) return cam->DirH + MathF.PI;
				}
			}
		}
		catch { }
		return _objectTable.LocalPlayer?.Rotation ?? 0f;
	}

	// ===== 防挂机 =====

	/// <summary>清除输入/AFK 计时器(自动移动期间调用,防止被判定为离开/超时踢出)</summary>
	public static void ResetAfkTimers()
	{
		try
		{
			var module = UIModule.Instance()->GetInputTimerModule();
			module->AfkTimer = 0;
			module->ContentInputTimer = 0;
			module->InputTimer = 0;
		}
		catch { /* 结构体版本差异时忽略,不影响移动 */ }
	}

	/// <summary>把角度归一化到 [-π, π]</summary>
	private static float NormalizeAngle(float a)
	{
		while (a < -MathF.PI) a += 2 * MathF.PI;
		while (a > MathF.PI) a -= 2 * MathF.PI;
		return a;
	}
}
