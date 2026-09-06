using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using AuraCanAI.Dalamud.Core;
using AuraCanAI.Dalamud.Core.Midi;

namespace AuraCanAI.Dalamud.Windows;

/// <summary>「打开」按钮主面板:附近玩家(状态过滤/滚动/备注) + 活点地图(房区地图与玩家位置) + 玩家备注(检索/编辑) + 回忆检索(按玩家检索聊天主题) + 行为设置 + 演奏(MIDI)</summary>
public class DashboardWindow : Window, IDisposable
{
	private readonly Plugin _plugin;
	private DateTime _lastRefresh = DateTime.MinValue;
	private List<NearbyPlayerInfo> _players = new();
	private string _refreshedAt = "";
	private int _statusFilterIndex; // 0=全部 1-3=指定状态 4=其他
	private string _nearbySearch = ""; // 附近玩家名字模糊搜索
	private int _forceTab = -1; // 指令指定打开的页签(-1=不强制;0=附近玩家 1=活点地图 2=玩家备注 3=回忆检索 4=行为设置 5=演奏)

	// 玩家备注编辑状态(查询/编辑共用同一 UI:附近玩家行按钮、备注 tab 行点击、/aca note 指令都走这里)
	private ulong _noteEditContentId; // 正在编辑的玩家 ContentId(0=未在编辑)
	private string _noteEditName = ""; // 名字@服务器(显示/保存用)
	private string _noteEditText = ""; // 编辑框内容(多行)
	private ulong _noteConfirmDelete; // 两段式删除确认(保存的 ContentId;0=无)
	private string _noteSearch = ""; // 备注检索关键字
	private int _forceTabFrames; // SetSelected 连续帧数(页签切换双保险,防首帧被输入控件激活态吞掉)
	private int _forceTabTarget = -1; // 连续帧切换的目标页签

	public DashboardWindow(Plugin plugin) : base(
		"AuraCanAI###AuraCanAIDashboard",
		ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
	{
		_plugin = plugin;
		Size = new Vector2(500, 420);
		SizeCondition = ImGuiCond.FirstUseEver;
	}

	/// <summary>按指令打开指定页签(窗口未开则先打开;tabIndex=-1 仅打开窗口保持上次页签)</summary>
	public void OpenTab(int tabIndex)
	{
		IsOpen = true;
		_forceTab = tabIndex;
	}

	/// <summary>外部(指令)直接触发回忆检索:填入名字并立即搜索(名字为空则仅打开页签)</summary>
	public void StartSearch(string name)
	{
		OpenTab(3); // 打开并切到回忆检索页签
		if (string.IsNullOrWhiteSpace(name)) return;
		_memInput = name;
		var core = Plugin.AuraCore;
		if (core != null) StartMemorySearch(core, name);
	}

	/// <summary>外部(指令)设置活点地图查询:填入名字片段(多个用 | 分隔)并切换页签(查询即时生效)</summary>
	public void SetMapQuery(string query)
	{
		OpenTab(1); // 活点地图
		if (string.IsNullOrWhiteSpace(query)) return;
		_mapQuery = query;
		_mapQueryTerms = query
			.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(s => s.Length > 0)
			.ToList();
	}

	public override void Draw()
	{
		var core = Plugin.AuraCore;
		if (core == null) return;

		// 500ms 节流刷新玩家列表(活点地图每次绘制实时取)
		if ((DateTime.Now - _lastRefresh).TotalMilliseconds > 500)
		{
			_lastRefresh = DateTime.Now;
			_players = core.GetNearbyPlayers();
			_refreshedAt = DateTime.Now.ToString("HH:mm:ss");
		}

		// 指令强制切换页签:仅给目标页签加 SetSelected(ImGui 要求该 flag 一帧内只用于一个页签)
		// SetSelected 连续 2 帧(双保险:首帧可能被输入控件(搜索框等)激活态干扰)
		var forceSel = new[] { ImGuiTabItemFlags.None, ImGuiTabItemFlags.None, ImGuiTabItemFlags.None, ImGuiTabItemFlags.None, ImGuiTabItemFlags.None, ImGuiTabItemFlags.None };
		if (_forceTab is >= 0 and <= 5)
		{
			forceSel[_forceTab] = ImGuiTabItemFlags.SetSelected;
			_forceTabFrames = 2;
			_forceTabTarget = _forceTab;
			Plugin.Log?.Information($"切页签请求: tab={_forceTab}");
		}
		else if (_forceTabFrames > 0 && _forceTabTarget is >= 0 and <= 5)
		{
			forceSel[_forceTabTarget] = ImGuiTabItemFlags.SetSelected;
			_forceTabFrames--;
		}
		var force = _forceTab;
		_forceTab = -1;

		if (ImGui.BeginTabBar("auraTabs", ImGuiTabBarFlags.None))
		{
			if (ImGui.BeginTabItem("附近玩家", forceSel[0]))
			{
				DrawNearbyPlayers(core);
				ImGui.EndTabItem();
			}
			if (ImGui.BeginTabItem("活点地图", forceSel[1]))
			{
				DrawLivingMap(core);
				ImGui.EndTabItem();
			}
			if (ImGui.BeginTabItem("玩家备注", forceSel[2]))
			{
				DrawPlayerNotes(core);
				ImGui.EndTabItem();
			}
			if (ImGui.BeginTabItem("回忆检索", forceSel[3]))
			{
				DrawMemoryRetrieval(core);
				ImGui.EndTabItem();
			}
			if (ImGui.BeginTabItem("行为设置", forceSel[4]))
			{
				DrawBehaviors(core);
				ImGui.EndTabItem();
			}
			if (ImGui.BeginTabItem("演奏", forceSel[5]))
			{
				DrawMidi(core);
				ImGui.EndTabItem();
			}
			ImGui.EndTabBar();
		}
	}

	// ==================== Tab:附近玩家 ====================

	private void DrawNearbyPlayers(AuraCanAiCore core)
	{
		ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1), "附近玩家");
		ImGui.SameLine();
		ImGui.Text($"  {_players.Count} 人");
		ImGui.SameLine();
		ImGui.TextDisabled($"({_refreshedAt} 刷新)");
		ImGui.TextWrapped("点击玩家行可复制 名字@服务器");
		ImGui.Separator();

		// ---- 状态过滤下拉(固定三个常用 RP 状态 + 其他,按 OnlineStatus RowId 匹配,更稳) ----
		var filterItems = new List<string> { "全部", "希望组队", "接受镶嵌魔晶石请求", "角色扮演中", "其他" };
		if (_statusFilterIndex >= filterItems.Count) _statusFilterIndex = 0;
		ImGui.Combo("状态过滤", ref _statusFilterIndex, filterItems, -1);

		// 名字模糊搜索(匹配 清洗名 或 名字@服务器,不区分大小写)
		ImGui.InputTextWithHint("##nearbySearch", "搜索玩家名(模糊)", ref _nearbySearch, 128);

		// 三个目标状态的 RowId(反查失败返回 0)
		var rpTargetIds = new HashSet<uint>
		{
			core.GetOnlineStatusId("希望组队"),
			core.GetOnlineStatusId("接受镶嵌魔晶石请求"),
			core.GetOnlineStatusId("角色扮演中"),
		};
		rpTargetIds.Remove(0);

		// 应用过滤:指定状态按 RowId 比较(反查失败兜底中文名);「其他」= 不属于三个目标状态
		var shown = (IEnumerable<NearbyPlayerInfo>)_players;
		if (_statusFilterIndex >= 1 && _statusFilterIndex <= 3)
		{
			var name = filterItems[_statusFilterIndex];
			var targetId = core.GetOnlineStatusId(name);
			if (targetId != 0)
				shown = shown.Where(p => p.statusId == targetId);
			else
				shown = shown.Where(p => p.status == name);
		}
		else if (_statusFilterIndex == 4) // 其他
		{
			shown = rpTargetIds.Count > 0
				? shown.Where(p => !rpTargetIds.Contains(p.statusId))
				: shown.Where(p => p.status is not ("希望组队" or "接受镶嵌魔晶石请求" or "角色扮演中"));
		}
		// 名字模糊搜索(搜索框内容非空时)
		var searchKw = _nearbySearch.Trim();
		if (searchKw.Length > 0)
		{
			shown = shown.Where(p =>
				p.name.Contains(searchKw, StringComparison.OrdinalIgnoreCase)
				|| $"{p.name}@{p.world}".Contains(searchKw, StringComparison.OrdinalIgnoreCase));
		}
		var shownList = shown.ToList();

		ImGui.Separator();
		if (_players.Count != shownList.Count)
		{
			ImGui.TextDisabled($"显示 {shownList.Count} / 共 {_players.Count} 人");
			ImGui.Separator();
		}

		// ---- 玩家表格(可滚动区域) ----
		var avail = ImGui.GetContentRegionAvail();
		if (ImGui.BeginChild("playerList", new Vector2(avail.X, Math.Max(avail.Y - 2, 60)), false))
		{
			if (ImGui.BeginTable("nearby", 4,
					ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
			{
				ImGui.TableSetupColumn("玩家", ImGuiTableColumnFlags.WidthStretch, 0f, 0);
				ImGui.TableSetupColumn("种族/性别", ImGuiTableColumnFlags.WidthFixed, 110f, 1);
				ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 110f, 2);
				ImGui.TableSetupColumn("备注", ImGuiTableColumnFlags.WidthFixed, 70f, 3);
				ImGui.TableHeadersRow();

				if (shownList.Count == 0)
				{
					ImGui.TableNextRow();
					ImGui.TableSetColumnIndex(0);
					ImGui.TextDisabled("没有符合条件的玩家");
				}

				foreach (var p in shownList)
				{
					ImGui.TableNextRow();

					// 名字@服务器(点击名字区域可复制;已有备注的名字前加 ★ 标记)
					// 注意:勿用 SpanAllColumns——它会横跨整行覆盖后续列(备注按钮)的点击命中(已踩坑:按钮时灵时不灵)
					ImGui.TableSetColumnIndex(0);
					var display = string.IsNullOrEmpty(p.world) ? p.name : $"{p.name}@{p.world}";
					var hasNote = p.contentId != 0 && core.GetNote(p.contentId) != null;
					if (hasNote)
					{
						ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1), "★");
						ImGui.SameLine();
					}
					if (ImGui.Selectable(display, false))
					{
						ImGui.SetClipboardText(display);
						Plugin.Log.Information($"已复制玩家名: {display}");
					}

					// 种族/性别
					ImGui.TableSetColumnIndex(1);
					ImGui.Text($"{p.race}·{p.gender}");

					// 在线状态(无状态显示灰色 —)
					ImGui.TableSetColumnIndex(2);
					if (string.IsNullOrEmpty(p.status)) ImGui.TextDisabled("—");
					else ImGui.Text(p.status);

					// 备注按钮(仅在场玩家可新建/编辑,面对面才能创建)
					ImGui.TableSetColumnIndex(3);
					if (p.contentId == 0)
					{
						ImGui.TextDisabled("—");
					}
					else
					{
						ImGui.PushID($"note_{p.contentId}");
						if (ImGui.Button(hasNote ? "编辑" : "添加"))
							OpenNoteEditor(core, p.contentId, display); // 切到玩家备注页签并打开编辑区
						if (ImGui.IsItemHovered())
							ImGui.SetTooltip(hasNote ? "查看/编辑该玩家的备注" : "给该玩家添加备注(仅面对面时可用)");
						ImGui.PopID();
					}
				}

				ImGui.EndTable();
			}
			ImGui.EndChild();
		}
	}

	// ==================== 玩家备注(检索/编辑共用同一 UI) ====================

	/// <summary>打开备注编辑(skipSwitch=true 时切到「玩家备注」页签;查询与编辑同一 UI)</summary>
	public void OpenNoteEditor(AuraCanAiCore core, ulong contentId, string nameWorld, bool switchToNotesTab = true)
	{
		if (contentId == 0) return;
		Plugin.Log?.Information($"备注编辑打开: {nameWorld} (ContentId={contentId})");
		_noteEditContentId = contentId;
		_noteEditName = nameWorld;
		_noteEditText = core.GetNote(contentId)?.Note ?? "";
		_noteConfirmDelete = 0;
		if (switchToNotesTab) OpenTab(2); // 玩家备注页签
	}

	/// <summary>备注编辑区(附近玩家页签与玩家备注页签共用;点击按钮/行后内联显示)</summary>
	private void DrawNoteEditor(AuraCanAiCore core)
	{
		if (_noteEditContentId == 0) return;
		var editing = core.GetNote(_noteEditContentId);
		ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1), editing != null ? $"★ 编辑备注: {_noteEditName}" : $"✚ 添加备注: {_noteEditName}");
		ImGui.TextDisabled("备注绑定角色ID,玩家改名后依然有效;内容可多行。");
		ImGui.InputTextMultiline("##noteText", ref _noteEditText, 4000, new Vector2(-1, 130));

		if (ImGui.Button("保存"))
		{
			if (core.SaveNote(_noteEditContentId, _noteEditName, _noteEditText))
			{
				Plugin.Log.Information($"已保存备注: {_noteEditName}");
				_noteEditContentId = 0;
				_noteConfirmDelete = 0;
			}
			else
			{
				Plugin.Log.Warning($"备注保存失败(ContentId 无效): {_noteEditName}");
			}
		}
		ImGui.SameLine();
		if (editing != null)
		{
			if (_noteConfirmDelete == _noteEditContentId)
			{
				if (ImGui.Button("确认删除?"))
				{
					core.DeleteNote(_noteEditContentId);
					Plugin.Log.Information($"已删除备注: {_noteEditName}");
					_noteEditContentId = 0;
					_noteConfirmDelete = 0;
				}
				ImGui.SameLine();
				if (ImGui.Button("取消")) _noteConfirmDelete = 0;
			}
			else if (ImGui.Button("删除"))
			{
				_noteConfirmDelete = _noteEditContentId;
			}
			ImGui.SameLine();
		}
		if (ImGui.Button("关闭"))
		{
			_noteEditContentId = 0;
			_noteConfirmDelete = 0;
		}
		ImGui.Separator();
	}

	private void DrawPlayerNotes(AuraCanAiCore core)
	{
		// ---- 编辑区(选择玩家后显示;查询与编辑同一 UI) ----
		DrawNoteEditor(core);

		// ---- 检索 + 列表 ----
		ImGui.TextWrapped("添加备注:打开「附近玩家」页签,点玩家行右侧的「添加」(仅面对面时可用)。");
		ImGui.TextWrapped("备注绑定角色ID,玩家改名后依然能找到;点击下方结果行可查看/编辑/删除。");
		ImGui.InputTextWithHint("##noteSearch", "检索 玩家名 或 备注内容(留空显示全部)", ref _noteSearch, 256);
		var notes = core.SearchNotes(_noteSearch);
		ImGui.TextDisabled($"共 {notes.Count} 条备注");
		var avail = ImGui.GetContentRegionAvail();
		if (ImGui.BeginChild("noteList", new Vector2(avail.X, Math.Max(avail.Y - 2, 60)), false))
		{
			if (notes.Count == 0)
			{
				ImGui.TextDisabled(_noteSearch.Length == 0 ? "还没有备注" : "没有匹配的备注");
			}
			foreach (var n in notes)
			{
				ImGui.PushID($"noteRow_{n.ContentId}");
				var summary = n.Note.Replace('\n', ' ').Replace('\r', ' ');
				if (summary.Length > 50) summary = summary[..50] + "…";
				var line = $"★ {n.Name}   {summary}   ({n.UpdatedAt:MM-dd HH:mm})";
				if (ImGui.Selectable(line, false, ImGuiSelectableFlags.SpanAllColumns))
					OpenNoteEditor(core, n.ContentId, n.Name);
				if (ImGui.IsItemHovered()) ImGui.SetTooltip(n.Note);
				ImGui.PopID();
			}
		}
		ImGui.EndChild();
	}

	// ==================== Tab:活点地图 ====================

	private void DrawLivingMap(AuraCanAiCore core)
	{
		// 在默语频道显示注视者方位(勾选后,有人注视你时 /e 提示方位;默认关闭)+ 测试发送,同一行
		var cfg = _plugin.Configuration;
		var showLooking = cfg.ShowLookingDirection;
		if (ImGui.Checkbox("在默语频道显示注视者方位", ref showLooking))
		{
			cfg.ShowLookingDirection = showLooking;
			cfg.Save(Plugin.PluginInterface);
		}
		ImGui.SameLine();
		if (ImGui.Button("测试发送(发到默语频道)"))
		{
			var ok = core.RunCommandPublic("/e AuraCanAI 消息通道测试");
			if (ok) Plugin.Log.Information("测试消息已发送");
		}
		ImGui.Separator();

		// 查询框:名字片段模糊匹配,多个用 | 分隔;匹配到的点标橘黄(与正在看我的同色),地图外的贴边显示
		ImGui.TextWrapped("查询角色(模糊匹配,多个用 | 分隔;匹配到的点标橘黄,地图外的贴边显示):");
		ImGui.SetNextItemWidth(220);
		if (ImGui.InputText("##mapQuery", ref _mapQuery, 64))
		{
			_mapQueryTerms = _mapQuery
				.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(s => s.Length > 0)
				.ToList();
		}
		ImGui.Separator();

		var players = core.GetAllPlayerPositions();
		if (players.Count == 0)
		{
			ImGui.TextWrapped("附近没有玩家");
			return;
		}

		var avail = ImGui.GetContentRegionAvail();
		// 显示区域固定 1:1 正方形(取可用区域较小边,居中)
		var side = Math.Max(Math.Min(avail.X, avail.Y), 80);
		var size = new Vector2(side, side);
		var origin = ImGui.GetCursorScreenPos()
			+ new Vector2(Math.Max((avail.X - side) / 2, 0), Math.Max((avail.Y - side) / 2, 0));
		var dl = ImGui.GetWindowDrawList();

		// 浅土黄背景 + 外框
		dl.AddRectFilled(origin, origin + size, 0xFFC3E1F0);
		dl.AddRect(origin, origin + size, 0xFF9A7A4A, 0f, ImDrawFlags.None, 1f);

		// 坐标映射:住宅区用房间固定比例(带十字),其他区域用动态范围(无房间框)
		Func<float, float> toPanelX, toPanelZ;
		var isHousing = core.IsInHousing();
		if (isHousing)
		{
			var roomSize = core.GetRoomSize();
			var half = roomSize / 2f;
			toPanelX = w => (w + half) / roomSize;
			toPanelZ = w => (w + half) / roomSize;
			// 中间十字参考线(房间中心)
			var midX = origin.X + size.X / 2;
			var midY = origin.Y + size.Y / 2;
			dl.AddLine(new Vector2(midX, origin.Y), new Vector2(midX, origin.Y + size.Y), 0x33000000, 1f);
			dl.AddLine(new Vector2(origin.X, midY), new Vector2(origin.X + size.X, midY), 0x33000000, 1f);
		}
		else
		{
			// 以自己为中心,显示周边 40x40 区域(视野更大;自己恒在面板中心)
			var self = players.FirstOrDefault(p => p.isSelf);
			var centerX = self?.worldX ?? 0f;
			var centerZ = self?.worldZ ?? 0f;
			toPanelX = w => (w - centerX + 20f) / 40f;
			toPanelZ = w => (w - centerZ + 20f) / 40f;
		}

		// 玩家点(查询目标贴边显示;其他玩家超出视野不绘制,降低负荷)
		foreach (var p in players)
		{
			var rx = toPanelX(p.worldX);
			var rz = toPanelZ(p.worldZ);
			var queried = IsMapQueryHit(p);
			if (rx < 0 || rx > 1 || rz < 0 || rz > 1)
			{
				if (!queried) continue; // 非查询目标:超出视野不绘制(不 clamp 到边缘)
				rx = Math.Clamp(rx, 0f, 1f); // 查询目标:贴边显示
				rz = Math.Clamp(rz, 0f, 1f);
			}
			var center = new Vector2(origin.X + rx * size.X, origin.Y + rz * size.Y);

			// 点颜色:自己金色、查询目标/正在看我 橘黄、其他玩家蓝色(ABGR)
			var isLooking = core.IsLookingAtMe(p.entityId);
			var hl = queried || isLooking;
			var col = p.isSelf ? 0xFF00D7FFu
				: hl ? 0xFF00A5FFu
				: 0xFFFF8C3Au;
			dl.AddCircleFilled(center, p.isSelf ? 6f : 4f, col);

			// 自己不显示名字(去掉"我");其他玩家显示名字标签(查询目标/被注视保持橘黄,其他浅灰)
			if (!p.isSelf)
			{
				var nameCol = hl ? col : 0xFFAAAAAAu; // 浅灰
				// 名字标签:黑描边 + 彩色字;住宅区在点右上方,其他区域在点正下方(紧凑)
				Vector2 namePos = isHousing
					? center + new Vector2(9, -7)
					: new Vector2(center.X - 9, center.Y + 6);
				dl.AddText(namePos + new Vector2(1, 1), 0xFF000000u, p.name);
				dl.AddText(namePos, nameCol, p.name);
			}
		}
	}

	/// <summary>活点地图查询命中判断:名字(清洗后)包含任一查询片段即命中(模糊匹配)</summary>
	private bool IsMapQueryHit(PlayerPos p)
	{
		if (_mapQueryTerms.Count == 0) return false;
		foreach (var term in _mapQueryTerms)
		{
			if (p.name.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	// ==================== Tab:行为设置 ====================

	private bool _behavIsNew; // 新增(true)还是编辑(false)
	private BehaviorItem? _behavEditing; // 编辑副本(保存成功才写回配置);null=未在编辑
	private string _behavError = ""; // 保存失败红字提示(含原因)
	// ---- AI 助手生成行为命令 ----
	private string _behavAiDesc = ""; // 自然语言描述输入
	private Task<BehaviorGenerateResult>? _behavAiTask; // 后台生成任务
	private BehaviorGenerateResult? _behavAiResult; // 最近一次生成结果
	private bool _behavAiBusy; // 生成中

	private void DrawBehaviors(AuraCanAiCore core)
	{
		// 编辑中:只显示编辑区,隐藏「新增行为」按钮行与列表(取消/保存后回到列表);滚动容器防止内容溢出
		if (_behavEditing != null)
		{
			var editAvail = ImGui.GetContentRegionAvail();
			if (ImGui.BeginChild("behavEdit", editAvail, false))
			{
				DrawBehaviorEditor(core);
			}
			ImGui.EndChild();
			return;
		}

		if (ImGui.Button("+ 新增行为")) BeginEditBehavior(null);
		ImGui.SameLine();
		var items = core.GetBehaviorItems();
		var enabledCount = items.Count(b => b.enabled);
		ImGui.TextDisabled($"已启用 {enabledCount} / 共 {items.Count} 条");
		ImGui.Separator();

		var avail = ImGui.GetContentRegionAvail();
		if (ImGui.BeginChild("behavList", new Vector2(avail.X, Math.Max(avail.Y - 2, 60)), false))
		{
			if (items.Count == 0)
			{
				ImGui.TextDisabled("暂无行为,点击「+ 新增行为」创建");
			}
			else if (ImGui.BeginTable("behavTable", 4,
				ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
			{
				ImGui.TableSetupColumn("序号", ImGuiTableColumnFlags.WidthFixed, 40f, 0);
				ImGui.TableSetupColumn("注释", ImGuiTableColumnFlags.WidthStretch, 0f, 1);
				ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, 40f, 2);
				ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 60f, 3);
				ImGui.TableHeadersRow();

				foreach (var b in items)
				{
					ImGui.TableNextRow();
					ImGui.TableSetColumnIndex(0);
					ImGui.Text(b.id.ToString());
					ImGui.TableSetColumnIndex(1);
					if (string.IsNullOrEmpty(b.comment)) ImGui.TextDisabled("(无注释)");
					else ImGui.TextWrapped(b.comment);
					ImGui.TableSetColumnIndex(2);
					var en = b.enabled;
					if (ImGui.Checkbox($"##behavEn{b.id}", ref en)) { b.enabled = en; core.SaveBehaviors(); }
					ImGui.TableSetColumnIndex(3);
					if (ImGui.Button($"编辑##behavEd{b.id}")) BeginEditBehavior(b);
				}
				ImGui.EndTable();
			}
		}
		ImGui.EndChild();
	}

	/// <summary>AI 助手:自然语言描述 → 行为命令(位于行为编辑区内)。
	/// 生成在后台任务中执行,完成后自动填入上方「行为语句」输入框;未配 Key 时置灰提示。</summary>
	private void DrawBehaviorAiHelper(AuraCanAiCore core)
	{
		// 后台任务完成检查(每帧);成功时直接填入定义框
		if (_behavAiBusy && _behavAiTask != null && _behavAiTask.IsCompleted)
		{
			try { _behavAiResult = _behavAiTask.Result; }
			catch (Exception e) { _behavAiResult = new BehaviorGenerateResult { Error = $"生成异常: {e.Message}" }; }
			_behavAiBusy = false;
			_behavAiTask = null;
			if (_behavAiResult.Success && _behavEditing != null)
			{
				_behavEditing.definition = _behavAiResult.Definition;
				if (!string.IsNullOrEmpty(_behavAiResult.Comment) && string.IsNullOrEmpty(_behavEditing.comment))
					_behavEditing.comment = _behavAiResult.Comment;
			}
		}

		ImGui.Separator();
		ImGui.TextDisabled("不会编辑行为语句?在下方输入想要的效果");
		ImGui.SameLine();
		var canGen = !_behavAiBusy && !string.IsNullOrWhiteSpace(_behavAiDesc);
		ImGui.BeginDisabled(!canGen);
		if (ImGui.Button("生成行为语句"))
			StartBehaviorAiGenerate(core);
		ImGui.EndDisabled();
		// 描述输入框(多行,与「行为语句」同宽;回车换行,提交靠按钮)
		ImGui.SetNextItemWidth(-1);
		ImGui.InputTextMultiline("##behavAiDesc", ref _behavAiDesc, 512, new Vector2(0, 60));
		if (!core.HasApiKey)
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1), "未配置 DeepSeek API Key(游戏内「设置」面板配置)");
		else if (_behavAiBusy)
			ImGui.TextColored(new Vector4(0.85f, 0.8f, 0.3f, 1), "正在生成...");

		if (_behavAiResult == null) return;

		if (_behavAiResult.Error.Length > 0)
		{
			ImGui.PushTextWrapPos();
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1), _behavAiResult.Error);
			ImGui.PopTextWrapPos();
		}
		else if (_behavAiResult.Success)
		{
			ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.5f, 1), "✓ 已生成并填入上方「行为语句」,保存时会自动检查语法");
			ImGui.SameLine();
			if (ImGui.Button("重新生成")) StartBehaviorAiGenerate(core);
		}
		ImGui.Spacing();
	}

	/// <summary>启动后台生成任务(单轮;无 Key / 描述为空 / 已在生成时不启动)</summary>
	private void StartBehaviorAiGenerate(AuraCanAiCore core)
	{
		var desc = _behavAiDesc.Trim();
		if (desc.Length == 0 || _behavAiBusy) return;
		if (!core.HasApiKey)
		{
			_behavAiResult = new BehaviorGenerateResult { Error = "未配置 DeepSeek API Key,请在游戏内「设置」面板(AuraCanAI 控制面板)配置后再试" };
			return;
		}
		_behavAiResult = null;
		_behavAiBusy = true;
		_behavAiTask = Task.Run(() => core.GenerateBehavior(desc));
	}

	/// <summary>开始编辑(item=null 为新增;复制副本,保存成功才写回配置)</summary>
	private void BeginEditBehavior(BehaviorItem? item)
	{
		_behavIsNew = item == null;
		_behavEditing = item == null
			? new BehaviorItem { id = 0, enabled = true }
			: new BehaviorItem
			{
				id = item.id,
				comment = item.comment,
				enabled = item.enabled,
				chatNotice = item.chatNotice,
				skipOnLeave = item.skipOnLeave,
				skipOnCombat = item.skipOnCombat,
				definition = item.definition,
			};
		_behavError = "";
		_behavAiResult = null;
		_behavAiDesc = "";
	}

	/// <summary>绘制内联编辑区:注释 + 定义文本(多行) + 聊天内提示 + 保存/删除/取消(保存失败红字)</summary>
	private void DrawBehaviorEditor(AuraCanAiCore core)
	{
		ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1), _behavIsNew ? "新增行为" : $"编辑行为 #{_behavEditing!.id}");

		ImGui.Text("注释:");
		ImGui.SetNextItemWidth(-1);
		ImGui.InputText("##behavComment", ref _behavEditing!.comment, 200);

		ImGui.Text("行为语句:");
		ImGui.SetNextItemWidth(-1);
		ImGui.InputTextMultiline("##behavDef", ref _behavEditing.definition, 4096, new Vector2(0, 100));

		// AI 助手生成行为命令(描述 → 命令,成功后自动填入上方定义框)
		DrawBehaviorAiHelper(core);

		ImGui.Checkbox("离开时不触发", ref _behavEditing.skipOnLeave);
		ImGui.Checkbox("战斗中不触发", ref _behavEditing.skipOnCombat);
		ImGui.Checkbox("聊天内提示(触发时在聊天栏 /e 提示)", ref _behavEditing.chatNotice);

		if (_behavError.Length > 0)
		{
			// 长错误信息自动换行(TextColored 默认不换行,会横向溢出)
			ImGui.PushTextWrapPos();
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1), _behavError);
			ImGui.PopTextWrapPos();
		}

		ImGui.Spacing();
		if (ImGui.Button("保存"))
		{
			var err = SaveBehaviorEdit();
			if (err == null) _behavEditing = null;
			else _behavError = err;
		}
		ImGui.SameLine();
		if (!_behavIsNew)
		{
			if (ImGui.Button("删除"))
			{
				core.RemoveBehavior(_behavEditing!.id);
				_behavEditing = null;
			}
			ImGui.SameLine();
		}
		if (ImGui.Button("取消")) _behavEditing = null;
		ImGui.Spacing();
	}

	/// <summary>保存当前编辑:解析校验定义,通过后写回配置并持久化+重载引擎。返回 null=成功,否则为红字错误。</summary>
	private string? SaveBehaviorEdit()
	{
		var core = Plugin.AuraCore;
		if (core == null) return "核心未就绪";
		var def = (_behavEditing?.definition ?? "").Trim();
		if (def.Length == 0) return "定义不能为空";

		var parsed = BehaviorParser.Parse(def, _behavEditing!.id, _behavEditing.comment, _behavEditing.chatNotice, _behavEditing.skipOnLeave, _behavEditing.skipOnCombat);
		if (parsed.Errors.Count > 0)
		{
			var err = string.Join("\n", parsed.Errors);
			// 内容由 AI 生成且描述框仍有关键词时,提示用户改描述重新生成
			if (!string.IsNullOrWhiteSpace(_behavAiDesc))
				err += "\n(内容来自 AI 生成,可修改下方描述后重新点「生成行为语句」)";
			return err;
		}
		if (parsed.Rules.Count == 0) return "定义中没有有效规则(请检查语法)";

		if (_behavIsNew)
		{
			_behavEditing.id = core.AllocateBehaviorId();
			core.GetBehaviorItems().Add(_behavEditing);
		}
		else
		{
			var list = core.GetBehaviorItems();
			var idx = list.FindIndex(x => x.id == _behavEditing.id);
			if (idx < 0) return "行为不存在(可能已被删除)";
			list[idx] = _behavEditing;
		}
		core.SaveBehaviors();
		return null;
	}

	// ==================== Tab:回忆检索 ====================

	private string _memInput = "";
	private int _memGen; // 查询代数:新查询 +1,旧异步结果据此丢弃(释放内存)
	private readonly object _memLock = new();
	private MemorySearchData? _memData;
	private List<MemoryTheme> _memThemes = new();
	private string _memStatus = ""; // 搜索中.../总结中.../错误提示
	private int _memSelected = -1; // -1=主题列表;>=0=查看该主题对话
	private List<MemoryChatLine> _memConv = new(); // 当前主题对话切片
	private DateTime _memConvStart; // 主题时间段(高亮判断用)
	private DateTime _memConvEnd;

	// 活点地图查询:输入名字片段(多个用 | 分隔),匹配到的点标橘黄并贴边显示
	private string _mapQuery = "";
	private List<string> _mapQueryTerms = new();

	private void DrawMemoryRetrieval(AuraCanAiCore core)
	{
		ImGui.TextWrapped("输入角色名称(支持部分匹配,不含区服),回车或点「搜索」;先按玩家过滤聊天记录,再由大模型归纳主题。");
		ImGui.TextWrapped("点击主题查看相关对话:该时间段内全部频道对话,并向前/后各扩充 10 条。");
		ImGui.SetNextItemWidth(200);
		var enter = ImGui.InputText("##memInput", ref _memInput, 32, ImGuiInputTextFlags.EnterReturnsTrue);
		ImGui.SameLine();
		var name = _memInput.Trim();
		if (ImGui.Button("搜索") || (enter && name.Length > 0))
		{
			if (name.Length > 0) StartMemorySearch(core, name);
		}
		ImGui.Separator();

		MemorySearchData? data;
		List<MemoryTheme> themes;
		string status;
		lock (_memLock) { data = _memData; themes = _memThemes; status = _memStatus; }

		if (!string.IsNullOrEmpty(status))
		{
			var bad = status.StartsWith("未找到") || status.StartsWith("出错") || status.Contains("失败");
			ImGui.TextColored(bad ? new Vector4(1f, 0.5f, 0.5f, 1) : new Vector4(0.8f, 0.8f, 0.8f, 1), status);
		}

		if (data == null)
		{
			if (string.IsNullOrEmpty(status)) ImGui.TextDisabled("输入玩家名开始检索");
			return;
		}
		if (data.PlayerLines.Count == 0) return; // 未找到(状态栏已提示)

		// 匹配玩家信息:每个玩家名是独立按钮,点击谁复制谁(按钮 ID 独立,避免同行控件状态串扰)
		var blue = new Vector4(0.4f, 0.8f, 1f, 1);
		ImGui.TextColored(blue, "匹配玩家: ");
		for (int k = 0; k < data.MatchedNames.Count; k++)
		{
			var playerName = data.MatchedNames[k];
			ImGui.SameLine();
			ImGui.PushID(k);
			ImGui.PushStyleColor(ImGuiCol.Button, 0x00000000); // 透明背景
			ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0x22404040); // hover 淡灰
			ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0x33404040); // 按下稍深
			ImGui.PushStyleColor(ImGuiCol.Border, 0x00000000); // 无边框
			ImGui.PushStyleColor(ImGuiCol.Text, blue);
			if (ImGui.Button(playerName)) ImGui.SetClipboardText(playerName);
			ImGui.PopStyleColor(); // Text
			ImGui.PopStyleColor(); // Border
			ImGui.PopStyleColor(); // ButtonActive
			ImGui.PopStyleColor(); // ButtonHovered
			ImGui.PopStyleColor(); // Button
			if (ImGui.IsItemHovered()) ImGui.SetTooltip($"点击复制: {playerName}");
			ImGui.PopID();
		}
		ImGui.SameLine();
		ImGui.Text($"  {data.PlayerLines.Count} 条消息");
		ImGui.TextDisabled(data.Extended
			? $"(最近半年无记录,已扩展搜索最近一年;文件范围 {data.FileRange})"
			: $"(文件范围 {data.FileRange})");
		ImGui.Separator();

		if (_memSelected >= 0 && _memSelected < themes.Count)
		{
			DrawMemoryConversation(themes[_memSelected]);
		}
		else
		{
			if (themes.Count == 0)
			{
				ImGui.TextDisabled("暂无主题(请检查 DeepSeek Key 是否已配置后重试)");
				return;
			}
			ImGui.TextWrapped("点击主题查看相关对话(亮色为匹配玩家的发言,绿色为发给 TA 的私聊)");
			var avail = ImGui.GetContentRegionAvail();
			if (ImGui.BeginChild("memThemeList", new Vector2(avail.X, Math.Max(avail.Y - 2, 60)), false))
			{
				// 倒序显示:新的(时间靠后的)在上面;仅标题行可点击进入对话
				// 命中多个玩家时,每个话题前标注其主要归属玩家(单玩家不标注)
				var multi = data.MatchedNames.Count > 1;
				for (int i = themes.Count - 1; i >= 0; i--)
				{
					var t = themes[i];
					ImGui.PushID(i);
					var owner = multi ? GetThemeOwnerName(data, t) : "";
					var label = string.IsNullOrEmpty(owner)
						? $"{t.title}  ({t.StartTime:MM-dd HH:mm} ~ {t.EndTime:MM-dd HH:mm} · {t.Count}条)"
						: $"[{owner}] {t.title}  ({t.StartTime:MM-dd HH:mm} ~ {t.EndTime:MM-dd HH:mm} · {t.Count}条)";
					if (ImGui.Selectable(label, false, ImGuiSelectableFlags.SpanAllColumns))
					{
						_memSelected = i;
						BuildMemoryConversation(data, t);
					}
					if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(t.summary))
						ImGui.SetTooltip(t.summary);
					if (!string.IsNullOrEmpty(t.summary))
						ImGui.TextDisabled(t.summary);
					ImGui.Spacing();
					ImGui.PopID();
				}
			}
			ImGui.EndChild();
		}
	}

	/// <summary>发起新查询(后台任务:搜索文件 + 大模型总结;旧查询结果按代数丢弃,释放内存)</summary>
	private void StartMemorySearch(AuraCanAiCore core, string name)
	{
		_memGen++;
		var gen = _memGen;
		lock (_memLock)
		{
			_memData = null;
			_memThemes = new List<MemoryTheme>();
			_memConv.Clear();
			_memSelected = -1;
			_memStatus = "搜索聊天记录中...";
		}
		Task.Run(() =>
		{
			try
			{
				var data = core.SearchPlayerHistory(name);
				if (gen != _memGen) return; // 已被新查询取代,丢弃结果
				if (data.PlayerLines.Count == 0)
				{
					lock (_memLock) { _memData = data; _memStatus = "未找到该玩家的聊天记录(已搜索最近一年)"; }
					return;
				}
				lock (_memLock) { _memData = data; _memStatus = "大模型归纳主题中..."; }
				var themes = core.SummarizePlayerMessages(data);
				if (gen != _memGen) return;
				lock (_memLock)
				{
					_memThemes = themes;
					_memStatus = themes.Count == 0 ? "未归纳出主题(请检查 DeepSeek Key 配置后重试)" : "";
				}
			}
			catch (Exception e)
			{
				if (gen != _memGen) return;
				lock (_memLock) { _memStatus = "出错: " + e.Message; }
				Plugin.Log.Error($"回忆检索异常: {e}");
			}
		});
	}

	/// <summary>按主题时间段切片对话:该时间段内全部日志行,前后各扩 10 条(跨文件已按时间排序)</summary>
	private void BuildMemoryConversation(MemorySearchData data, MemoryTheme t)
	{
		_memConv.Clear();
		_memConvStart = t.StartTime;
		_memConvEnd = t.EndTime;
		var lines = data.AllLines;
		var lo = LowerBound(lines, t.StartTime); // 第一个 >= StartTime
		var hi = UpperBound(lines, t.EndTime) - 1; // 最后一个 <= EndTime
		if (hi < lo) return;
		lo = Math.Max(0, lo - 10);
		hi = Math.Min(lines.Count - 1, hi + 10);
		for (int i = lo; i <= hi; i++) _memConv.Add(lines[i]);
	}

	private static int LowerBound(List<MemoryChatLine> lines, DateTime t)
	{
		int lo = 0, hi = lines.Count;
		while (lo < hi) { int mid = (lo + hi) / 2; if (lines[mid].Time < t) lo = mid + 1; else hi = mid; }
		return lo;
	}

	private static int UpperBound(List<MemoryChatLine> lines, DateTime t)
	{
		int lo = 0, hi = lines.Count;
		while (lo < hi) { int mid = (lo + hi) / 2; if (lines[mid].Time <= t) lo = mid + 1; else hi = mid; }
		return lo;
	}

	/// <summary>统计主题区间内各匹配玩家的消息数,返回消息最多的玩家名(话题归属标注用)</summary>
	private static string GetThemeOwnerName(MemorySearchData data, MemoryTheme t)
	{
		var counts = new Dictionary<string, int>();
		for (int i = t.start; i <= t.end && i < data.PlayerLines.Count; i++)
		{
			var n = data.PlayerLines[i].CleanName;
			if (string.IsNullOrEmpty(n)) continue;
			counts[n] = counts.TryGetValue(n, out var c) ? c + 1 : 1;
		}
		return counts.Count == 0 ? "" : counts.OrderByDescending(kv => kv.Value).First().Key;
	}

	/// <summary>绘制当前主题的对话切片(点击行复制原文;青色=该玩家发言,绿色=我方发给TA的私聊)</summary>
	private void DrawMemoryConversation(MemoryTheme t)
	{
		if (ImGui.Button("← 返回主题列表")) { _memSelected = -1; _memConv.Clear(); }
		ImGui.SameLine();
		ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1), t.title);
		var names = _memData != null ? string.Join("、", _memData.MatchedNames) : "";
		ImGui.TextDisabled($"{t.StartTime:yyyy-MM-dd HH:mm:ss} ~ {t.EndTime:yyyy-MM-dd HH:mm:ss} · 该时间段全部对话,前后各扩充10条");
		ImGui.TextDisabled($"点击行复制原文; 亮蓝={names}的发言, 绿色=你发给 TA 的私聊");
		ImGui.Separator();
		var avail = ImGui.GetContentRegionAvail();
		if (ImGui.BeginChild("memConvList", new Vector2(avail.X, Math.Max(avail.Y - 2, 60)), false))
		{
			for (int i = 0; i < _memConv.Count; i++)
			{
				var line = _memConv[i];
				var isMyTell = line.IsTarget && line.Channel == "发悄悄话"; // 我方发给 TA 的私聊(日志里 Speaker 是收件人)
				var isTarget = line.IsTarget && !isMyTell
							   && line.Time >= _memConvStart && line.Time <= _memConvEnd;
				var col = isTarget
					? new Vector4(0.5f, 0.9f, 1f, 1)
					: isMyTell
						? new Vector4(0.6f, 0.95f, 0.6f, 1)
						: ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
				ImGui.PushID(i);
				ImGui.PushStyleColor(ImGuiCol.Text, col);
				var display = line.Raw.Length > 130 ? line.Raw[..130] + "…" : line.Raw;
				if (ImGui.Selectable(display, false, ImGuiSelectableFlags.SpanAllColumns))
					ImGui.SetClipboardText(line.Raw);
				if (ImGui.IsItemHovered()) ImGui.SetTooltip(line.Raw);
				ImGui.PopStyleColor();
				ImGui.PopID();
			}
		}
		ImGui.EndChild();
	}

	/// <summary>演奏页(MIDI 播放器,阶段 1)</summary>
	/// <summary>演奏页(大幅简化:歌单管理已移到网页「歌单」页,此处仅状态与基本控制)</summary>
	private void DrawMidi(AuraCanAiCore core)
	{
		ImGui.TextWrapped("演奏功能请在网页「歌单」页操作(浏览器/手机均可,地址见设置面板):");
		ImGui.TextWrapped("· 维护歌单:添加 MIDI、每曲独立设置预处理/拆解/速度/移调、调整顺序");
		ImGui.TextWrapped("· 播放模式:单曲 / 单曲循环 / 列表 / 随机,支持暂停/继续/停止");
		ImGui.Separator();

		// 演奏就绪检测(主声部=自己):职业=吟游诗人 + 演奏模式
		var (isBard, isPerforming, hint) = core.GetPerformStatus();
		var pCol = isBard && isPerforming
			? new Vector4(0.4f, 0.9f, 0.5f, 1)
			: (isBard ? new Vector4(0.9f, 0.8f, 0.3f, 1) : new Vector4(1f, 0.4f, 0.4f, 1));
		ImGui.TextColored(pCol, (isBard && isPerforming ? "● " : isBard ? "⚠ " : "✖ ") + hint);
		if (!isPerforming)
			ImGui.TextDisabled("提示:辅端(合奏)角色请自行确认已持乐器进入演奏模式");
		ImGui.Separator();

		var pl = core.Playlist;
		ImGui.Text($"当前曲目: {(pl.CurrentFileName.Length > 0 ? pl.CurrentFileName : "(未播放)")}");
		var (cur, total) = pl.Main.GetProgress();
		if (total > 0)
		{
			ImGui.ProgressBar((float)(cur / (double)total), new Vector2(-1, 0), $"{cur / 1000.0:F1}s / {total / 1000.0:F1}s");
		}
		ImGui.Spacing();
		if (pl.IsPlaying && !pl.IsPaused)
		{
			if (ImGui.Button("暂停")) pl.Pause();
		}
		else
		{
			if (ImGui.Button("继续")) pl.Resume();
		}
		ImGui.SameLine();
		if (ImGui.Button("停止")) pl.Stop();
		if (pl.LastError.Length > 0)
			ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1), pl.LastError);
	}

	/// <summary>开始合奏:辅端播放器加载同一 MIDI 选辅轨(目标=辅端窗口)→ 两端同刻 Play(同进程,误差 <1ms)。</summary>
	public void Dispose()
	{
	}
}
