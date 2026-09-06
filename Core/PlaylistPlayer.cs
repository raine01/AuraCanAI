namespace AuraCanAI.Dalamud.Core;

/// <summary>播放模式:单曲 / 单曲循环 / 列表顺序 / 随机</summary>
public enum PlaylistMode
{
	Single, // 只播当前一首,播完停
	Loop, // 单曲循环
	List, // 列表顺序,播完整个列表停
	Random, // 随机播放
}

/// <summary>
/// 歌单播放引擎:主播放器(自己窗口)+ 辅播放器(辅端窗口,合奏曲目用)。
/// 支持单曲/循环/列表/随机四种模式、自动下一首、暂停/继续/停止、上一首/下一首。
/// </summary>
public class PlaylistPlayer : IDisposable
{
	private readonly AuraCanAiCore _core;
	private readonly Random _rng = new();
	private List<PlaylistItem> _queue = new(); // 当前队列快照
	private int _index = -1; // 当前曲目(队列下标)
	private PlaylistMode _mode = PlaylistMode.List;
	private bool _paused;
	private List<int> _randomOrder = new(); // 随机顺序
	private int _randomPos;

	/// <summary>主播放器(注入自己进程窗口)</summary>
	public readonly Midi.MidiPlayer Main = new();

	/// <summary>辅播放器(合奏曲目:注入辅端窗口)</summary>
	public readonly Midi.MidiPlayer Slave = new();

	/// <summary>状态变化(前端轮询 status 即可,此事件供卫月端 UI 刷新)</summary>
	public event Action? StateChanged;

	public PlaylistPlayer(AuraCanAiCore core)
	{
		_core = core;
		Main.Finished += OnMainFinished;
	}

	public bool IsPlaying => Main.IsPlaying;
	public bool IsPaused => _paused && Main.IsPlaying == false && _index >= 0 && _queue.Count > 0;
	public PlaylistMode Mode { get => _mode; set => _mode = value; }
	public int Index => _index;
	public IReadOnlyList<PlaylistItem> Queue => _queue;
	public string CurrentFileName => _index >= 0 && _index < _queue.Count ? _queue[_index].FileName : "";
	public string CurrentPath => _index >= 0 && _index < _queue.Count ? _queue[_index].Path : "";

	/// <summary>加载队列并播放(从 startIndex;index&lt;0 表示只加载不播)。</summary>
	public void PlayQueue(List<PlaylistItem> items, int startIndex)
	{
		Stop();
		_queue = items.ToList();
		BuildRandomOrder();
		if (_queue.Count == 0) { _index = -1; StateChanged?.Invoke(); return; }
		_index = startIndex >= 0 && startIndex < _queue.Count ? startIndex : 0;
		_paused = false;
		PlayCurrent();
	}

	/// <summary>单曲播放(独立于队列;清空队列只播这首)。</summary>
	public void PlaySingle(PlaylistItem item)
	{
		_queue = new List<PlaylistItem> { item };
		_index = 0;
		_paused = false;
		BuildRandomOrder();
		PlayCurrent();
	}

	private void BuildRandomOrder()
	{
		_randomOrder = Enumerable.Range(0, _queue.Count).ToList();
		for (int i = _randomOrder.Count - 1; i > 0; i--)
		{
			var j = _rng.Next(i + 1);
			(_randomOrder[i], _randomOrder[j]) = (_randomOrder[j], _randomOrder[i]);
		}
		_randomPos = 0;
	}

	/// <summary>播放当前队列项(主轨自己,合奏曲目加辅轨注入辅端窗口)。</summary>
	private void PlayCurrent()
	{
		if (_index < 0 || _index >= _queue.Count) return;
		var item = _queue[_index];
		var err = Main.Load(item.Path);
		if (err != null) { OnPlayError($"主播放器加载失败:{err}"); return; }
		ApplyPreTrack(Main, item); // 预处理开关须在选轨前(SelectTrack 重建 Playback 时读取)
		if (item.MainTrack == -2) Main.SelectAllTracks(); // 单人整曲:全部轨道合并
		else Main.SelectTrack(item.MainTrack >= 0 && item.MainTrack < Main.TrackCount ? item.MainTrack : 0);
		ApplyPostTrack(Main, item, false);
		// 合奏曲目:辅播放器选辅轨,注入辅端窗口
		if (item.Ensemble)
		{
			var slaveHwnd = _core.GetOtherFfxivWindowHandle();
			Plugin.Log?.Information($"合奏: 曲目={item.FileName} 辅端窗口=0x{slaveHwnd.ToInt64():X}");
			if (slaveHwnd != IntPtr.Zero)
			{
				var slaveErr = Slave.Load(item.Path);
				if (slaveErr == null)
				{
					if (Slave.TrackCount <= 1)
					{
						// 仅一条轨道:无合奏意义,辅端不演奏(主轨照常)
						OnPlayError("合奏曲目仅一条轨道,辅端不演奏(主轨照常)");
					}
					else
					{
						var slaveTrack = item.SlaveTrack >= 0 && item.SlaveTrack < Slave.TrackCount
							? item.SlaveTrack
							: (Slave.TrackCount > 1 ? 1 : 0);
						ApplyPreTrack(Slave, item); // 预处理开关先设(SelectTrack 重建时生效)
						Slave.SelectTrack(slaveTrack);
						ApplyPostTrack(Slave, item, true);
						Slave.TargetHwnd = slaveHwnd;
						var ok = Slave.Play();
						Plugin.Log?.Information($"合奏: 辅端选轨={slaveTrack}/{Slave.TrackCount} Play()={ok} 目标HWND=0x{slaveHwnd.ToInt64():X}");
					}
				}
				else
				{
					OnPlayError($"辅播放器加载失败:{slaveErr}");
				}
			}
			else
			{
				OnPlayError("合奏曲目:未找到辅端游戏窗口(需双开)");
			}
		}
		Main.Play();
		_paused = false;
		StateChanged?.Invoke();
	}

	private static void ApplyPreTrack(Midi.MidiPlayer player, PlaylistItem item)
	{
		// 预处理开关在 SelectTrack 时被读取(重建 Playback),必须选轨前设置
		player.UseAnalysis = item.Analysis;
		player.UseChord = item.Chord;
	}

	private static void ApplyPostTrack(Midi.MidiPlayer player, PlaylistItem item, bool isSlave)
	{
		// 运行时参数:Speed 作用于已建好的 Playback,须选轨后设置;Transpose 注入时读取,顺序无关
		player.Speed = item.Speed <= 0 ? 1.0 : item.Speed;
		player.Transpose = item.Transpose;
	}

	private void OnPlayError(string msg)
	{
		LastError = msg;
		Plugin.Log?.Warning($"歌单播放: {msg}");
		StateChanged?.Invoke();
	}

	/// <summary>最近一次播放错误(前端提示用)</summary>
	public string LastError = "";

	// ==================== 控制 ====================

	public void Pause()
	{
		if (_index < 0) return;
		Main.Pause();
		if (CurrentEnsemble) Slave.Pause();
		_paused = true;
		StateChanged?.Invoke();
	}

	/// <summary>跳转到指定位置(ms,仅前端进度条拖拽用);播放中/暂停均可。</summary>
	public void Seek(double ms)
	{
		if (_index < 0) return;
		Main.Seek(ms);
		if (CurrentEnsemble) Slave.Seek(ms);
		StateChanged?.Invoke();
	}

	public void Resume()
	{
		if (_index < 0) return;
		Main.Resume();
		if (CurrentEnsemble) Slave.Resume();
		_paused = false;
		StateChanged?.Invoke();
	}

	public void Stop()
	{
		Main.Stop();
		Slave.Stop();
		_paused = false;
		StateChanged?.Invoke();
	}

	public void Next() => Step(1);

	public void Prev() => Step(-1);

	private bool CurrentEnsemble => _index >= 0 && _index < _queue.Count && _queue[_index].Ensemble;

	private void Step(int delta)
	{
		if (_queue.Count == 0) return;
		switch (_mode)
		{
			case PlaylistMode.Single:
				break; // 单曲模式不切歌
			case PlaylistMode.Loop:
				if (_index >= 0) { PlayCurrent(); } // 单曲循环:重播当前
				break;
			case PlaylistMode.Random:
				AdvanceRandom(delta);
				break;
			default:
				AdvanceLinear(delta);
				break;
		}
	}

	private void AdvanceLinear(int delta)
	{
		var next = _index + delta;
		if (next < 0) next = _queue.Count - 1;
		else if (next >= _queue.Count) next = 0;
		_index = next;
		_paused = false;
		PlayCurrent();
	}

	private void AdvanceRandom(int delta)
	{
		if (_randomOrder.Count == 0) return;
		_randomPos += delta;
		if (_randomPos < 0) _randomPos = _randomOrder.Count - 1;
		else if (_randomPos >= _randomOrder.Count) _randomPos = 0;
		_index = _randomOrder[_randomPos];
		_paused = false;
		PlayCurrent();
	}

	private void OnMainFinished()
	{
		// 播放完成(内部线程):按模式推进;单曲/列表播完则停
		switch (_mode)
		{
			case PlaylistMode.Loop:
				PlayCurrent(); // 单曲循环:重播当前
				break;
			case PlaylistMode.Single:
				break; // 播完停
			case PlaylistMode.Random:
				_randomPos++;
				if (_randomPos >= _randomOrder.Count) BuildRandomOrder(); // 一轮播完重新洗牌
				_index = _randomOrder[_randomPos];
				PlayCurrent();
				break;
			default: // List
				if (_index + 1 < _queue.Count)
				{
					_index++;
					PlayCurrent();
				}
				// 播完整个列表,停
				break;
		}
	}

	/// <summary>播放状态快照(前端/UI 用)</summary>
	public object GetStatusJson()
	{
		var (cur, total) = Main.GetProgress();
		var (isBard, isPerforming, hint) = _core.GetPerformStatus();
		return new
		{
			playing = IsPlaying,
			paused = IsPaused,
			index = _index,
			count = _queue.Count,
			mode = _mode.ToString(),
			file = CurrentFileName,
			path = CurrentPath,
			progressMs = cur,
			totalMs = total,
			error = LastError,
			slaveActive = CurrentEnsemble,
			perform = new { isBard, isPerforming, hint },
		};
	}

	public void Dispose()
	{
		Main.Dispose();
		Slave.Dispose();
	}
}
