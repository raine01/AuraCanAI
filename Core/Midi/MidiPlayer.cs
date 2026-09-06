using System.Diagnostics;
using System.Runtime.InteropServices;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using AuraCanAI.Dalamud;

namespace AuraCanAI.Dalamud.Core.Midi;

/// <summary>
/// MIDI 演奏核心(阶段 1):读取 MIDI → 列出非空轨道 → 选轨 → Playback 播放 →
/// EventPlayed(NoteOn/NoteOff)→ PostMessage 注入按键到游戏主窗口(进程内,后台亦可)。
/// 参考 Daigassou 架构(其代码 GPL 仅借鉴思路,本实现全部自行编写)。
/// 注意:Playback 事件在内部线程触发,PostMessage 线程安全,无需切框架线程。
/// </summary>
public sealed class MidiPlayer : IDisposable
{
	private const uint WmKeydown = 0x0100;
	private const uint WmKeyup = 0x0101;

	[DllImport("user32.dll", EntryPoint = "PostMessage")]
	private static extern bool PostMessage(IntPtr hWnd, uint msg, uint wParam, uint lParam);

	// DryWetMidi 的 Playback 高精度时钟 P/Invoke 原生库;.NET 不会自动搜插件目录,须按绝对路径预加载
	static MidiPlayer()
	{
		try
		{
			var nativeName = Environment.Is64BitProcess ? "Melanchall_DryWetMidi_Native64.dll" : "Melanchall_DryWetMidi_Native32.dll";
			var candidates = new List<string?>
			{
				// 卫月官方插件路径(最可靠)
				Try(() => Path.GetDirectoryName(Plugin.PluginInterface.AssemblyLocation.FullName)),
				// 程序集 Location(某些加载方式下可能为空)
				Try(() => Path.GetDirectoryName(typeof(MidiPlayer).Assembly.Location)),
			};
			foreach (var dir in candidates.Where(d => !string.IsNullOrEmpty(d)).Distinct())
			{
				var native = Path.Combine(dir!, nativeName);
				if (!File.Exists(native)) continue;
				NativeLibrary.Load(native);
				Plugin.Log?.Information($"已预加载 DryWetMidi 原生库: {native}");
				return;
			}
			Plugin.Log?.Warning($"未找到 DryWetMidi 原生库 {nativeName}(候选目录: {string.Join(" / ", candidates.Where(c => !string.IsNullOrEmpty(c))) })");
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"预加载 DryWetMidi 原生库失败: {e.Message}");
		}
	}

	private static string? Try(Func<string?> f) { try { return f(); } catch { return null; } }

	private readonly object _lock = new();
	private MidiFile? _midi;
	private TempoMap _tempoMap = TempoMap.Default;
	private readonly List<TrackChunk> _tracks = new(); // 非空轨道(与 TrackNames 对齐)
	private readonly List<string> _trackNames = new(); // 轨道名(取 SequenceTrackName,无则「未命名轨道」)
	private Playback? _playback;
	private int _selectedTrack = -1;
	private IntPtr _gameWindow;

	/// <summary>播放完成事件(内部线程触发;供歌单自动下一首)。</summary>
	public event Action? Finished;

	/// <summary>已加载的 MIDI 文件名(仅显示用)</summary>
	public string FileName = "";
	public bool IsLoaded => _midi != null;
	public bool IsPlaying => _playback?.IsRunning == true;
	public int SelectedTrack => _selectedTrack;
	public IReadOnlyList<string> TrackNames => _trackNames;
	public int TrackCount => _tracks.Count;
	public double Speed { get => _playback?.Speed ?? 1.0; set { if (_playback != null) _playback.Speed = value; } }

	/// <summary>加载 MIDI 文件。成功返回 null,失败返回错误提示(中文)。</summary>
	public string? Load(string path)
	{
		try
		{
			var midi = MidiFile.Read(path, new ReadingSettings
			{
				NoHeaderChunkPolicy = NoHeaderChunkPolicy.Ignore,
				NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
				InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.ReadValid,
				InvalidChunkSizePolicy = InvalidChunkSizePolicy.Ignore,
				InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
				MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Ignore,
				UnexpectedTrackChunksCountPolicy = UnexpectedTrackChunksCountPolicy.Ignore,
				ExtraTrackChunkPolicy = ExtraTrackChunkPolicy.Read,
				UnknownChunkIdPolicy = UnknownChunkIdPolicy.ReadAsUnknownChunk,
				SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOff,
				UnknownFileFormatPolicy = UnknownFileFormatPolicy.Ignore,
				InvalidSystemCommonEventParameterValuePolicy = InvalidSystemCommonEventParameterValuePolicy.SnapToLimits,
			});
			lock (_lock)
			{
				StopInternal();
				_playback?.Dispose();
				_playback = null;
				_midi = midi;
				_tempoMap = midi.GetTempoMap();
				_tracks.Clear();
				_trackNames.Clear();
				foreach (var chunk in midi.GetTrackChunks())
				{
					if (!chunk.ManageNotes().Objects.Any()) continue;
					_tracks.Add(chunk);
					_trackNames.Add(GetTrackName(chunk));
				}
				_selectedTrack = -1;
				FileName = path.Split('\\', '/').Last();
				if (_tracks.Count > 0) SelectTrack(0);
			}
			return null;
		}
		catch (Exception e)
		{
			return $"MIDI 读取失败:{e.Message}";
		}
	}

	private static string GetTrackName(TrackChunk chunk)
	{
		foreach (var ev in chunk.Events)
			if (ev is SequenceTrackNameEvent name && !string.IsNullOrEmpty(name.Text))
				return name.Text;
		return "未命名轨道";
	}

	/// <summary>选择轨道(0-based),重建 Playback(按 UseAnalysis/UseChord 开关预处理)。越界忽略。</summary>
	public void SelectTrack(int index)
	{
		lock (_lock)
		{
			if (_midi == null || index < 0 || index >= _tracks.Count) return;
			_playback?.Dispose();
			_playback = null;
			using var notes = _tracks[index].ManageNotes();
			var processed = MidiPreprocessor.Process(notes.Objects.ToList(), _tempoMap, UseAnalysis, UseChord);
			_playback = new Playback(processed, _tempoMap)
			{
				InterruptNotesOnStop = false,
			};
			_playback.EventPlayed += OnEventPlayed;
			_playback.Finished += OnFinished;
			_selectedTrack = index;
		}
	}

	/// <summary>
	/// 选择全部轨道(整曲):所有非空轨道的音符按时间合并进一个 Playback,单人演奏整首用。
	/// 合并后 SelectedTrack 返回 -1(与"未选轨"共用该值;Load 后若轨道数 &gt; 0 必已自动选过轨,故无歧义)。
	/// 合并产生的同音冲突/和弦由 MidiPreprocessor(UseAnalysis 连奏缩短 / UseChord 拆解)统一处理。
	/// </summary>
	public void SelectAllTracks()
	{
		lock (_lock)
		{
			if (_midi == null) return;
			_playback?.Dispose();
			_playback = null;
			var all = new List<Note>();
			foreach (var chunk in _tracks)
			{
				using var notes = chunk.ManageNotes();
				all.AddRange(notes.Objects); // Process 内部会 Clone,不会改原轨道
			}
			var processed = MidiPreprocessor.Process(all, _tempoMap, UseAnalysis, UseChord);
			_playback = new Playback(processed, _tempoMap)
			{
				InterruptNotesOnStop = false,
			};
			_playback.EventPlayed += OnEventPlayed;
			_playback.Finished += OnFinished;
			_selectedTrack = -1;
		}
	}

	/// <summary>开始演奏(从头)。返回是否成功。</summary>
	public bool Play()
	{
		lock (_lock)
		{
			if (_playback == null) return false;
			_playback.MoveToStart();
			_playback.Start();
			return true;
		}
	}

	/// <summary>继续演奏(DryWetMidi:Stop 后 Start 从断点继续)。返回是否成功。</summary>
	public bool Resume()
	{
		lock (_lock)
		{
			if (_playback == null || _playback.IsRunning) return false;
			_playback.Start();
			return true;
		}
	}

	/// <summary>暂停:停在当前位置并松开按键(继续时用 Resume)。</summary>
	public void Pause()
	{
		lock (_lock)
		{
			_playback?.Stop();
			ReleaseAllKeys();
		}
	}

	/// <summary>停止演奏,松开所有按键,回到开头。</summary>
	public void Stop()
	{
		lock (_lock)
		{
			StopInternal();
		}
	}

	/// <summary>跳转到指定时间(ms,相对开头),松开当前按键。播放中/暂停均可;播放中跳转会继续播,暂停中跳转保持暂停。</summary>
	public void Seek(double ms)
	{
		lock (_lock)
		{
			if (_playback == null) return;
			if (ms < 0) ms = 0;
			_playback.MoveToTime(new MetricTimeSpan(TimeSpan.FromMilliseconds(ms)));
			ReleaseAllKeys();
		}
	}

	private void StopInternal()
	{
		_playback?.Stop();
		ReleaseAllKeys();
		_playback?.MoveToStart();
	}

	private void OnFinished(object? sender, EventArgs e)
	{
		ReleaseAllKeys();
		Finished?.Invoke();
	}

	/// <summary>移调(半音,-24 ~ +24;注入时加到 NoteNumber 上,不改音符数据)</summary>
	public int Transpose { get; set; }

	/// <summary>目标窗口句柄(0 = 当前进程主窗口;合奏辅端实例设为辅端游戏窗口句柄,跨进程注入)</summary>
	public IntPtr TargetHwnd { get; set; }

	/// <summary>预处理总开关(噪声清理 + 同音连奏缩短)。默认关(FF14 特制 MIDI 已处理好,无需再处理)。</summary>
	public bool UseAnalysis { get; set; }

	/// <summary>和弦拆解开关(默认关;普通 MIDI 有同时按下多个音时可开,FF14 演奏会丢音)。</summary>
	public bool UseChord { get; set; }

	private void OnEventPlayed(object? sender, MidiEventPlayedEventArgs e)
	{
		switch (e.Event)
		{
			case NoteOnEvent n when n.Velocity > 0:
				PressPitch(ClampToPlayable(n.NoteNumber + Transpose));
				break;
			case NoteOnEvent n:
				ReleasePitch(ClampToPlayable(n.NoteNumber + Transpose));
				break;
			case NoteOffEvent n:
				ReleasePitch(ClampToPlayable(n.NoteNumber + Transpose));
				break;
		}
	}

	/// <summary>把音高搬进 FF14 可演奏范围(48~84):超出时向上/向下移八度,保持音名。</summary>
	private static int ClampToPlayable(int pitch)
	{
		if (pitch is >= 48 and <= 84) return pitch;
		if (pitch < 48) return pitch + ((48 - pitch + 11) / 12) * 12;
		return pitch - ((pitch - 84 + 11) / 12) * 12;
	}

	private void PressPitch(int pitch)
	{
		var vk = MidiKeyMap.GetVk(pitch);
		if (vk == 0) return;
		PostMessageToGame(WmKeydown, vk);
	}

	private void ReleasePitch(int pitch)
	{
		var vk = MidiKeyMap.GetVk(pitch);
		if (vk == 0) return;
		PostMessageToGame(WmKeyup, vk);
	}

	/// <summary>松开所有映射键(停止/结束时防卡键)。</summary>
	public void ReleaseAllKeys()
	{
		foreach (var vk in MidiKeyMap.PitchToVk.Values)
			PostMessageToGame(WmKeyup, vk);
	}

	private void PostMessageToGame(uint msg, int vk)
	{
		var hwnd = TargetHwnd != IntPtr.Zero ? TargetHwnd : EnsureGameWindow();
		if (hwnd == IntPtr.Zero) return;
		PostMessage(hwnd, msg, (uint)vk, 0);
	}

	private IntPtr EnsureGameWindow()
	{
		if (_gameWindow != IntPtr.Zero) return _gameWindow;
		try { _gameWindow = Process.GetCurrentProcess().MainWindowHandle; } catch { /* 忽略 */ }
		return _gameWindow;
	}

	/// <summary>取指定轨道的音符列表(预处理后):(timeMs, pitch, durationMs)。用于推送给合奏辅端。</summary>
	public List<(double timeMs, int pitch, double durationMs)> GetTrackNotes(int trackIndex)
	{
		lock (_lock)
		{
			if (_midi == null || trackIndex < 0 || trackIndex >= _tracks.Count) return new();
			using var notes = _tracks[trackIndex].ManageNotes();
			var processed = MidiPreprocessor.Process(notes.Objects.ToList(), _tempoMap, UseAnalysis, UseChord);
			return processed
				.Select(n => (n.TimeAs<MetricTimeSpan>(_tempoMap).TotalMilliseconds, (int)n.NoteNumber, n.LengthAs<MetricTimeSpan>(_tempoMap).TotalMilliseconds))
				.ToList();
		}
	}

	/// <summary>当前播放进度(毫秒)/总时长(毫秒)。未播放返回 0/0。</summary>
	public (long current, long total) GetProgress()
	{
		if (_playback == null) return (0, 0);
		var cur = (long)((MetricTimeSpan)_playback.GetCurrentTime(TimeSpanType.Metric)).TotalMilliseconds;
		var tot = (long)((MetricTimeSpan)_playback.GetDuration(TimeSpanType.Metric)).TotalMilliseconds;
		return (cur, tot);
	}

	public void Dispose()
	{
		lock (_lock)
		{
			StopInternal();
			_playback?.Dispose();
			_playback = null;
			_midi = null;
		}
	}
}
