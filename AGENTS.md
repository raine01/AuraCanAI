# AuraCanAI.Dalamud — 项目 AGENTS.md(pi 助手自动加载)

FF14 卫月(Dalamud)插件:AuraCanAI 游戏内 AI 角色扮演助手(本地网页控制台 + TTS + DeepSeek LLM + 聊天记录 + 附近玩家/活点地图)。

> 详细知识笔记(架构、API 要点、踩坑记录):`~/.pi/agent/auracanai-triggernometry.md`(全局,任何会话可用)

## 项目信息

- 位置:`D:\AuraCanAI.Dalamud\`(独立 git 仓库;旧 `D:\AuraCanAI\` 目录由用户自行管理,不要读取)
- 目标框架:**net10.0-windows**,SDK `Dalamud.NET.Sdk/15.0.0`(国服卫月 XVLauncherCN)
- 插件命令:`/auracanai`;网页:`http://localhost:8051/`
- 构建:`dotnet build`(0 警告为标准)
- 安装测试:卫月设置 → Experimental → Dev Plugin Locations → 添加 `bin\Debug\`;改动后需**禁用再启用插件**生效

## 项目结构

| 文件 | 职责 |
|---|---|
| `Plugin.cs` | 入口;双按钮:OpenMainUi(打开→DashboardWindow)、OpenConfigUi(设置→MainWindow) |
| `Configuration.cs` | IPluginConfiguration;MessageSettingsJson/LlmConfigJson 存嵌套 JSON |
| `Models.cs` | 消息设置/LLM/频道/附近玩家/活点地图等数据模型(小写字段名,与网页 JSON 对应) |
| `Defaults.cs` | 默认频道表 + 默认角色(白屿涟音) |
| `Core/AuraCanAiCore.cs` | 核心:聊天处理/TTS/LLM/玩家进出与注视监视/附近玩家/活点地图数据/HTTP 方法 |
| `Core/HttpServer.cs` | 本地 HTTP+WebSocket(端口可调,默认 8051),静态页 + 静态资源(图片等) |
| `Core/TtsService.cs` | System.Speech(SAPI)中文播报,队列串行 |
| `Core/GameCommandSender.cs` | 发消息:Utf8String + ProcessChatBoxEntry(mes,0,false),主线程调用 |
| `Core/Behavior/*.cs` | 行为设置:Models(条目/条件/规则)、Parser(定义文本→规则)、Engine(500ms 求值+冷却+动作队列) |
| `Windows/MainWindow.cs` | 「设置」按钮控制面板:网页服务(端口/重启/检测)、TTS、消息发送、指令 |
| `Windows/DashboardWindow.cs` | 「打开」按钮主窗口「AuraCanAI」,双页签:附近玩家 + 活点地图 |
| `Web/*.html` | 网页 UI(index/setting/character/chat/nav/footer/help) |

## 当前功能状态(2026-08 更新)

### 游戏内面板
- 「打开」按钮 → **AuraCanAI 窗口**,四个页签:
  - **附近玩家**:场景玩家列表(名字@服务器、种族/性别、在线状态),状态过滤固定项(全部/希望组队/接受镶嵌魔晶石请求/角色扮演中/其他,按 OnlineStatus RowId 匹配),点击行复制名字,表格可滚动
  - **活点地图**:任意区域显示玩家位置;**自己金色点无名字**;**正在看我的人橘黄点**(500ms 注视检测);**其他玩家蓝点、名字浅灰**;显示区域固定 1:1 正方形;超出视野的玩家不绘制(不 clamp 到边缘);**查询框**:输入名字片段模糊匹配(多个用 | 分割),匹配到的点标橘黄(与正在看我的同色),地图外的查询目标贴边(clamp)显示
  - **回忆检索**:按角色名(部分匹配,不含区服)检索聊天记录 → DeepSeek 普通摘要提示词(非角色扮演)归纳大话题(≤10) → 点击主题查看该时间段全部频道对话 + 前后各10条;同名玩家合并并显示名字列表;发悄悄话=我方发给对方(日志 Speaker 是收件人),对话视图绿色高亮;先搜最近半年,无结果自动扩一年;最多取最近 2000 条/70K 字符;新查询释放上一查询内存(代数丢弃异步结果)
  - **玩家备注**:回忆检索左边页签,给玩家记多行备注(**绑定 ContentId 防改名**);仅面对面(在场)时可在「附近玩家」行内「添加/查看·编辑」按钮新建;检索框同时匹配 玩家名@服务器/备注内容;编辑区 = 大输入框(多行)+ 保存/删除(两段式确认)/关闭,查询与编辑同一 UI;`/aca note` 查/编辑当前选中玩家;已备注玩家在附近玩家列表名字前有 ★ 标记
  - **行为设置**:条件触发宏自动化(详见下方「行为设置」段)
- 「设置」按钮 → 控制面板(网页服务端口输入 + 重启/检测按钮、TTS 开关/音量/语速、复制聊天日志路径、测试发送)

### 活点地图坐标映射(两套)
- **住宅区**:固定房间比例(中心=世界原点,尺寸 S=40/M=50/L=60/公寓=40,由住宅代码后缀 i1/i2/i3/i4 判断)+ 中心十字参考线
- **非住宅区**:以自己为中心,显示周边 **40×40** 世界坐标区域(自己恒在面板中心)

### 房区判断(⚠️ 勿改回 ID 列表)
- 用 **TerritoryType.Bg 路径**:含 `/ind/` = 房屋内部(私人 S/M/L + 公寓,天然覆盖所有内饰版本);含 `/hou/` = 房区外部
- `_clientState.TerritoryType` 返回的是 **TerritoryType.RowId**(不是 Map.RowId;曾踩坑)

## 已回退/未做的功能(不要再做)

- **❌ 活点地图地图背景图**:尝试过两条路都放弃——(1) Lumina 读 `ui/map/{Map.Id}.tex`:**dev 版 Lumina 与国服 7.x 数据不兼容,FileExists/GetFile 对 ui/map 等大部分文件返回 null**(只能读 icon 等少数);(2) FFXIVClientStructs 读客户端小地图纹理(NaviMap MapImageAsset):签名解析器需 Private=false 用 Dalamud 内置版,且 NaviMap 拿不到。结论:背景图不可行,用浅土黄背景
- **❌ 叠加到游戏自带地图**:用户评估后放弃(依赖客户端 UI 结构、更新易失效)
- **❌ 房间边界框(S/M/L 各房型具体坐标)**:用户给了 L 房 10×10 坐标数据尝试,坐标系混淆后回退;现在住宅区只有中心十字,无房间框

## 关键技术约定(改代码前必读)

- **聊天事件**:`ChatGui.ChatMessage += (IHandleableChatMessage msg)`;用 `msg.LogKind`(XivChatType)、`msg.Sender.TextValue`、`msg.Message.TextValue`
- **判断自己发言**:优先 `SourceKind==LocalPlayer`,但小队消息常为 None;可靠方式 = 清洗名字后对比(`_objectTable.LocalPlayer.Name.TextValue` / `_playerState.CharacterName`)
- **⚠️ GetCleanName 必须过滤名字前缀图标字符**:`\ue0e1 \ue071-\ue073 \ue090-\ue097`(私用区,12 个)+ `★●▲♦♥♠♣` + 服务器名;漏掉会导致"自己发言被 TTS 播报/网页左右错位"(已踩坑)
- **回复悄悄话**:网页收到的 0D 消息带 `sender` 字段(回复地址);`SendTellJson` 用 `/t {name} {msg}`;国服 `/t` 强制要求 `名字@服务器` 格式,缺服务器名时自动补本地服务器名(`IPlayerState.HomeWorld`);跨服好友名字带 `@服务器` 用 payload 解析(`PlayerPayload.World.RowId` → World 表)
- **发消息(勿改回旧方式!)**:`Utf8String.FromString(msg)` → `UIModule.Instance()->ProcessChatBoxEntry(mes, 0, false)` → `mes->Dtor(true)`;必须在游戏框架线程(`IFramework.RunOnFrameworkThread`);**不要**操作输入框 IsActive、**不要** saveToHistory=true(会崩游戏);已移除 ACT/PostNamazu 方案,勿恢复
- **玩家进出/注视**:ObjectTable 轮询差异(500ms 定时器),回调必须包 `RunOnFrameworkThread`(否则 "Not on main thread!");玩家判断 `ObjectKind.Pc`,排除自己用 `GameObjectId`;注视者存 `_targetMePlayers`(playerId+name),`IsLookingAtMe(entityId)` 供活点地图用
- **附近玩家数据**:遍历 ObjectTable,`IPlayerCharacter`(命名空间 `Objects.SubKinds`)的 `Customize[0]`=种族、`Customize[1]`=性别、`HomeWorld`=服务器名、`OnlineStatus.RowId`=状态;状态中文名/RowId 反查用 `GetOnlineStatusId(name)` 缓存
- **ImGui**:命名空间 `Dalamud.Bindings.ImGui`(不是 ImGuiNET);TabBar/Table/DrawList 均可用
- **配置**:类实现 `Dalamud.Configuration.IPluginConfiguration`(Version 属性);配置文件在 `%APPDATA%\XIVLauncherCN\pluginConfigs\AuraCanAI.Dalamud.json`(首次保存后生成)
- csproj:必须同时设 `DalamudLibPath` 和 `AssemblySearchPaths` 指向 `$(AppData)\XIVLauncherCN\addon\Hooks\dev\`(SDK 默认国际服路径)
- **网页服务常驻**:插件加载即 StartWeb,无启停命令(已删 `/auracanai web|stop`);端口可在面板改,重启按钮生效;HttpServer 启动时加载 Web 目录 html + 静态资源(图片等,按 Content-Type 返回);帮助页支持 `{{webUrl}}/{{port}}/{{logPath}}` 占位符
- 发送失败处理:GameCommandSender 捕获异常返回 false;SendMessageJson 返回 {result:error};网页 chat.html 有 success 回调弹提示

## 宏触发功能(已实现,2026-08)

**功能**:代码直接执行游戏内宏列表 0-99 号(路线 B,不走热键栏)。游戏宏编号为 0-99(有 0 号宏)。

**实现**(`Core/MacroExecutor.cs`):
- 链路:`UIModule.RaptureMacroModule(偏移 0x61B0).GetMacro(set, index)` 取宏数据 → `UIModule.RaptureShellModule(偏移 0xB9B30).ExecuteMacro(macro)` 执行
- 游戏原生宏执行器逐行执行,完整支持 /wait、占位符、条件;MacroLocked 检查防重复触发,空宏不执行,异常捕获不崩游戏
- UIModule 的这两个字段是 internal 不可直接访问,用反射验证过的固定偏移(FFXIVClientStructs 7.51.0.8870 布局)
- 参考源码:`D:\AuraCanAI\_ref\FFXIVClientStructs`(gitignore 不入库)
- `MacroExecutor.IsBusy()` 读取 MacroLocked,供行为引擎队列泵送判断执行器忙

**用法**:
- `/aca macro N`(0-99 个人宏);`/aca macro sN` 共享宏
- `/aca look [名字]`:看向指定玩家;不带名字 = 看向最后一个看我的人(`core.GetLastLookUser()`,最后注视你的玩家;为空提示“还没有人看过你”);内部走 `core.TryLook`(设置 TargetManager.Target,自动切框架线程)
- `Plugin.TriggerMacro(index, shared)` 公共 API,供面板/网页/AI 层调用(自动切框架线程,须登录)

## 行为设置(已实现,2026-08)

**功能**:主面板第四个页签「行为设置」,用户定义「条件满足时自动执行动作」规则(动作:触发宏 / 指定频道发言 / 选中目标)。

**语法**(关键字/操作符英文,值中文;多段用半角分号 ; 分割,中文全角分号 ; 不支持会报错):
```
trigger 2 after 5 when area = 穹顶皓天私人别墅 and my_status = 角色扮演中 and not in_party cooldown 20
trigger 1,2,3 after 3,6 when looking_player = 龙尾轻轻摇 not in_combat cooldown 30
say p 欢迎{last_enter}来到我家! when anyone_enter
say t 你好呀 when anyone_looking cooldown 10
say t 龙尾轻轻摇@红玉海 在吗? when in_housing
```
- 动作(when 前只能有一个,**三选一**):`trigger N`(0-99)/ `trigger sN`(共享宏)触发宏,**多个宏用逗号分割**(`trigger 1,2,3` / `trigger s1,2`,支持全角逗号),执行完前一个再执行下一个;`say 频道 内容` 在指定频道发言(频道简写 = 命令去 /:s/sh/p/a/y/b/fc/cwl1~8/t/r/em,表见 BehaviorSyntaxDoc.SayChannels;内容可用 {变量} 引用玩家名;**悄悄话 t 目标:内容开头 名字@服务器(纯名字,不含变量)发给固定人,否则默认 last_tell**);或 `look [名字]` 选中目标(不带名字 = **最近接触用户**)
- **发言变量**(共 6 个,见 BehaviorSyntaxDoc.SayVars):`{last_tell}`(最后悄悄话)/`{last_look}`(最后看你)/`{last_enter}`(最后进入附近)/`{last_contact}`(最新接触,悄悄话/注视/入场/表情任一)/`{emote_player}`(刚对你做表情的)/`{emote_name}`(表情名);替换为清洗名;**引用的变量为空 → 本次发言跳过**(日志警告;勾选聊天内提示时 /e 提示跳过原因),避免发半截话;未知变量/非法频道解析时直接报错(悄悄话无目标不再报错,执行时兜底 last_tell,兜底也为空则跳过)
- 最近接触变量(core 维护 4 个,均存清洗名):`_lastTellUser`(悄悄话 0C/0D)、`_lastLookUser`(新增注视者)、`_lastEnterUser`(新玩家进入附近列表)、`_lastContactUser`(三者任一最新,look 动作用);**变量中的人离场时清空对应变量**(ClearContactIfMatch,在玩家离开附近列表处),切换区域时全部清空(OnTerritoryChanged);getter:GetLastTellUser/GetLastLookUser/GetLastEnterUser/GetLatestContactUser
- 延迟:`after N` 秒后执行(不占队列);**`after A,B` = A~B 秒随机延迟**(调度时取一次随机值,支持小数如 after 0.5,2;全角逗号亦可);look 或 after>0 走引擎 _delayed 延迟列表,到期直接执行;trigger 无延迟走 3个/5秒 队列;**say 无延迟直接执行**(不占宏队列)
- 连接:`when and or not`;**独立 `not` 词也作为隐式 and 分割**(`looking_player = 龙尾轻轻摇 not in_combat` = 三个条件,不必写 and);⚠️ **not 作为连接词时其后条件必须取反(已修):曾 bug——not 被 ConnRe 剥离后 in_party 变成正条件,导致 `not in_party` 实际=in_party,行为方向与定义相反**(症状:单人时 not in_party 条件不满足、组队反而满足;IsInParty 数据本身是对的,勿再改它);比较:`= != >= <=`(旧 `==` 语法兼容,值前多余的 = 自动去掉;contains 已移除)
- 冷却:`cooldown N` 秒,默认 20,0=无冷却(**必须无等号**,如 `cooldown 30`;`cooldown = 30` 会报错——曾踩坑,cooldown 关键字存在但格式不对时直接报错,避免混进条件值);触发 = 边沿触发(不满足→满足时一次)+ 冷却期内不重复
- **「离开时不触发」「战斗中不触发」**(BehaviorItem.skipOnLeave / skipOnCombat,均默认勾选):我的状态为「离开」(IsPlayerAway,OnlineStatus 中文名 == "离开")或战斗中(IsInCombat)时该行为直接丢弃(不触发、不设冷却、状态照常更新);丢弃时日志 + (勾了聊天内提示) /e 提示原因
- 条件库:`looking_player`(看我的玩家名,=/!=)、`anyone_looking`、`looking_player_count`(数值)、`area`(PlaceName 中文名)、`in_housing`、`room_size`(S/M/L/公寓,取住宅代码首字母)、`my_status`(OnlineStatus 中文名)、`in_party`(FFXIVClientStructs GroupManager,国服无 IGroupManager 服务)、`my_job`(ClassJob 表中文名,失败回退 Abbreviation)、`nearby_player_count`(数值)、`target_name`(当前选中目标)、`time`(现实时间 HH:mm,>=/<=)、`anyone_emote_to_me`(有人刚对我做表情,布尔)、`emote_to_me_player`(刚对我做表情的玩家名,=/!=)、`emote_to_me_name`(表情名,游戏内中文名如 摸头,=/!=)

**实现**:
- `Core/Behavior/BehaviorParser.cs`:定义文本 → 规则(纯逻辑,无 Dalamud 依赖);`BehaviorParseResult.Errors` 含段号,UI 红字显示;trigger 宏列表按逗号解析(每项 s?N,0-99 校验),after 支持 A,B 区间(校验 A≤B、非负,残缺写法如 after 3, / after 3,x / after when 给出专属报错,避免误报成宏编号错误);say 解析(频道/内容,频道表+变量表以 BehaviorSyntaxDoc 为单一数据源,未知频道/未知变量均报错);**BuildSayText(纯静态):变量替换 + 空变量跳过 + t 目标解析(内容开头 名字@服务器(不含变量)否则默认 last_tell),引擎仅传变量值回调**
- **AI 生成行为命令**(`AuraCanAiCore.GenerateBehavior` → DeepSeek function calling 调 generate_behavior):**结构化模式(2026-08 重构,推荐路径)**:tool 参数为 `{comment, rules[]}`,每条 rule 含 action/macros/channel/text/target/after/afterMax/cooldown/when(条件数组)/connectors/need;程序用 `BehaviorParser.AssembleRule` **拼装规范文本**(动作 → after → when → need → cooldown),LLM 只填值不碰语法 → 杜绝文本乱序/拼错/条件名拼错(拼装时校验动作/宏 0-99/频道/条件名(支持去下划线模糊匹配)/数值/时间,任一规则失败整体报错重试)。**兼容回退**:LLM 未按结构返回时走旧 definition 文本路径 → `FormatBehaviorDefinition`(分号与换行都视为规则分隔,逐段 `NormalizeSegment` 按规范顺序重建,解析失败保留原文由保存时报错)。**勿改回宽度折行**(曾踩坑:折行把条件值拦腰截断,如 emote_to_me_name = 抚摸 被折成 抚⏎摸,保存报"无法解析";单条长规则在输入框内横向滚动即可)
- `Core/Behavior/BehaviorEngine.cs`:500ms 轮询(挂 core 的 _timer500),边沿触发+冷却;动作调度(Schedule:say 直接执行(带 after 走 _delayed),look/after 走 _delayed 延迟列表,多宏 trigger 入 _chains 连发链,单宏 trigger 无延迟走 _queue);**多宏链**(MacroChain):执行完前一个(等 MacroExecutor.IsBusy 释放)再执行下一个,间隔 ChainGapMs=300ms,单宏失败不阻塞,300 秒防卡死;**_macroFiredThisTick 全局限制每 tick 最多触发一个宏**(延迟/队列/链共用,防同帧连发);ExecuteAction 分发 Trigger(单宏直接触发/多宏入链)/Say(ExecuteSay:BuildSayMessage → BehaviorParser.BuildSayText(变量替换/空跳过/t 目标) → core.SendBehaviorSay)/Look(选中目标),触发日志留痕;条目勾选「聊天内提示」→ /e 提示(发言成功/跳过/失败均有提示)
- `Core/AuraCanAiCore.cs`:TryLook/FindPlayerByName(清洗名精确匹配,IGameObject 在 `Dalamud.Game.ClientState.Objects.Types` 命名空间)/GetLatestLookingPlayer 供 look 动作使用;**SendBehaviorSay(简写→cmd:优先当前 channelConfig 配置,兜底 SyntaxDoc.SayChannels)→ RunCommand 发送**
- `Core/Behavior/BehaviorModels.cs`:BehaviorItem 用 public 字段(ImGui ref 绑定)+ 每字段 [JsonProperty](Json.NET 不默认序列化字段)
- 持久化:`Configuration.Behaviors`(List<BehaviorItem>),UI 增删改/启停 → `core.SaveBehaviors()`(保存 + 引擎 Reload,Reload 重置运行时状态)
- UI:`Windows/DashboardWindow.cs` 第四页签,列表 = 序号(自动分配,删除复用最小空缺)/注释/启停勾选/编辑按钮;编辑区**内联在列表上方**(与回忆检索同交互,勿用 BeginPopupModal/OpenPopup —— 本 ImGui 绑定下 popup 打开无效,已踩坑回退),内容 = 注释 + 多行定义 + 聊天内提示 + 保存/删除/取消;保存失败红字;编辑区勾选项顺序:**「离开时不触发」「战斗中不触发」(均默认勾选)在「聊天内提示」前面**
- 条件数据源在 `AuraCanAiCore.cs`(GetPlayersLookingAtMe/GetAreaName/GetMyStatusName/GetMyJobName/GetRoomTypeName/IsInParty/GetNearbyPlayerCount/GetTargetName/ChatNotice)
- **表情触发条件(anyone_emote_to_me / emote_to_me_player / emote_to_me_name,2026-08 新增)**:`ScanEmotes()`(500ms,框架线程,挂 CheckLookingAndPlayers 末尾)扫附近玩家 `BattleChara* → EmoteController`(`Character+0x630`:EmoteId @+0x14 ushort、Target.ObjectId @+0x18 uint;FFXIVClientStructs 7.51.0.8870 布局,勿手写偏移,直接结构体访问)。严格判定「对我做」= `Target.ObjectId == LocalPlayer.GameObjectId`;边沿检测:EntityId→上次 EmoteId 字典(`_lastEmoteId`),EmoteId 从 0/其他值变为新值记一次事件(首次见到只记录不触发;循环表情只记开始);事件记 3 秒窗口(`AnyEmoteToMeRecently`),同时更新 `_lastEmoteUser/_lastEmoteName/_lastEmoteTime/_lastContactUser`;表情名取 Lumina `Emote` sheet 中文名(EmoteId 即 RowId,失败回退 `#编号`)。**仅系统内置表情播放动画,`/em` 自定义文本宏不播放动画天然不触发;但宏里写系统表情命令(如 /摸头)同样播放动画会触发(游戏层无法区分,已向用户说明)**。⚠️ 待实机验证:`Target` 是否在表情菜单对目标使用时正确写入。离场清除/切区域重置已覆盖。
  **⚠️ 语义坑(实测踩坑)**:`emote_to_me_player`/`emote_to_me_name` 是**持续状态**(记录最后事件,不自动复位),行为引擎边沿触发 → 单独使用只在状态从不满足→满足时触发**一次**,之后 WasTrue 保持 true 不再触发(症状:第一次生效、之后每次摸都没反应)。**必须搭配事件窗口 `anyone_emote_to_me`(3 秒)**:如 `when anyone_emote_to_me and emote_to_me_player = X and emote_to_me_name = 抚摸`。AI 生成提示(BehaviorSyntaxDoc.BuildGenPrompt)已内置此规则;勿把 emote_to_me_* 改成自动复位(发言变量 {emote_player}/{emote_name} 需要保留最后事件信息)。
- `/aca behavior` 打开该页签

⚠️ 国服 Dalamud.dll 无 `IGroupManager` 服务,小队判断用 FFXIVClientStructs GroupManager。**✅ 实测确认(2026-08 /aca party):单人时 `MainGroup.MemberCount=0`、`PartyId=0`(不在队),故 `MemberCount > 0` 即为正确判断**(`AuraCanAiCore.IsInParty`)。⚠️ 踩坑过程:曾误以为单人 MemberCount=1 而改成遍历 PartyMembers 排除自己、后又取反,均错误——**以实测数据为准,勿再改**。调试:`/aca party` 输出 MemberCount/PartyId/成员 EntityId/ContentId/判定

## 频道号映射

XivChatType 十六进制两位 = 频道号:0A=说话 /s、0E=小队 /p、18=部队 /fc、1C=原创动作、1D=情感动作、25=跨服贝1、65-6B=跨服贝2-8;0C=发悄悄话(自己)、0D=收悄悄话(他人);io=出入场、zs=注视(内部配置频道)

## MIDI 演奏(阶段 1,2026-08)

**功能**:主面板第五个页签「演奏」(`/aca music`)导入 MIDI 自动演奏(单角色)。参考 Daigassou 架构(其 GPL v2 仅借鉴思路,**代码全部自行编写,勿复制其文本**;参考笔记 `(本地私有路径,已移除)`)。

**实现**:
- `Core/Midi/MidiKeyMap.cs`:pitch 48~84 → 虚拟键码表(FF14 演奏键位事实数据,与 Daigassou 一致);`IsPlayablePitch`/`GetVk`
- `Core/Midi/MidiPlayer.cs`:MidiFile.Read(容错 ReadingSettings)→ 过滤空轨道列表(轨道名取 SequenceTrackName)→ SelectTrack 重建 Playback → Play/Stop/速度/进度;EventPlayed(NoteOn/NoteOff)→ **PostMessage(WM_KEYDOWN/UP)到当前进程主窗口**(`Process.GetCurrentProcess().MainWindowHandle`,后台亦可,线程安全);停止/结束 ReleaseAllKeys 防卡键;超出 48~84 音域忽略(八度处理后续做)
- 依赖:**Melanchall.DryWetMidi 6.1.3(MIT)**(Playback/ManageNotes/Note 等;SevenBitNumber 在 `Melanchall.DryWetMidi.Common` 命名空间,AddNotes 不存在用 ManageNotes.Objects.Add)
- UI:`Windows/DashboardWindow.cs` 第五页签 DrawMidi(路径输入 → 加载 → 选轨 → 演奏/停止/速度/进度);_forceTab 上限已改 4
- 命令:`/aca music`(或 /aca midi)打开演奏页签

**阶段 3 已做(2026-08)**:双开合奏(最终方案:卫月端直接向两端窗口注入,辅端零软件):
- `MidiPlayer.TargetHwnd` 属性:0=当前进程主窗口,非 0=指定窗口(跨进程 PostMessage,Daigassou 同机制);`AuraCanAiCore.GetOtherFfxivWindowHandle()` 返回辅端游戏窗口句柄(同机双开唯一)
- ⚠️ **GetOtherFfxivWindowHandle 必须用 EnumWindows 按类名 `FFXIVGAME` 查找(排除隐藏窗口)**,不能用 `Process.MainWindowHandle`——该 API 有内部缓存,双开时会返回过期/失效句柄(已踩坑:实测返回已销毁窗口,PostMessage 静默失败)
- 演奏页合奏区:主轨(自己 _midiPlayer)+ 辅轨下拉(排除主轨)+ 辅端窗口定位状态灯 + 合奏开始/停止;**合奏开始 = 辅端播放器(_slavePlayer)加载同一 MIDI 选辅轨,TargetHwnd=辅端窗口,参数(速度/预处理/移调)与主端同步,两端同刻 Play(同进程同 UI 线程,误差 <1ms)**
- **废弃方案(已删)**:注入器 exe(`AuraCanAI.EnsembleSlave`,已删除目录)、HttpServer `/ensemble` WS 端点(已回退移除)——辅端不需要装卫月/任何软件,纯原版客户端
- ⚠️ 待真实双开验证:辅端窗口需可见(PostMessage 到最小化窗口无效)

**⚠️⚠️ UIPI 权限隔离坑(2026-08,真实双开验证后定位)**
- **现象**:合奏时主端(卫月)自己正常演奏,辅端(第二个客户端)完全没反应
- **根因**:Windows UIPI 禁止低完整性进程向高完整性进程窗口 PostMessage(实测 PostMessage 返回 False、GetLastError=5)。主端(XIVLauncherCN 启动的游戏)完整性与辅端(盛趣登录器 sdologin 启动,稳定 Medium)不一致时即被拒
- **⚠️ 用户环境特殊性**:整个桌面会话本身以**低完整性**运行(explorer=Low)!所以 XIVLauncherCN 的权限取决于启动环境,普通启动可能 Low 可能 Medium,不稳定(实测反复出现 Low/Medium 两种状态)。辅端盛趣登录器启动为稳定 Medium
- **✅ 稳定方案(推荐,已实测)**:主端 XIVLauncherCN **以管理员身份运行**(桌面「XIVLauncherCN(管理员)」快捷方式,RunAs 位已置;每次弹 UAC)→ 主端游戏 High → High>Medium UIPI 必然放行,与启动环境无关。管理员只是备选时:**High→中放行,中→中放行,低→中被拒**
- 诊断方法:PostMessage 到目标窗口返回 False 时用 GetLastError(1400=句柄失效,5=UIPI 拒绝);进程完整性读 token SID(S-1-16-8192=低/12288=中/16384=高);低完整性进程无法自我提升
- 帮助页(help.html)「演奏」段已写:合奏建议管理员启动卫月端

**✅ 已实测(2026-08)**:加载 MIDI → 选轨 → 演奏完整跑通,PostMessage 注入方案在国服 7.x 有效(角色持乐器进演奏模式即可)。踩坑记录:
- DryWetMidi 原生库 `Melanchall_DryWetMidi_Native64.dll`(高精度时钟 P/Invoke)不会自动搜插件目录 → **MidiPlayer 静态构造按 `Plugin.PluginInterface.AssemblyLocation` 目录 `NativeLibrary.Load` 预加载**(csproj 也需把原生 DLL 复制到输出根,<None Link> 方式);首次踩坑后加日志定位
- forceSel 数组长度必须 = 页签数(当前 6:0附近玩家 1活点地图 2玩家备注 3回忆检索 4行为设置 5演奏;加页签时同步改 Plugin.cs 里 OpenTab(n) 与 forceSel,否则 /aca music 切页签越界 IndexOutOfRange)
- **Note 构造参数顺序是 (noteNumber, length, time)**!(DryWetMidi 6.x)新建/Clone Note 时勿写成 (note, time, length)

**阶段 2 已做(2026-08)**:`Core/Midi/MidiPreprocessor.cs`(纯逻辑可单测):噪声清理(<10ms)→ 同音连奏缩短(留 MinEventMs=10ms)→ 和弦拆解(同 StartTime 多音按时间均匀铺开);SelectTrack 时对音符**拷贝**后预处理(不改原轨道)。**两个独立开关,均默认关**(FF14 特制 MIDI 已处理好,开箱即用;普通 MIDI 用户手动开):`UseAnalysis`=噪声+连奏,`UseChord`=和弦拆解,开关变化自动重建 Playback。移调:`MidiPlayer.Transpose`(±24 半音,UI 滑条),注入时 NoteNumber+移调并 **ClampToPlayable 自动八度搬移**进 48~84(超出向上/下八度)。测试:开关 4 组合/噪声/和弦/连奏/时间保持全部 PASS(注意 TempoMap.Create 需带 TimeDivision 否则 TPQN=0 时间换算全 0)

## 歌单播放(2026-08)

**功能**:网页「歌单」页(`/playlist.html`,手机/电脑浏览器均可)维护多歌单,MIDI 演奏全部移到前端;卫月端演奏页大幅简化(仅状态+基本控制)。

**数据/引擎**:
- `Core/Playlist.cs`:`Playlist{Name,Items}` / `PlaylistItem{Path,FileName,Analysis,Chord,Speed,Transpose,Ensemble,MainTrack,SlaveTrack}`;持久化在 `Configuration.Playlists`(public 属性自动保存),`core.GetPlaylists/SavePlaylists`
- `Core/PlaylistPlayer.cs`(core.Playlist):主/辅双 MidiPlayer(合奏曲目辅轨注入辅端窗口 `GetOtherFfxivWindowHandle`);模式 Single/Loop/List/Random;暂停(Stop 断点)/继续(Start)/停止/上下首;`Main.Finished` 自动下一首(内部线程触发);`GetStatusJson()` 含进度/模式/错误/**perform 就绪状态**(isBard/isPerforming/hint)
- `MidiPlayer` 新增:`Finished` 事件、`Pause()`(不 MoveToStart)/`Resume()`(断点续播,Playback.Stop→Start 语义)

**Web API**(HttpServer 注册):GetPlaylists/SavePlaylists/PlaylistPlay/PlaylistControl/PlaylistStatus/ListMidiFiles(列目录 mid)
- 前端 `Web/playlist.html`:歌单增删改/曲目增删排序/每曲参数(预处理/拆解/速度/移调/合奏)/播放控制+模式/进度轮询(500ms)/演奏就绪提示;`nav.html` 加「歌单」入口

**卫月端演奏页**(DrawMidi 重写):引导文案 + 演奏就绪检测(职业=吟游诗人 + `ConditionFlag.Performing`(注意枚举名是 **Performing** 不是 Performance,卫月15 Dalamud.dll)) + 当前曲目/进度条 + 暂停/继续/停止 + 错误显示;旧路径输入/选轨/速度等 UI 全部移除

**手机访问**:❌ **已回退(2026-08)**:曾改为优先绑定 `http://+:端口/`(局域网)失败回退 localhost,但导致网页打不开,已恢复只绑定 `http://localhost:端口/`(仅本机访问)。若以后要手机访问,再单独处理(需验证 urlacl/防火墙/端口占用兼容性)

**⚠️ 限制**:辅端(纯原版)角色状态跨进程不可读,演奏就绪检测仅覆盖主声部(自己);辅端只能人工确认

**歌单页后续迭代(2026-08,均在 playlist.html + 后端):**
- 默认歌单:无歌单时自动创建「默认歌单」(后端 GetPlaylists 兜底 + 前端删最后一个歌单后立即补建)
- 歌单改名按钮;保存防抖 250ms(连续操作只发一次全量保存,更流畅)
- 播放/暂停合并为一个按钮(未播→从头,播放中→暂停,暂停→继续断点续播);删除了独立的继续/停止按钮
- 进度条改为可拖动 range:拖动中本地显示(轮询不覆盖),松手 seek(后端 PlaylistPlayer.Seek → MidiPlayer.Seek → Playback.MoveToTime,合奏主辅两端一起跳)
- 曲目行:点击=选中(绿色 .track-item.sel,将演奏);播放中轮询同步 selectedIdx=后端 index(绿色跟随自动下一首);播放按钮从选中曲目开始;播放中点击另一首先 stop 再等播放;行内按钮(上移/下移/删除)用 event.stopPropagation 防误触;参数区 param-row 也 stopPropagation(否则 checkbox 点击冒泡到行触发 selectTrack 重渲染导致勾选失效——已踩坑)
- 播放控制区显示「将演奏: N. 文件名」(未播放) / 「演奏中: 文件名」
- **合奏选轨**:勾选合奏后前端异步 GetMidiTracks(后端:临时 MidiPlayer 解析返回轨道名)显示主轨/辅轨下拉(MainTrack/SlaveTrack,-1=自动:主轨0/辅轨1);单轨时辅端不演奏(后端 TrackCount<=1 跳过辅播放器 + 前端辅轨下拉显示「仅一条轨道,辅端不演奏」)
- **添加MIDI 模态框**(addMidiModal,替代原折叠面板+目录选择器):标题「添加MIDI」,显示目标歌单名,目录浏览(.. / 文件夹 / MIDI 文件),点击文件即加入且不关框可连续添加(绿色提示「已添加」),「完成」关闭;移除「列出」按钮/手动路径输入;midiDirCache 记忆上次目录
- ⚠️ 前端每次改动后需 dotnet build(Web 文件复制到 bin)再重载插件;改动多为 playlist.html 单文件

## 版本记录

| 日期 | 版本 | 说明 |
|---|---|---|
| 2026-09-04 | 0.1.0.0 | **本次记录基线**:csproj `Version=0.1.0.0`;bin/Debug 产物 `AssemblyVersion=0.1.0.0`,`DalamudApiLevel=15`,SDK `Dalamud.NET.Sdk/15.0.0`(net10.0-windows);bin DLL 构建时间 2026-08-16 12:07(此后网页前端 playlist.html 等有改动但未 build);功能面:面板 6 页签(附近玩家/活点地图/玩家备注/回忆检索/行为设置/演奏)、行为引擎、宏执行、MIDI 歌单/双开合奏。**git 仓库 0 commit**,唯一备份 `(本地备份,已移除)`(2026-08-23)。 |

## 移动(Phase 1 骨架,2026-09-04 新增,待实机验证)

**目标**:人设驱动 RP 机器人的"身体"拼图——LLM/行为引擎可指挥角色走近/跟随/走开/走到点。本阶段只做内核+命令,验证移动注入可用后接行为引擎与 LLM。

**参考**:vnavmesh(awgil/ffxiv_navmesh,已克隆 `_ref/ffxiv_navmesh`,主库)+ 国服移植版(`_ref/tc-port-vnavmesh`,tc-7.20 分支)。**两版移动签名完全一致**(RMIWalk/RMIFly/IsInputEnabled);tc-port 版加了大量健壮性(签名 Fallible 降级、detour try/catch、日志节流)——照抄其思路,代码自写。**csproj 已排除 `_ref/**` 不参与编译**。

**实现**(`Core/Movement/`):
- `MovementOverride.cs`(底层):hook RMIWalk(地面)/RMIFly(飞行),在玩家没按键且游戏允许读输入(两个 RMIWalkIsInputEnabled)时把输入方向覆盖为朝 `DesiredPosition`。签名与 vnavmesh 相同;walk 可用=两个 IsInputEnabled 都扫到,缺一放弃 walk;签名全失效→降级"移动不可用"(绝不崩插件)。legacy 模式参考朝向=相机 DirH+180,相机 API 不可用回退角色朝向。防挂机:ResetAfkTimers 清 UIModule 输入计时器。hook 常驻+Active 开关(不做 Enable/Disable 抖动)。
- `MovementController.cs`(高层):意图状态机 Approach/Follow/Leave/MoveTo(每帧 Framework.Update 驱动,目标玩家名每帧重解析);到达条件=平面距离(Approach 社交距离默认 3m 后 **TryLook 选中目标→角色自动面向**);卡住检测(0.15m/s 以下持续 MovementStuckTimeoutSec=3s → Stuck);用户按键即打断(默认开);护栏=未登录/过场/BetweenAreas/演奏/骑乘/任务事件/战斗(可选)/回放;单次最长 90s;切换区域中止。完成时 `MovementFinished` 事件(MoveResult)。
- 配置(Configuration.cs `Movement*` 字段):社交距离 3m/跟随保持 3m(+1.5 余量)/走开 8m/追距上限 60m/卡住 3s/阻断等待 8s/总开关等。
- 命令:`/aca face|approach|follow|leave [名字]`(缺名字=最近接触的人)、`/aca move x y z`(或当玩家名=走近)、`/aca stop|movestat|movediag`。
- 依赖服务新增注入:Plugin 增加 `ISigScanner/IGameInteropProvider/IGameConfig` 三个 [PluginService] 并传入 core(AuraCanAiCore 构造签名已扩展)。

**⚠️ 待实机验证**(无法在游戏外测试,签名/坐标约定需真机确认):
1. `/aca movediag` 应为 walk 可用=true(失败把原因发我);
2. 房内两人测试 approach/follow/leave/面向(面向=选中目标,需确认角色会转向对方);
3. 卡墙 3 秒应结束并提示"走不过去了";
4. 移动中按键应打断;**若方向反了(往目标反方向走)或根本不走,把 movediag + /xllog 里 [移动] 行发我**;
5. legacy(传统移动)模式验证单独做——参考朝向走相机路径,若走歪优先排查。

**后续规划**(验证通过后):Phase 2 行为引擎加 move/approach 动作 + distance_to 条件;Phase 3 LLM 身体演出模式(场景注入 + function calling 动作决策 + 到点接台词)。防卡死/表现层(走路步速等)后补。

## LLM 身体演出模式(Phase 2+3,2026-09-04,待实机验证)

**Phase 2 - 行为引擎移动动作**(已实现,与触发/发言同一套语法与 AI 生成):
- 动作扩为六选一,新增 `approach [玩家名]`(走近社交距离并面向)/ `follow` / `leave`(走开),缺名/空变量=最近接触的人;名字可用 {变量}(如 approach {last_enter})。移动动作不进宏队列,与 look 同调度(after 走延迟);执行结果由 MovementController 统一日志。
- 目标必须是**当前在场玩家**(FindPlayerByName 实时解析,每帧刷新,人走丢→NoTarget 结束)。
- AI 生成(AssembleRule/BehaviorSyntaxDoc/AuraCanAiCore tools schema)同步支持 approach/follow/leave;提示强调:不能对地点/不在场的人用,不是传送/寻路。
- 语法数据源仍在 BehaviorSyntaxDoc(条件/频道/变量一处改全同步;帮助页自动);条件库本轮未加 distance_to(近距离判定靠 anyone_enter 等事件即可)。

**Phase 3 - LLM 身体演出模式**(核心交付,默认关):
- 无开关:选了人设(currentRole 的 setting 非空)即自动进入身体演出(2026-09-05 起,曾用 LlmBodyMode 开关已删);无人设 = 纯文字。
  - 动作枚举 approach(走近)/follow(跟随)/leave(走开)/face(转身面向)/stop;target 缺省/空 = 最近接触的人;失败自动降级目标或给中文失败描述;
  - 台词正常发送(跟随来源频道,见采集与身份链路契约)+ 入历史;
  - 模型只动作没台词 → 自动补一轮"输出台词"请求(allowTools=false),避免冷场;
  - 动作绝不写进台词(与 OutputFormatRule 互补:格式规则管文本,动作走工具调用)。
- **场景注入**(每轮请求插入 system,位置=最近用户消息前):地区名 / 在场玩家(≤5,距离米,正在看你优先排序) / 你的移动状态(StatusText) / 25 秒内刚结束的移动结果(MovementFinished 事件存 _lastMoveResult)。注入内容用中文口语,且末尾附"要移动/面向请调 rp_body_action,动作绝不写进台词"。
- 动作执行线程:ExecuteBodyAction 用 RunOnFrameworkThread 编组;更换动作先 Stop 旧移动。
- 移动结果记录:core 订阅 Movement.MovementFinished → _lastMoveResult/_lastMoveResultAt(下次请求注入让模型知道刚才走到/卡住了)。

**⚠️ 待实机验证要点**:
1. 找人来对你说"来我这边"/喊你名字 → 看模型是否:能识别在场玩家名 → 调 approach 走到 3 米并面向 → 说话;/xllog 看 BODYREQ/BODYRSP(工具调用是否出现,动作枚举/名字是否正确);
2. 若模型频繁乱走/过度动作:可在人设设置里补一句"行动克制,只有情境需要才动";若从不动作:检查人设是否含动作倾向描述(纯文字人设可能不带动作意图),必要时在人设里写"你是活的,可以用动作回应";
3. 若某动作目标名在场景里(不同服务器显示名差异)→ 模型应使用注入的在场名单(注意名字@服务器跨服场景);
4. 结束后台逻辑:face 只是 TryLook(选中→自动面向),不移动;stop 立即停;被家具挡/走丢由 MovementController 结束并注入给模型;
5. 若 DeepSeek 对 tools 支持异常(返回格式怪)→ 检查 BODYRSP 日志,可回退关掉身体模式。

## 采集与身份链路契约(2026-09-04,勿破坏)

- **LLM 采集 = 玩家在「消息设置」勾了「LLM 采集」的频道**(channelConfig.llm,按频道号匹配)。`OnChatMessage → ChatLLMHandler` 第一行 `if (!GetChannelSwitchs(channelNo).llm) return;`。普通文字模式与身体演出模式共用此链路,过滤一致。自己发言在勾选频道内以 assistant 身份进历史(上下文)。
- **人设 = `LLMConfig.currentRole` 指向的 Role.setting**(当前选中角色)。`ResetChatHistory` 按该角色生成 system;切角色=重置对话用新人设。身体演出模式仅当 currentRole 有人设(setting 非空)才启用。
- **回复频道 = 跟随来源频道(2026-09-04 已改,勿改回 /e//s 兜底)**:assistant 台词按触发消息的来源频道回发——说话类走该频道配置的 cmd(0A→/s、0B→/sh、0E→/p、0F→/a、1E→/y、18→/fc、1B→/b、跨服贝 25/65-6B);收悄悄话 0D 特判 → /t 回复地址(名字@服务器)。频道无对应命令(如情感动作)→ 不回复。禁止任何 /s 默认兜底;禁止回 /e 默语(仅自己可见,对方看不到)。模型不选择频道;来源频道由 OnChatMessage→ChatLLMHandler→SendMsg 逐条携带。
- **情感动作 1C/1D(他人) = 仅上下文**(2026-09-04):进历史(SendMsg triggerReply=false),不触发回复请求——让模型知道谁做了什么即可;不回话。
- **回复节奏(2026-09-05 重写)**:静默延迟调度(见文末 2026-09-05 段),不再有 15s 丢弃式 CD;攒多条一起回;频道跟随最后一条触发消息。勿按旧理解使用。
- 场景注入(在场玩家/移动状态)只作模型"眼睛",不受频道开关影响;回不回应谁仍由频道采集决定。

- ⚠️ **tool_calls 解析坑(2026-09-05 实机定位)**:动作枚举(action/target)**在 `function.arguments` 的 JSON 里**,不是 tool 名。曾错把 tool 名 `rp_body_action` 当动作 → 白名单判断不认 → 动作从不执行,且"纯动作无台词"轮因 actedDesc=null 连补台词请求都不发(症状:模型想走近/面向但角色纹丝不动、日志无任何移动记录、纯 face 时后续无请求)。修复:从 arguments 解析 action/target。日志定位法:BODYREQ 正常、无 移动开始/身体动作失败 → 查是否 act 拿到的是 tool 名。

## 2026-09-05 交互节奏与移动限制调整(已实机简单验证)

- **回复节奏改为拟真延迟调度**(替代旧 15s 丢弃式 CD,字段 LlmReplyCooldownSec/LlmBodyMode 已删,勿再用):
  - 收到触发消息 → 进"待回"(NotifyChatTurnPending);对方连续打字不断顺延(只更新最近消息时间,不重复抽随机);
  - 静默满随机 2~5s(`LlmReplyDelaySecMin/Max`,首次收到时抽一次)或从首条算起超过 `LlmReplyMaxWaitSec`(12s,对方说个不停强制插话) → FireReply;
  - FireReply 单飞行(_replyBusy),回复期间新消息继续攒;攒了多条(manyMsgs>1)时在请求里插 system"对方连续发了几条,当成一件事自然回应,勿逐条复述"(文本与身体模式都有,见 BuildRequestMessages / SnapshotHistoryWithScene);
  - 调度泵在 500ms tick 的 ReplyTick()(框架线程);回复频道=最后一条触发消息的频道(replyAddress 同步)。
  - ⚠️ 行为变化:之前"每次说话立即回"现在会有 2~5s 静默才回(正常,是特性);回复 CD 概念已消失,勿在代码里找 ReplyGate。
- **身体演出不再有开关**:移除 `/aca bodymode` 与 Configuration.LlmBodyMode;RunChatTurnAsync 里 `role.setting 非空(选了人设)= 身体演出(可动作移动)`,无人设=纯文字。⚠️ 意味着选任何带设定的人设都会自动获得"会动"能力,做测试/挂机人设时注意。
- **移动限制(只走不跑不飞,保留跳跃)**:
  - `MovementOverride.SpeedFactor`(0.05~1)乘到注入输入量;MovementController 每帧从 `Configuration.MovementWalkFactor`(默认 0.4)同步。⚠️ 待实机确认:游戏是否吃输入模拟量(慢速)。若仍全速跑,需改方案(如周期性微步/找速度寄存器),见配置文件注释。
  - 护栏新增:飞行中(InFlight)/潜水(Diving)禁止自动移动(与骑乘 Mounted 同级);跳跃不干预(天然保留)。直线移动遇矮障碍不会自己跳。
- 修复(同日):tool 动作从 arguments 解析(见前文);台词自身回显去重(own 频道捕获 25s 内同文本跳过)。

## 待办:角色找椅子坐下(用户预告的下一步,2026-09-05)
目标:角色能"自己找到附近的椅子并坐下"(下一步工作,等待用户具体指示)。初步方向猜测:找可交互座位物件(HousingFurniture? 游戏物件 EventObj/NPC 旁的坐垫/长椅 /sit?)→ 走近 → 使用坐姿;技术点需查:座位判定(哪些物件可坐、是否走 EventHandler)、移动与坐姿宏/动作、`/sit` 类指令或动作表、坐下打断规则。具体以用户指示为准,动手前先商量方案。

## 2026-09-05 走路模式 + 无人设策略(用户口径,已实现)
- **走路模式 = 模拟玩家的走路/跑步切换键**:Configuration.MovementUseWalkMode(默认 true)+ MovementWalkKeyVk(默认 0)。`/aca learnwalk`:10 秒内捕捉一次按键并记住 VK(GetAsyncKeyState 轮询,排除鼠标/修饰键;存配置)。`/aca movediag` 显示 VK。
- 切换模拟:core.TryToggleWalk() = PostMessage WM_KEYDOWN/UP(VK+MapVirtualKey 扫屏码)到本进程 FFXIVGAME 窗口(与 MIDI 注键同机制,CN 已验证消息泵可用)。
- **全程走路算法(MovementController 探测)**:移动启动先跑 ~0.6s(需位移≥1.2m),实测速度 >3.5m/s 判定在跑步 → TryToggleWalk 切一次走路;结束(Finish)时若切换过则再切回跑步(保留玩家手动状态)。阈值 WalkSpeedThreshold=3.5(走≈2~3/跑≈6)。
- ⚠️ 若实机发现切换键模拟无效(角色仍跑步或没反应):优先怀疑 ①PostMessage 对走路键不被游戏读取(需改 GetAsyncKeyState→真实注入或内存标志)②探测阈值/窗口问题;把 movediag(VK 值)+ xllog 相关行发我。
- **无人设 = AI 完全不触发**(2026-09-05):SendMsg 对 user 触发消息先查 currentRole 的 role.setting,为空直接返回(不进调度、不请求);只留历史/网页。日志仅首次提示一次(_noPersonaWarned,勿去掉这句说明)。之前"无人设也回话"的裸助手行为已废弃。

## 2026-09-05 走路模式最终方案(替代上文 learnwalk/测速,勿再用旧思路)
- **直接读写 `FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Instance()->IsWalking`(bool 实例字段)**:玩家"走路/跑步切换键"改的就是它,已用元数据工具确认字段存在且类型 bool、Control 有 Instance()。→ 无按键模拟、无测速、无 VK 学习。
- MovementController 启动(StartRuntime)若 MovementUseWalkMode 且当前 !IsWalking → TrySetWalking(true)(_walkForced=true,core 方法只在状态确实改变时返回 true);结束(Finish)若 _walkForced → TrySetWalking(false) 恢复。
- 配置:MovementUseWalkMode(默认 true);已删 MovementWalkKeyVk。旧 `/aca learnwalk` 保留为"已移除"提示分支。
- ⚠️ 待实机验证:①直接写 IsWalking 是否让角色以走路速度/动画移动(理论上等同手动切走路)②写后服务器/客户端是否接受(预期只影响客户端表现与移动速率,安全)。失败现象若为"角色仍在跑"或"走路但跳帧",把 movediag + xllog 发我。
- 元数据挖字段的方法(以后可复用):用 System.Reflection.Metadata 小工具读 FFXIVClientStructs.dll(dev 目录),列类型的字段/签名类型,确认 Instance() 存在(临时项目已删,可重写)。

## 坐椅子 v1(2026-09-05,待实机验证;入口=/aca sit [玩家名],缺省离自己最近)
- **识别粒度(答用户问)**:候选单位 =【家具对象】,屋内家具在 ObjectTable 里是 `ObjectKind.HousingEventObject`(EventObj 兜底;该版本无 HousingFurniture 枚举)。不识别更细的"交互点/座位点"——"能不能坐、坐哪、还能不能坐(多人椅)"全部交给游戏判定。
- 流程(MovementController IntentKind.Sit 状态机):Pick(找离中心最近、未试、无玩家紧贴 0.5m 的家具)→ ToSeat(走到距家具中心 1.2m 站定,SitStandDistance)→ Interact(选中家具 + 发 `/interact`,core.SitInteract)→ Verify(等 0.9s 后,用 0.45s"朝远离座位方向强制移动"探测:位移<0.25m=坐姿锁死=坐下 SatDown;能走动=没坐下→该座入 _triedSeats 换下一把)→ 全空椅 → FallbackStand 退而站到中心旁(MovementStopDistance,NoEmptySeat)。
- 相关核心:FindSeatCandidate/AnyPlayerNearFurniture/SitInteract(都在 AuraCanAiCore);MoveOutcome 新增 SatDown/NoEmptySeat。
- ⚠️ 不确定点(需实机):①`/interact` 对家具是否触发坐下(可能需换交互方式,如事件对象 Use/按键)②座前落点 1.2m 是否合适 ③占用粗滤(0.5m)与多人椅表现。行为语法/LLM 动作的 sit 接入等 v1 验证后再加(复用同一 Sit 入口)。
- 试座数据:先 `/aca seatscan`(看家具是否以 HousingEventObject 出现与名字/base),再 `/aca sit`;失败看 xllog“坐椅:…”行。

## 2026-09-05(晚)可坐位置改“手动标定清单”(移除自动找椅实验代码)
- **已移除**:MovementController 的 IntentKind.Sit/SitStage/UpdateSit/FindSeatCandidate 相关、MoveOutcome.SatDown/NoEmptySeat、core 的 SitInteract/IsSeatKind/AnyPlayerNearFurniture/SeatScanDebug、/aca sit、/aca seatscan。勿再按旧“识别家具+interact+锁死探测”思路(用户否决)。
- **新方案(记录式,兼顾占座与“椅子在哪”)**:用户自己坐到目标位置后执行命令记录。
  - 命令:`/aca seatadd [名字]` → 记录本地玩家 坐标X/Y/Z + 面向Yaw(local.Rotation,0=南/逆时针)到 Configuration.Seats(List<SeatPoint>{Id,Name,X,Y,Z,Yaw,CreatedAt});同点(平面≤0.6m)去重提示。framework 线程安全。
  - Web:character.html 角色设定下新增「场景设定」区块(标题即可坐位置),GetSeats/SaveSeats(HttpServer 已注册,core GetSeatsJson/SaveSeatsJson);列出 Id/Name/坐标/面向°,删除即时保存。JSON 字段为 Pascal(Id/Name/X/Z/Yaw,与 playlist 网页约定一致)。
  - 占座判定未来做“坐”执行时用:记录点是精确座点,别人若已坐,其坐标≈记录点(距离判占用)。
- **“坐”动作本身(下一功能)**:按用户指示“没有更好方案就用游戏内部宏指令”——即走到记录点→摆好面向→触发用户游戏宏/游戏内坐指令。**未实现**,等用户对记录功能验证后再按指示做(可能要座点清单里配“该座点的坐法宏”)。勿自行发明 interact 方案。

## 2026-09-05(晚2)场景设定微调(用户口径)
- `/aca seatadd` 名字可空(不再自动"座位N");去掉了 0.6m 防重(坐姿是精确落点,无需防重)。
- SeatPoint 新增 `TerritoryId`(记录时地区;小地图与将来的"坐"执行都只认当前房间的记录,防跨屋错位)。字段为 Pascal 序列化(与 playlist 网页约定一致)。
- Web 场景设定区块:上半小地图(canvas,蓝点=记录点+朝向线,黄三角=自己位置/朝向,轮询 GetSeatMap 2s,只画当前房间),下半列表:每行 #id + 名字输入框(失焦即存 SaveSeats,可留空/补名)+ 坐标面向 + 删除。
- 后端新接口 GetSeatMap(返回 seats+self+当前tid;跨线程自动切框架线程)。GetSeats/SaveSeats 仍在。
- "坐"的执行(走到点→对面向→触发游戏宏坐法)仍待用户确认触发方式后实现。

## 2026-09-05(晚3)场景设定:多房子页签 + 表格同步刷新(用户口径,已实现)
- **多房子**:Configuration.Houses(List<House>{Id,Name})+ CurrentHouseId;SeatPoint 加 HouseId。房子不与人设关联,记录前手动选房子(网页页签/SetCurrentHouse)。seatadd 归属当前房子;无房子时提示先建(EnsureSceneState 自动把历史无分组座位收编为"房子1")。
- 接口:GetSeats 返回 houses/current/全部 seats;SetCurrentHouse;SaveHouses(改名/删房连带删其座位,current 指向被删则回退第一个);SaveSeats 改为按 houseId 只替换该房记录(勿再当全量替换);GetSeatMap 只画当前房子∩当前地区,附 houseSeatCount/houseMaxId。
- 前端 character.html 场景设定:房子页签(选中主色,✎改名 ✕删房 + 新建),小地图(蓝点=记录+朝向短线9px,黄三角=自己朝向10px;名字留空则不显示文字;坐标 X 右/Z 下不改),表格行:名字输入留空时灰字、失焦保存。
- **表格同步刷新**:refreshSeatMap 每 2s 轮询,比较 houseSeatCount/maxId 变化才 loadScene() 重刷列表(正在编辑名字时跳过,防失焦)。
- 记录用法:先新建/选中房子页签 → 游戏里坐上去 → /aca seatadd [可选名字]。

## 2026-09-05(晚4)坐到记录点(执行,待实机验证)
- 命令:`/aca seatgo [选择器]`;选择器:空=离自己最近且没被占的空座(同房间优先)/ `#id` 调试 / 名字部分匹配(当前房子)。占用判定=座位点 0.6m 内有其他玩家(IsSeatOccupied)。
- 流程(MovementController IntentKind.Sit + SitPhase):Walk 到座前参考点(座位记录点 + 沿坐姿朝向 SeatApproachDistance=1.0m;参考点用于让角色走到椅子正面)→ Pause 0.35s → TriggerSit:core.ExecuteSitMethod()——优先 `Configuration.SeatSitMacro`(0-99 游戏宏,默认 -1 未用),否则发 `SeatSitCommand`(默认 "/interact";可改成自己准备的坐法)→ Confirm 1.4s → Finish(SatDown)。
- 失败:占用→SeatOccupied/提示;别的房间/房子→WrongRoom;找不到→error 文本;移动护栏同移动。
- ⚠️ 默认 /interact 是否真能坐下**必须实机验证**:不行就把你的坐法做成宏,配置 SeatSitMacro 指过去(或把 SeatSitCommand 改成该指令)再试。角色朝向无法原地旋转:靠"走到椅子正面"自然面向(座位记录时的 yaw 方向)。
- 行为规则/LLM 的 sit 动作接入待此命令验证后补(直接复用 SitOnSeat)。

## 2026-09-05(晚5)去坐精度改进(用户反馈"有时能坐有时歪")
- 根因:默认站定点是推算的(座位点+沿坐姿朝向 SeatApproachDistance),不同椅子/朝向下不准。到站容差也从 0.7 收紧到 SeatArriveTolerance(默认 0.35)。
- 新增**手动校准**:`/aca seatstand [名字|#id|空=最近]` —— 站到你认为"自然准备坐"的位置执行,记 SeatPoint.HasApproach/ApproachX/Z;之后 `/aca seatgo` 该座优先走到校准点。网页列表行会标"已校准站定"。校准点在 seatadd 记录时为空(false),默认仍走推算。
- SeatSitCommand 默认 "/sit"(实机确认),旧 "/interact" 迁移。
- 命令:seatadd/seatstand/seatgo;帮助串已含。
- 2026-09-05(晚6):默认站距 1.0→0.45(会跑到邻椅/桌另一侧的案例),Plugin 启动迁移 1.0→0.45;仍偏则 /aca seatstand 校。
- 2026-09-05(晚7):站距 0.45→0.1(0.45 会坐歪),Plugin 迁移 (1.0|0.45)→0.1。若 0.1 与椅子碰撞/站不进,则说明该椅需 /aca seatstand 校准,勿再全局改。
- 2026-09-05(晚8):站距→0(走到座位记录点正上方再 /sit,实机最正);迁移 (1.0|0.45|0.1)→0。
- 2026-09-05(晚9):坐下后校验+重试(用户口径):Confirm 1.4s 后测与记录点平面偏差,≤0.35m=坐正(SatDown);歪了且 <3 次 → StandUp 阶段朝侧向走一小步(0.8m)站起脱离,再 Walk 回座位点重坐;第 3 次仍歪 → Finish(Failed) 停住不动。
- 2026-09-05(晚10):重试仅 1 次(MaxSitAttempts=2);StandUp 挪位 0.8→2m,走够 1.4m/2.4s 后回座重坐。
- 2026-09-05(晚11):重试路径三拍:横向离椅 1m(StandUp)→ 到离椅 2m 的远位(Prep,沿远离方向延伸)→ 从 2m 外走回座位点坐下(Walk);仍只重试 1 次。
- 2026-09-05(晚12):重试再简化 = 站起来(走路输入即站起)→ 沿远离椅方向走到离目标 2m 处 → 回头走 2m 到目标 → 坐下;仍 1 次重试。
- 2026-09-05(晚13):重试最终口径 = 坐歪后先再发一次 /sit(站起),朝目标方向直走 2 米(站起点算起),再 /sit;仍只 1 次重试。若站起点≈目标(正坐其上),改为沿坐姿朝向先走 2 米再回? — 现实现为朝坐姿朝向走 2 米再 /sit。
- 2026-09-05(晚14):重试步长 2→1m;坐流程走位加 0.5s 卡住检测(SeatMoveStuck,位移<0.12m/0.5s 即停);通用移动卡住判定 3→0.6s(配置迁移)。

## 2026-09-06(凌晨)AI工具化 + 障碍标记/避让(大版本)
- **场景注入瘦身**:不再每轮自动列座位;改为按需工具。
- **身体工具集(BuildBodyActionTools)**:rp_body_action(approach/follow/leave/face/stop/sit)+ 查询工具 lookup_player(某人当前距离/是否附近/看你;避免"以为很近实际已走开")、list_seats(当前房间可坐位+占用)、list_obstacles。ProcessBodyReplyAsync 支持**多轮工具循环**(信息工具→回填 role=tool→再问),最多 4 轮;assistant(tool_calls)与 tool 结果都以 JObject 进上下文。SendBodyChatAsync 现返回(content,toolName,toolArgs,toolId,assistantJObj)。
- 场景注入末尾提示:距离以当前为准、对方可能已走开;涉及走向前不确定先 lookup_player。
- **障碍矩形**:ObstacleRect{Id,HouseId,Name,TerritoryId,MinX/MinZ/MaxX/MaxZ};配置 Obstacles;网页场景设定小地图上"✏️画障碍"拖拽画矩形(世界坐标轴对齐;可命名/留空;列表改名/删除);SaveObstaclesJson 按房子替换(记录时当前房间为 TerritoryId);SaveHouses 删房子连带删障碍;GetSeatMap/GetSeats 均返回 obstacles。
- **避让**:MovementController.AvoidObstacles(2D slab 线段-扩边矩形相交;挡住时沿起点→中心反方向推算出边界外 0.3m 的出逃点)应用到 approach/follow/move 的目标点与坐流程 Walk/StandUp 的目标点(leave 未接)。margin 0.4。
- ⚠️ 待实机:拖拽坐标→世界换算方向(X 右/Z 下,与地图一致);避让绕行是否顺滑(出逃点单跳法,复杂摆位可能绕不好,需要时再升级为多跳/沿边)。list_obstacles 已可读真实数据。
- 2026-09-06 早:vnavmesh 参考源码(_ref)已删(仅参考,曾不入编译/git);❌ 以下旧避让方案已全部废弃(勿恢复):
  ComputePath 多跳/SideArc 侧边弧线/AvoidObstacles 出逃点/换侧绕行(_avoidSide/_avoidFlipped/_avoidWaypoints)——已被下方 2026-09-06 下午 A* 寻路整体替换;
  ~~避让升级多跳(ComputePath:逐段找挡路矩形→沿起点反方向推出绕出点→最多6跳,Queue 存路点,目标移动>0.6m 清空重排)~~
  ~~2026-09-06:避让调试定稿:侧边弧线(SideArc):挑较近一侧、外侧 0.55m…~~
- 2026-09-06:乱码修复——模型在"补台词轮(tools 关闭)"把 <tool_calls><invoke rp_body_action>… 当文本输出;补台词提示加"严禁调用工具/输出标签",并加 LooksLikeToolLeak 过滤(命中即丢弃不发送不进历史)。
- 2026-09-06(下午2):**工具调用文本泄漏升级为"恢复"而非"丢弃"**(用户反馈日志里 LLM 回复丢弃):DeepSeek(v4-flash)偶发不返回标准 tool_calls,而把工具写成 XML 文本放 content(Anthropic 风格 <tool_calls><invoke name=…><parameter name=… string="true">…</parameter></invoke></tool_calls>)。旧行为整条丢弃 → 动作不执行(冷场)、有前置台词时台词也丢。新逻辑(SendBodyChatAsync 解析处):
  - LeakedToolCallHint 宽松判定(content 含 <invoke/<tool_calls);TryParseLeakedToolCalls 用正则提取 invoke 块与 parameter(name→值,HtmlDecode,兼容带/不带引号、跨行、string="true" 属性),生成伪 id(leak_txt_N),把 XML 块从 content 剥掉留下前置台词;
  - 恢复成功 → content=剩余台词(纯动作轮为空)+ calls 非空 + 同步改 asst 的 msg["tool_calls"];走既有流程(hasAction→ExecuteBodyAction + 有台词发台词/无台词补台词轮);纯动作泄漏也能执行并自动补台词,不再冷场;
  - 恢复失败(无 invoke)→ 维持原丢弃逻辑(AppendAssistantAndEcho 内 LooksLikeToolLeak 兜底不变)。
  - 单测过 4 种样本:纯动作泄漏/台词+动作泄漏/无引号变体/lookup_player。⚠️ 待实机:新 build 后重测 sit 动作泄漏是否真的执行+补台词,把 xllog 里 "LLM 工具调用泄漏已恢复" 行发我。

## 2026-09-06(下午3)"坐在 X 旁边"只走近不坐的修复(用户实测反馈;需实机重测)
- **现象**:玩家说"坐在我旁边",角色只是 approach 走近而非 sit(日志 14:34:30:rp_body_action {action:approach,target:奥·乌儿})。
- **根因**:rp_body_action.sit 的 target 只认 座位名/#id,无法表达"某玩家旁边";list_seats 也只列名字不带位置 → 模型查完(全息显示器/#2..#8)无法判断哪个空座"在奥·乌儿旁边" → 降级 approach。
- **修复(三层)**:
  1. ResolveSeatForSittingCore:#N 之后、名字匹配之前新增**玩家名精确匹配**(FindPlayerByName,清洗名):命中 → 同房间、未占用、离该玩家最近、且 ≤6m 的空座(>6m 报"太远"回退让上层决定);
  2. list_seats 加可选 **near=玩家名** 参数:按到该玩家的距离升序列出(名字距X.Xm(空/有人)),方便模型挑旁边座;不带 near 维持原全部列表。list_seats 工具 schema 同步加 near(非必填);
  3. 提示更新:rp_body_action.sit target 描述 + BuildBodyActionTools + BuildSceneSnippetCore 注入句,都写明"坐某人旁边 = sit 且 target 填那个玩家名"。
- /aca 帮助串 seatgo 同步支持"填玩家名=坐 TA 旁边最近空座"(与 /aca seatgo 同解析器,行为/命令/LLM 三入口自动受益)。
- ⚠️ 待实机:玩家站着说"坐我旁边" → 角色应 sit(走到旁边最近的记录空座坐下)而非 approach;旁边若无记录座位/都太远,应说"没法坐旁边"而不是走近;xllog 看 rp_body_action 的 arguments 是否 action=sit target=玩家名。

## 2026-09-06(下午4)空座被相邻座位误判"有人"(实机反馈:一排 7/8/9,7/9 有人,8 空却显示有人)
- **根因**:座位记录点间距恰好 0.6m(如 #7(2.90,3.09) #8(2.30,3.09) #9(1.70,3.09)),而 IsSeatOccupied 占用判定半径旧值 0.6m → 坐 #7/#9 的人距 #8 恰好 0.6m,落入 #8 判定圈 → 中间空座被误判有人。
- **修复**:占用半径 0.6→**0.25**(const SeatOccupiedRadius,用户定稿口径)。依据:一排记录点间距恰 0.6m,0.6 半径会让邻座落入中间座判定圈;0.25 只认几乎正坐在记录点的人(坐姿本体偏差通常 <0.1m),不误伤邻座;占用漏报(坐偏>0.25m)可接受,坐下前会再校。⚠️ 勿再调回 ≥0.5(一排密座中间位必误伤)。
- ⚠️ 待实机:一排多人座(如 7/8/9)两边有人中间空 → list_seats 应显示 7(有人)8(空)9(有人);坐下选择也应能选中 #8。

## 2026-09-06(下午5)聊天采集/工具增强(用户四连需求,已实现,待实机)
- **1C/1D(原创/情感动作)有回应**:原来只进历史不触发回复。现在:他人动作进历史并(若最近有普通文本频道 _lastSpeakChannel)触发一次回复——台词跟随最近普通文本频道(_lastSpeakChannel/_lastSpeakAddr),即用户口径“动作也该回应、发到最近普通聊天处”;从未有普通文本(无可跟随)→ 仅上下文。DefaultChannelConfig 给 1C/1D 默认 llm=true。自己发的动作去重(防 /em 回显重复进历史)。
- **采集限制**:NoLlmChannels = 跨服贝 25/65-6B + 部队 18 + 新人 1B:**后端 ChatLLMHandler 开头强制不采集**,SaveConfigJson 兜底强制这些频道 llm=false;前端 setting.html:这些频道的大模型辅助聊天勾选 disabled 置灰“(不支持)”,保存强制 false。
- **小队频道 0E 只采本队成员**:GetPartyMemberCleanNames(GroupManager EntityId → ObjectTable 反查清洗名,解析不出=不过滤防误伤)+ IsPartySelf;非本队他人的 0E 消息忽略 LLM 采集。
- **lookup_player 增强(可空)**:不带 name = 列出在场玩家(名字/种族/性别/在线状态/在你这边的方位);带 name = 单查加种族性别状态方位+是否看你(合并了“附近玩家识别种族性别”与“活点地图方位”);RP 时模型可知对方长相/方位。
- **face_player 新工具**:轻动作(转身看向某玩家,不移动),由模型在“多人的时候对谁说话就看谁”时主动调用;target 空=最近接触的人。执行在 RunInfoToolCore(走 info 回填循环,不触发补台词);场景注入句末加“[刚才 X 和你说话/互动;若在场,这就是你此刻的主要对话对象]”。rp_body_action.face 仍在(接近/到达时自动面向)。
- 注释/字段:_lastSpeakerName(最近互动者)供场景注入。
- ⚠️ 待实机验证:①动作触发后 2~5s 内应回一句(频道=最近普通文本);②小队里非成员发言不再被采集;③前端这些频道勾选置灰;④多人 RP 时模型可 face_player 看向说话者;⑤说话范围仍=已勾采集的普通频道。

## 2026-09-06(下午)寻路重做:2D 栅格 A* + 前瞻圆弧跟随(替代全部旧避障,待实机验证)
- **背景**:旧 SideArc 绕行在真实场景反复失败(绕点全在脚下/方向反向/每帧刷日志死循环)。日志实测定位:13:42 改的源码未重新 build,测试跑的是旧 DLL(详见 danmud.log 13:39 build vs 13:42 源)。但即使 build 后旧几何绕行思路仍脆弱 → 整体重写。
- **新模块 Core/Movement/NavPathPlanner.cs(纯逻辑,无 Dalamud 依赖,已建独立单测工程 D:\navtest 验证过):
  - 2D 栅格 A*(cell 0.2m),障碍=当前房间手画矩形(ObstacleRect→NavObstacle),按 **Pad=0.4m** 膨胀后标阻塞格(⚠️勿调大:桌旁座位区常仅 0.4m,过大把真实起/终点站格包进阻塞区→无解);8 邻域+禁止斜穿墙角;PriorityQueue A*,窗口=全部膨胀障碍+起终点外扩缓冲(超 600 格自动裁剪)。
  - 路径拉直(Straighten):贪心 LOS(线段-膨胀矩形 Slab 检测),去掉锯齿格点→少量直线拐点。
  - **边界处理(实机教训,勿删)**:①起点格强制可达;起点在真实家具矩形内(坐姿起身/矩形画大)→ BFS 挤出到最近自由格打通 1 格宽通道(上限8格);②终点在真实家具内→外推到最近自由格(上限6格),世界路径以该格收尾,**不加回原终点**(避免末段穿家具);③终点仅被膨胀盖住(贴家具站/坐)→解除终点格+4邻阻塞(真实矩形外部分);④障碍围死→返回 null。
  - 快速路径:无障碍或起终点 LOS 直通→直接两点,不走栅格。
- **MovementController 改为前瞻跟随(替代旧 ComputePath/换侧)**:
  - SteerToward(myPos,goal):维护 _pathPts 折线路径;失效条件=无路径/目标位移>0.5m/玩家偏离路径>1m;重规划节流 0.15s;无解→直线试走由卡住检测收尾。
  - LookAheadPoint:把玩家投影到路径上,取**前方 PathLookAhead=1.2m 的点**作为 MovementOverride.DesiredPosition → 角色朝路径前方一点走,过拐点前自然转弧,不停顿不卡角(平滑流畅的关键)。
  - 卡住检测:0.6s 无位移→强制重排一次(_stuckRetried)仍卡才 Finish Stuck(坐流程 0.5s/0.12m 同规则 SeatMoveStuck)。
  - 应用到 approach/follow/move/sit 的 Walk 与 StandUp 走位(leave 仍直线远离不寻路)。
- **删除**:ComputePath/FindBlocking/SideArc/Slab/_avoidWaypoints/_avoidSide/_avoidFlipped/_routeGoal;NavPathPlanner 内含自己的 Slab/LOS(勿删)。
- **验证过的纯逻辑场景**(D:\navtest\Program.cs 可重跑):日志场景(4.08,5.16)→座位(1.70,3.09)绕桌 X1.3~3.7 Z3.5~4.8 = 先出膨胀→沿右(x≈4.2)→下(z≈2.99)→横进,正确;双桌窄通道/无障碍直连/终点入家具外推/L 形墙/沙发+茶几布局/窄缝/5 桌阵列 全部有解。
- ⚠️ **待实机验证**:①日志同场景走近 奥·乌儿/去坐,应平滑绕桌不停顿;**把 xllog 寻路段日志发我**;②站定点=座位点(距桌 0.4m 内)时 A* 贴边到达后再 /sit 是否坐正;③lookahead 1.2m 转弯半径是否够(不够把配置化,勿写死)。

# ===== 交接记录 2026-09-06(新会话先读这一段) =====
整体目标:人设驱动 RP 机器人,能力=读聊天/回话(频道跟随+悄悄话)+ 移动内核(走/坐/绕障)+ LLM 身体演出(rp_body_action 多轮工具)。项目唯一,D:\AuraCanAI.Dalamud,git 仍 0 commit(未做存档,建议新会话先 git init commit + 备份 zip)。

## 已完成且(基本)验证
- 回复:随来源频道,0D→/t;拟真延迟调度(静默2~5s攒条,MaxWait12s);无人设=不触发AI;台词回显去重;工具泄漏文本=恢复为结构化调用执行(2026-09-06 下午2 起,见上;不再整条丢弃)。
- LLM:身体演出默认(有人设即启用);工具=rp_body_action(approach/follow/leave/face/stop/sit)+lookup_player+list_seats;多轮/并行 tool_calls 都支持(每 id 回填);场景注入精简。
- 坐:记录点法——/aca seatadd(坐下记录,房子分组自动带房间) 网页场景设定(房子页签/座位改删/小地图#id/已校准标记);执行 /aca seatgo [名字|#id|空=最近] 或 行为 sit 或 LLM sit;走向记录点(0 距离)+ /sit(SeatSitCommand),坐下后偏差≤0.35 判坐正,歪→起立→朝目标走1m→停0.6s→再坐(共2次);站距迁移到 0。
- 障碍:网页拖拽画矩形(ObstacleRect,按房子/房间);/aca 无碍命令。**寻路=2026-09-06 下午已重写**:NavPathPlanner(2D 栅格 A*+LOS 拉直,纯逻辑可单测)+ MovementController 前瞻跟随(见上方 2026-09-06(下午)段落),旧 ComputePath/SideArc 已删。纯逻辑场景验证通过,**待实机**。
- 走路模式=写 Control.IsWalking;卡住判定0.6s;坐流程 0.5s。

## 待办/未验证(新窗口优先)
1. **A* 寻路实机验证(2026-09-06 下午重写,新窗口第一优先)**:日志同场景——走近 奥·乌儿/去坐,目标(1.70,3.09)桌左下(桌 X1.3~3.7 Z3.5~4.8);期望沿右通道平滑绕行不停顿。失败把 xllog 寻路段发我。验证点:绕行轨迹弧度、贴桌坐位(0.4m 内)站定后再 /sit、直线无障直走不抖、目标玩家走动时重规划不卡。
2. 若 A* 仍不稳:再讨论 vnavmesh IPC(笔记有 IPC 接口清单;需装 vnavmesh)。
3. 椅子边缘细节:多人椅占用、斜家具矩形不支持(轴对齐)。
4. 存档:git 已有初始 commit(2026-09-06 下午);_ref/ffxiv_navmesh 已重新克隆(不入库,_ref 在 .gitignore)。改动后务必 commit;备份 zip 待验证后再打。
5. 后续打磨方向(用户提过):人设演出细节(走/坐台词时机、表情配合、座位占用圆场)。

## 易踩点速记
- FFXIV 无"坐着"旗标;坐正=位置偏差;站着时位置≈座位点可能误判(必要时改移动锁死探测)。
- /interact 对家具无效→用 /sit(实机确认)。
- Dalamud 版本 ObjectKind 无 HousingFurniture,屋内家具=HousingEventObject/EventObj。
- 工具:assistant 带 N 个 tool_calls 必须逐个 tool id 回填否则 400。
- 模型会把工具调用当台词→LooksLikeToolLeak 拦截。
- BODYREQ/BODYRSP 在 %AppData%\XIVLauncherCN\dalamud.log。
