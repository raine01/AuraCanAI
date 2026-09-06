namespace AuraCanAI.Dalamud.Core.Midi;

/// <summary>
/// FF14 演奏系统键位映射:音符号(pitch)→ 虚拟键码(VK)。
/// 这是游戏演奏系统的事实数据(与 Daigassou/其他演奏工具一致,非独创表达),自行维护。
/// 覆盖 pitch 48~84(FF14 演奏标准音域);吉他扩展键位(108~113)后续需要再加。
/// </summary>
public static class MidiKeyMap
{
	/// <summary>pitch → 虚拟键码(VK)。仅包含合法演奏音域。</summary>
	public static readonly IReadOnlyDictionary<int, int> PitchToVk = new Dictionary<int, int>
	{
		{ 48, 0x49 }, // I
		{ 49, 0x38 }, // 8
		{ 50, 0x4F }, // O
		{ 51, 0x39 }, // 9
		{ 52, 0x50 }, // P
		{ 53, 0xDB }, // [
		{ 54, 0x30 }, // 0
		{ 55, 0xDD }, // ]
		{ 56, 0xBD }, // -
		{ 57, 0xDC }, // \
		{ 58, 0xBB }, // =
		{ 59, 0xDE }, // '
		{ 60, 0x51 }, // Q
		{ 61, 0x32 }, // 2
		{ 62, 0x57 }, // W
		{ 63, 0x33 }, // 3
		{ 64, 0x45 }, // E
		{ 65, 0x52 }, // R
		{ 66, 0x35 }, // 5
		{ 67, 0x54 }, // T
		{ 68, 0x36 }, // 6
		{ 69, 0x59 }, // Y
		{ 70, 0x37 }, // 7
		{ 71, 0x55 }, // U
		{ 72, 0x5A }, // Z
		{ 73, 0x53 }, // S
		{ 74, 0x58 }, // X
		{ 75, 0x44 }, // D
		{ 76, 0x43 }, // C
		{ 77, 0x56 }, // V
		{ 78, 0x47 }, // G
		{ 79, 0x42 }, // B
		{ 80, 0x48 }, // H
		{ 81, 0x4E }, // N
		{ 82, 0x4A }, // J
		{ 83, 0x4D }, // M
		{ 84, 0xBF }, // /
	};

	/// <summary>音符号是否在可演奏范围内(48~84)。</summary>
	public static bool IsPlayablePitch(int pitch) => pitch is >= 48 and <= 84;

	/// <summary>取 pitch 对应的虚拟键码;不在范围内返回 0。</summary>
	public static int GetVk(int pitch) => PitchToVk.TryGetValue(pitch, out var vk) ? vk : 0;
}
