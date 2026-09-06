namespace AuraCanAI.Dalamud.Core;

/// <summary>歌单(持久化到配置)。多个歌单互不影响。</summary>
public class Playlist
{
	/// <summary>歌单名(唯一,列表显示)</summary>
	public string Name { get; set; } = "";

	/// <summary>曲目列表(按序播放)</summary>
	public List<PlaylistItem> Items { get; set; } = new();
}

/// <summary>歌单内一首 MIDI 及演奏参数(每曲独立,不影响其他曲目)</summary>
public class PlaylistItem
{
	/// <summary>MIDI 文件路径(电脑上)</summary>
	public string Path { get; set; } = "";

	/// <summary>文件名(仅显示用)</summary>
	public string FileName { get; set; } = "";

	/// <summary>预处理(噪声清理+连奏优化),默认关(FF14 特制 MIDI)</summary>
	public bool Analysis { get; set; }

	/// <summary>和弦拆解,默认关</summary>
	public bool Chord { get; set; }

	/// <summary>速度(0.5~2.0)</summary>
	public double Speed { get; set; } = 1.0;

	/// <summary>移调(半音,-24~24)</summary>
	public int Transpose { get; set; }

	/// <summary>是否为合奏曲目(辅轨;需要双开,注入辅端窗口)</summary>
	public bool Ensemble { get; set; }

	/// <summary>主轨索引(自己弹):-2=全部轨道合并(单人整曲,默认),-1=自动取 0,&gt;=0=指定轨道</summary>
	public int MainTrack { get; set; } = -2;

	/// <summary>辅轨索引(辅端弹;-1=自动取 1,仅一轨时取 0)</summary>
	public int SlaveTrack { get; set; } = -1;
}
