using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace AuraCanAI.Dalamud.Core.Midi;

/// <summary>
/// MIDI 演奏预处理(纯逻辑,可单测):噪声清理 + 同音连奏缩短(总开关 useAnalysis);
/// 和弦拆解(独立开关 useChord)。处理顺序与算法参考 Daigassou 思路(其代码 GPL v2,本实现自行编写)。
/// 只操作拷贝后的音符列表,不修改原轨道数据。
/// </summary>
public static class MidiPreprocessor
{
	/// <summary>连音最小间隔(ms):同音连续按下时,前一个音提前这么多松键,避免演奏系统冲突</summary>
	public const int MinEventMs = 10;

	/// <summary>时长低于此(ms)的音符视为噪声(水果等软件产生的短音)删除</summary>
	public const int NoiseMs = 10;

	/// <summary>对音符列表做预处理(拷贝 → 按开关处理)。useAnalysis=噪声+连奏;useChord=和弦拆解。</summary>
	public static List<Note> Process(List<Note> notes, TempoMap tempoMap, bool useAnalysis, bool useChord)
	{
		var result = notes.Select(Clone).ToList();
		if (useAnalysis)
		{
			RemoveNoise(result, tempoMap);
			TrimRepeatedNotes(result, tempoMap);
		}
		if (useChord)
			BreakChords(result, tempoMap);
		return result;
	}

	private static Note Clone(Note n) => new(n.NoteNumber, n.Length, n.Time) // 注意构造参数顺序:(note, length, time)!
	{
		Channel = n.Channel,
		Velocity = n.Velocity,
		OffVelocity = n.OffVelocity,
	};

	/// <summary>删除 <NoiseMs 的杂音。</summary>
	private static void RemoveNoise(List<Note> notes, TempoMap tempoMap)
		=> notes.RemoveAll(n => n.LengthAs<MetricTimeSpan>(tempoMap).TotalMilliseconds < NoiseMs);

	/// <summary>
	/// 和弦拆解:同时开始的多个音符(同一 StartTime),按时间均匀铺开——
	/// 每个音从和弦起点开始,依次间隔 autoTick(总时长/(个数+1)),每个音时长=autoTick。
	/// (FF14 演奏系统同一时刻只能有限按键,和弦不拆会丢音)
	/// </summary>
	private static void BreakChords(List<Note> notes, TempoMap tempoMap)
	{
		var sorted = notes.OrderBy(n => n.Time).ToList();
		var i = 0;
		while (i < sorted.Count)
		{
			var start = sorted[i].Time;
			var group = new List<Note>();
			while (i < sorted.Count && sorted[i].Time == start) group.Add(sorted[i++]);
			if (group.Count <= 1) continue;

			// 和弦总时长 = 最后一个音结束 - 起点(ticks)
			var chordLen = group.Max(n => n.Time + n.Length) - start;
			var autoTick = chordLen / (group.Count + 1);
			if (autoTick <= 0) autoTick = 1;
			var count = 0;
			foreach (var note in group.OrderBy(n => n.NoteNumber))
			{
				note.Time += count * autoTick;
				note.Length = autoTick;
				count++;
			}
		}
	}

	/// <summary>同音连续按下(legato 连奏)时,把前一个音提前松键,留 MinEventMs 间隔。</summary>
	private static void TrimRepeatedNotes(List<Note> notes, TempoMap tempoMap)
	{
		var sorted = notes.OrderBy(n => n.Time).ToList();
		var lastSame = new Dictionary<SevenBitNumber, Note>();
		foreach (var n in sorted)
		{
			if (lastSame.TryGetValue(n.NoteNumber, out var prev))
			{
				var prevStartMs = prev.TimeAs<MetricTimeSpan>(tempoMap).TotalMilliseconds;
				var prevEndMs = prevStartMs + prev.LengthAs<MetricTimeSpan>(tempoMap).TotalMilliseconds;
				var nxtStartMs = n.TimeAs<MetricTimeSpan>(tempoMap).TotalMilliseconds;
				if (prevEndMs + MinEventMs > nxtStartMs)
				{
					// 前一个音缩短到 nxtStart - MinEventMs,但至少保持 MinEventMs(避免负/零长度)
					var newLenMs = Math.Max(MinEventMs, nxtStartMs - prevStartMs - MinEventMs);
					prev.Length = LengthConverter.ConvertFrom(
						new MetricTimeSpan(TimeSpan.FromMilliseconds(newLenMs)), prev.Time, tempoMap);
				}
			}
			lastSame[n.NoteNumber] = n;
		}
	}
}
