using System.Speech.Synthesis;
using System.Collections.Concurrent;

namespace AuraCanAI.Dalamud.Core;

/// <summary>
/// TTS 语音播报(Windows SAPI,免费离线稳定)。
/// 并行播报池:默认 5 个 worker,每个 worker 持有一个独立的 SpeechSynthesizer(SAPI 实例线程不安全,不能共享)。
/// 适用场景:多人同时说话(RP 聊天)时多条播报并行出声,避免单线程串行导致积压很久播不完。
/// ⚠️ 并行播报 = 多路语音同时输出到同一音频设备(混音),条数越多越嘈杂;线程数可在设置面板调 1~8。
/// 队列上限 MaxQueue:满则丢最旧,保证最新消息不被积压拖死。
/// </summary>
public class TtsService : IDisposable
{
	private const int MaxQueue = 100; // 队列上限:超出丢最旧(防无限积压,保证最新消息能及时播)
	public const int MinWorkers = 1;
	public const int MaxWorkers = 8;

	private readonly ConcurrentQueue<string> _queue = new();
	private readonly object _sync = new();
	private readonly List<Worker> _workers = new(); // 常驻 worker 池(每个持有一个 SAPI 实例)
	private volatile bool _running;
	private volatile int _targetWorkers = 5;
	private string? _preferredVoice; // 中文语音名(所有实例共用,按名 SelectVoice 线程安全)

	public volatile bool Enabled = true;
	public volatile int Volume = 100;
	public volatile int Rate = 0;

	/// <summary>单个播报 worker:一个 SAPI 实例 + 队列消费循环</summary>
	private sealed class Worker
	{
		public int Id;
		public SpeechSynthesizer Synth = null!;
	}

	public TtsService(int workers = 5)
	{
		_targetWorkers = Math.Clamp(workers, MinWorkers, MaxWorkers);
		try
		{
			// 探测一次中文语音(临时实例,用完释放;各 worker 实例按名字 SelectVoice)
			using (var probe = new SpeechSynthesizer())
			{
				_preferredVoice = probe.GetInstalledVoices()
					.Select(v => v.VoiceInfo)
					.FirstOrDefault(vi => vi.Culture?.Name.StartsWith("zh") == true)?.Name
					?? probe.GetInstalledVoices().FirstOrDefault()?.VoiceInfo?.Name;
			}
			_running = true;
			EnsureWorkers();
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"TTS 初始化失败: {e.Message}");
		}
	}

	/// <summary>入队播报(线程安全)。队列满(MaxQueue)时丢最旧,保证新消息及时播。</summary>
	public void Speak(string text)
	{
		if (!Enabled || string.IsNullOrWhiteSpace(text)) return;
		while (_queue.Count >= MaxQueue) _queue.TryDequeue(out _); // 满则丢最旧
		_queue.Enqueue(text);
	}

	/// <summary>立即播报并清空排队中的消息(用于调试/手动)。正在播放的那句不打断,播完即停。</summary>
	public void SpeakNow(string text)
	{
		if (!Enabled || string.IsNullOrWhiteSpace(text)) return;
		while (_queue.TryDequeue(out _)) { }
		_queue.Enqueue(text);
	}

	/// <summary>调整并行播报线程数(1~8,运行时生效:多余的 worker 播完当前句后退出,不足的立即补充)。</summary>
	public void SetWorkers(int n)
	{
		n = Math.Clamp(n, MinWorkers, MaxWorkers);
		if (n == _targetWorkers) return;
		_targetWorkers = n;
		EnsureWorkers();
	}

	/// <summary>补足 worker(多余的 worker 在 WorkerLoop 里自查 id >= _targetWorkers 自然退出)</summary>
	private void EnsureWorkers()
	{
		lock (_sync)
		{
			while (_workers.Count < _targetWorkers)
			{
				var synth = CreateSynth();
				if (synth == null) break; // 引擎创建失败,不再补
				var w = new Worker { Id = _workers.Count, Synth = synth };
				_workers.Add(w);
				_ = Task.Run(() => WorkerLoop(w));
			}
		}
	}

	/// <summary>新建一个 SAPI 实例并选中中文语音</summary>
	private SpeechSynthesizer? CreateSynth()
	{
		try
		{
			var s = new SpeechSynthesizer();
			if (!string.IsNullOrEmpty(_preferredVoice)) s.SelectVoice(_preferredVoice);
			return s;
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"TTS 语音引擎创建失败: {e.Message}");
			return null;
		}
	}

	/// <summary>worker 主循环:取队列 → 播报(同步等待完成)→ 空则短暂休眠;线程数调小时自查退出并释放实例</summary>
	private void WorkerLoop(Worker w)
	{
		while (_running)
		{
			if (w.Id >= _targetWorkers)
			{
				// 线程数调小:本 worker 退出
				lock (_sync) _workers.Remove(w);
				DisposeSynth(w);
				return;
			}
			if (_queue.TryDequeue(out var text))
			{
				try { PlayOne(w.Synth, text); }
				catch (Exception e) { Plugin.Log?.Error($"TTS 播放异常: {e.Message}"); }
			}
			else
			{
				Thread.Sleep(50); // 空队列等待
			}
		}
		lock (_sync) _workers.Remove(w);
		DisposeSynth(w);
	}

	/// <summary>播报一条文本:应用音量/语速/语音,异步开播后同步等待完成(60 秒超时兜底,防卡死)。</summary>
	private void PlayOne(SpeechSynthesizer synth, string text)
	{
		var tcs = new TaskCompletionSource();
		synth.Volume = Math.Clamp(Volume, 0, 100);
		synth.Rate = Math.Clamp(Rate, -10, 10);
		if (!string.IsNullOrEmpty(_preferredVoice)) synth.SelectVoice(_preferredVoice);
		EventHandler<SpeakCompletedEventArgs> handler = null!;
		handler = (_, _) => { tcs.TrySetResult(); synth.SpeakCompleted -= handler; };
		synth.SpeakCompleted += handler;
		try
		{
			synth.SpeakAsync(text);
			tcs.Task.Wait(TimeSpan.FromSeconds(60));
		}
		finally
		{
			synth.SpeakCompleted -= handler; // 兜底卸载,防事件累积
		}
	}

	/// <summary>释放单个实例(容错:重复释放/已释放时静默)</summary>
	private static void DisposeSynth(Worker w)
	{
		try { w.Synth.SpeakAsyncCancelAll(); } catch { }
		try { w.Synth.Dispose(); } catch { }
	}

	public void Dispose()
	{
		_running = false;
		lock (_sync)
		{
			foreach (var w in _workers) DisposeSynth(w);
			_workers.Clear();
		}
	}
}
