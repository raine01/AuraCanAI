using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using AuraCanAI.Dalamud.Core;

namespace AuraCanAI.Dalamud.Windows;

/// <summary>游戏内控制面板</summary>
public class MainWindow : Window, IDisposable
{
	private readonly Plugin _plugin;

	// DeepSeek Key 管理(原网页端,已迁移到此)
	private string _deepseekKey = "";
	private bool _keyLoaded;
	private bool _keyVisible;

	public MainWindow(Plugin plugin) : base(
		"AuraCanAI 控制面板###AuraCanAIMainWindow",
		ImGuiWindowFlags.AlwaysVerticalScrollbar)
	{
		_plugin = plugin;
		Size = new System.Numerics.Vector2(420, 340);
		SizeCondition = ImGuiCond.FirstUseEver;
	}

	public override void Draw()
	{
		var core = Plugin.AuraCore;
		if (core == null) return;
		var cfg = _plugin.Configuration;

		// ---- DeepSeek Key(原网页端 character 页的 Key 管理,已迁移到此) ----
		ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.8f, 1f, 1), "DeepSeek Key");
		var llm = core.GetLlmConfigJson() as LLMConfig;
		var currentKey = llm?.deepseekKey ?? "";
		if (!_keyLoaded) { _deepseekKey = currentKey; _keyLoaded = true; }
		var keyDirty = _deepseekKey != currentKey;
		ImGui.SetNextItemWidth(240);
		ImGui.InputText("##dsKey", ref _deepseekKey, 128,
			_keyVisible ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password);
		ImGui.SameLine();
		ImGui.Checkbox("显示", ref _keyVisible);
		ImGui.TextWrapped("用于 DeepSeek AI 服务(角色对话、回忆检索主题总结等)。\nKey 独立存储,网页「还原默认配置」不会清除。");
		if (ImGui.Button("保存 Key"))
		{
			core.SaveApiKey(_deepseekKey.Trim());
			_keyLoaded = false;
			Plugin.Log.Information("DeepSeek Key 已保存");
		}
		if (keyDirty)
			ImGui.TextColored(new System.Numerics.Vector4(1f, 0.7f, 0.3f, 1), "Key 已修改,点击「保存 Key」生效");
		ImGui.Separator();

		// ---- 网页服务 ----
		ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.8f, 1f, 1), "本地网页服务");
		var port = cfg.HttpPort;
		if (ImGui.InputInt("网页端口", ref port))
		{
			cfg.HttpPort = Math.Clamp(port, 1024, 65535);
			cfg.Save(Plugin.PluginInterface);
		}
		ImGui.TextWrapped("端口修改后点「重启网页服务」生效。");
		if (ImGui.Button("重启网页服务"))
		{
			core.StopWeb();
			core.StartWeb();
		}
		ImGui.SameLine();
		if (ImGui.Button("检测服务"))
		{
			var ok = core.CheckWeb();
			Plugin.Log.Information(ok ? $"网页服务正常: {core.WebUrl}" : "网页服务不可访问!");
		}
		var webRunning = core.Http != null;
		ImGui.Text(webRunning ? $"状态: 运行中 (端口 {cfg.HttpPort})" : "状态: 未运行");
		if (webRunning)
		{
			ImGui.Text($"浏览器打开: {core.WebUrl}");
			if (ImGui.Button("复制网页地址"))
			{
				ImGui.SetClipboardText(core.WebUrl);
				Plugin.Log.Information("网页地址已复制到剪贴板");
			}
		}

		ImGui.Separator();

		// ---- TTS ----
		ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.8f, 1f, 1), "语音播报 (TTS)");
		var ttsEnabled = cfg.TtsEnabled;
		if (ImGui.Checkbox("启用 TTS", ref ttsEnabled))
		{
			cfg.TtsEnabled = ttsEnabled;
			if (core.Tts != null) core.Tts.Enabled = ttsEnabled;
			cfg.Save(Plugin.PluginInterface);
		}
		var volume = cfg.TtsVolume;
		if (ImGui.SliderInt("音量", ref volume, 0, 100))
		{
			cfg.TtsVolume = volume;
			if (core.Tts != null) core.Tts.Volume = volume;
			cfg.Save(Plugin.PluginInterface);
		}
		var rate = cfg.TtsRate;
		if (ImGui.SliderInt("语速", ref rate, -10, 10))
		{
			cfg.TtsRate = rate;
			if (core.Tts != null) core.Tts.Rate = rate;
			cfg.Save(Plugin.PluginInterface);
		}
		var workers = cfg.TtsWorkers;
		if (ImGui.SliderInt("并行播报线程", ref workers, Core.TtsService.MinWorkers, Core.TtsService.MaxWorkers))
		{
			cfg.TtsWorkers = workers;
			if (core.Tts != null) core.Tts.SetWorkers(workers);
			cfg.Save(Plugin.PluginInterface);
		}
		if (ImGui.IsItemHovered()) ImGui.SetTooltip("多人同时说话时并行播报的引擎数(多路声音同时输出,会混音)。人多听不清就调小,积压播不完就调大。");
		if (ImGui.Button("测试语音"))
		{
			core.Tts?.SpeakNow("测试语音播报正常");
		}

		ImGui.Separator();
	}

	public void Dispose() { }
}
