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
- **触发宏(MacroExecutor,2026-09-11 重写)**:`UIModule.Instance()` → 宏模块 `GetMacro(set, index)`(set 0=个人宏 1=共享宏) → `ShellModule->ExecuteMacro(macro)`;执行前查 `ShellModule->MacroLocked`(忙则不触发)。
  ⚠️ **子模块指针严禁硬编码偏移**:曾用 `(byte*)ui + 0x61B0 / + 0xB9B30`(CS 7.51 布局),游戏更新后 RaptureShellModule 偏移变 `0xB9B50`,偏移错 0x20 字节 → `MacroLocked` 读到脏数据恒 true → **所有宏触发都被当「执行器忙」丢弃**(表象:注视提示有了、行为提示没有、宏不执行;队列超时丢弃还是静默的,日志几乎无痕)。
  现取法:优先 `ui->GetRaptureMacroModule()` / `ui->GetRaptureShellModule()`;为 null 时回退到「运行时反射 `FieldOffsetAttribute` 得到的偏移」(随 CS 包自动适应,仍不是写死数字)。
  诊断命令:`/aca macrodia [N]` 打印 ui/macroModule/shell 指针、`MacroLocked`、宏是否为空与行数;`/aca macro N` 失败时自动附诊断文本;行为触发失败/队列超时也都会带上诊断。
  (同类风险:`Movement/MovementOverride.cs` 的 `CameraEx` 用了 `FieldOffset(0x140)` 写死相机方位角偏移,仅 legacy 移动模式用、失效时会走错方向,待换成 CS `Camera` 结构)
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
- **「角色扮演限制」**(BehaviorItem.rpMode,枚举 `BehaviorRpMode`:NoLimit=0 默认 / RpOnly=1 仅角色扮演时触发 / RpSkip=2 角色扮演时不触发;编辑区下拉框选择):**「角色扮演中」= 前端网页「角色设定」页已选中当前角色**(`LLMConfig.currentRole` 非空,core.IsRolePlaying 纯配置读取,与游戏 OnlineStatus「角色扮演中」无关——曾误用在线状态,已改);不满足时与「离开/战斗中」同一条丢弃路径(不触发、不设冷却、状态照常更新);解析链 Parse → ParseSegment → BehaviorRule.RpMode,运行时在 Engine.Tick 的顺序为 need → 离开 → 战斗 → 角色扮演
- 条件库:`looking_player`(看我的玩家名,=/!=)、`anyone_looking`、`looking_player_count`(数值)、`area`(PlaceName 中文名)、`in_housing`、`room_size`(S/M/L/公寓,取住宅代码首字母)、`my_status`(OnlineStatus 中文名)、`in_party`(FFXIVClientStructs GroupManager,国服无 IGroupManager 服务)、`my_job`(ClassJob 表中文名,失败回退 Abbreviation)、`nearby_player_count`(数值)、`target_name`(当前选中目标)、`time`(现实时间 HH:mm,>=/<=)、`anyone_emote_to_me`(有人刚对我做表情,布尔)、`emote_to_me_player`(刚对我做表情的玩家名,=/!=)、`emote_to_me_name`(表情名,游戏内中文名如 摸头,=/!=)

**实现**:
- `Core/Behavior/BehaviorParser.cs`:定义文本 → 规则(纯逻辑,无 Dalamud 依赖);`BehaviorParseResult.Errors` 含段号,UI 红字显示;trigger 宏列表按逗号解析(每项 s?N,0-99 校验),after 支持 A,B 区间(校验 A≤B、非负,残缺写法如 after 3, / after 3,x / after when 给出专属报错,避免误报成宏编号错误);say 解析(频道/内容,频道表+变量表以 BehaviorSyntaxDoc 为单一数据源,未知频道/未知变量均报错);**BuildSayText(纯静态):变量替换 + 空变量跳过 + t 目标解析(内容开头 名字@服务器(不含变量)否则默认 last_tell),引擎仅传变量值回调**
- **AI 生成行为命令**(`AuraCanAiCore.GenerateBehavior` → DeepSeek function calling 调 generate_behavior):**结构化模式(2026-08 重构,推荐路径)**:tool 参数为 `{comment, rules[]}`,每条 rule 含 action/macros/channel/text/target/after/afterMax/cooldown/when(条件数组)/connectors/need;程序用 `BehaviorParser.AssembleRule` **拼装规范文本**(动作 → after → when → need → cooldown),LLM 只填值不碰语法 → 杜绝文本乱序/拼错/条件名拼错(拼装时校验动作/宏 0-99/频道/条件名(支持去下划线模糊匹配)/数值/时间,任一规则失败整体报错重试)。**兼容回退**:LLM 未按结构返回时走旧 definition 文本路径 → `FormatBehaviorDefinition`(分号与换行都视为规则分隔,逐段 `NormalizeSegment` 按规范顺序重建,解析失败保留原文由保存时报错)。**勿改回宽度折行**(曾踩坑:折行把条件值拦腰截断,如 emote_to_me_name = 抚摸 被折成 抚⏎摸,保存报"无法解析";单条长规则在输入框内横向滚动即可)
- `Core/Behavior/BehaviorEngine.cs`:500ms 轮询(挂 core 的 _timer500),边沿触发+冷却;动作调度(Schedule:say 直接执行(带 after 走 _delayed),look/after 走 _delayed 延迟列表,多宏 trigger 入 _chains 连发链,单宏 trigger 无延迟走 _queue);**多宏链**(MacroChain):执行完前一个(等 MacroExecutor.IsBusy 释放)再执行下一个,间隔 ChainGapMs=300ms,单宏失败不阻塞,300 秒防卡死;**_macroFiredThisTick 全局限制每 tick 最多触发一个宏**(延迟/队列/链共用,防同帧连发);ExecuteAction 分发 Trigger(单宏直接触发/多宏入链)/Say(ExecuteSay:BuildSayMessage → BehaviorParser.BuildSayText(变量替换/空跳过/t 目标) → core.SendBehaviorSay)/Look(选中目标),触发日志留痕;条目勾选「聊天内提示」→ /e 提示(发言成功/跳过/失败均有提示)。
  **重载预热与超时可见性(2026-09-11 补)**:Reload 后首次 Tick 会把「当时已满足」的条件视为已触发并丢弃动作(`_warmingUp`,防重载后全部重跑)——刚保存行为就想测会看到什么都不发生,现在这种丢弃也会发 /e 提示(仅 warm、且勾了聊天内提示);队列超时丢弃不再静默(带 `MacroExecutor.Diagnose` 原因 + /e 提示);宏执行器持续忙 >15s 且距上次告警 >60s 时输出一条诊断日志
- `Core/AuraCanAiCore.cs`:TryLook/FindPlayerByName(清洗名精确匹配,IGameObject 在 `Dalamud.Game.ClientState.Objects.Types` 命名空间)/GetLatestLookingPlayer 供 look 动作使用;**SendBehaviorSay(简写→cmd:优先当前 channelConfig 配置,兜底 SyntaxDoc.SayChannels)→ RunCommand 发送**
- `Core/Behavior/BehaviorModels.cs`:BehaviorItem 用 public 字段(ImGui ref 绑定)+ 每字段 [JsonProperty](Json.NET 不默认序列化字段)
- 持久化:`Configuration.Behaviors`(List<BehaviorItem>),UI 增删改/启停 → `core.SaveBehaviors()`(保存 + 引擎 Reload,Reload 重置运行时状态)
- UI:`Windows/DashboardWindow.cs` 第四页签,列表 = 序号(自动分配,删除复用最小空缺)/注释/启停勾选/编辑按钮;编辑区**内联在列表上方**(与回忆检索同交互,勿用 BeginPopupModal/OpenPopup —— 本 ImGui 绑定下 popup 打开无效,已踩坑回退),内容 = 注释 + 多行定义 + 聊天内提示 + 保存/删除/取消;保存失败红字;编辑区勾选项顺序:**「离开时不触发」「战斗中不触发」(均默认勾选)在「聊天内提示」前面**;最后是「角色扮演限制」下拉(仅角色扮演时触发/角色扮演时不触发/不做限制,默认不做限制)
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

## 角色自定义动作(AI 主动做动作,2026-09-11,待实机验证)

**需求口径(用户)**:前端「角色设定 → 角色管理」里给每个角色加一份**供 AI 使用的动作列表**;每个动作四列:名称(备注 + AI 引用标识)/ 文字(`/em` 开头那部分)/ 动作(游戏内表情名)/ 冷却(秒)。AI 可主动执行;动作与角色绑定;本质上是依次执行宏。

**实现**:
| 位置 | 内容 |
|---|---|
| `Models.cs` | `Role.actions`(列表)+ `RoleAction { name, text, emote, cooldown }`(小写字段名,与前端 JSON 对应)。**冷却不进配置**(内存态,避免被前端保存覆盖) |
| `Core/RoleActionPlayer.cs` | 执行器:一个动作 = 依次发两条游戏命令(`/动作名` → `/em 文字`,已带 `/` 则原样),步间隔 StepGapMs=300ms,由 core 的 500ms tick 泵送(一次一步);队列上限 3;冷却键 = `角色名\u0001动作展示名`;`PerformByName` 返回中文结果描述给模型 |
| `Core/AuraCanAiCore.cs` | 工具 `rp_emote`(仅在角色有动作时加入 tools,enum = 动作名;描述里写清每个动作的表情/文字/冷却);工具分发在 `RunInfoToolCore` case `"rp_emote"`;`rp_emote` 归入 `infoCalls`(执行后回填结果并继续循环,模型可接着说话——不像 rp_body_action 那样强制补台词轮);系统提示追加 `BuildRoleActionRule`(动作表 + 用法:动作不写进台词、冷却中别重复调、可动作+台词同回合;**执行成功只回 "成功"**,因为动作自身已不再回显进历史,模型不需要额外描述);`GetRoleActions/GetCurrentRoleName` 为公开入口 |
| `Web/character.html` | 角色编辑区新增「动作列表（AI 可主动执行）」:动态行(名称/文字/动作/冷却秒/删除)+「添加动作」按钮(事件委托一次绑定);渲染与保存分别走 `buildActionRows/actionRowHtml` 与 save 里的 `.action-item` 遍历(三个字段全空的行忽略);`escAttr` 转义属性值 |
| `Plugin.cs` | `/aca action [名称]`:不带名称 = 列出当前角色动作;带名称 = 直接做一次(测试用,与 AI 走同一路径) |
| `Web/help.html` | 新增「AI 动作(自定义表情)」帮助节 |

**注意/约定**:
- **表情命令仅在填了文字时补「动作」/「motion」子命令**(`RoleActionPlayer.BuildEmoteCommand(emote, hasText)`,2026-09-11 用户口径修正):不加的话 `/<表情名>` 会先出一条游戏官方表情提示,再加 `/em 文字` 的提示 = **游戏里两条提示**;补子命令(只播动作)后只留 `/em` 一条。关键字本地化:**表情名含中日文字符 → 「 动作」,否则(英文命令如 pet)→ 「 motion」**;命令里已带参数(含空格)时原样使用不重复补。→ **没填文字就不补**,保留游戏官方表情文字(否则聊天栏什么都不显示)。
- **前端会展示补完后的样子**(`character.html` 的 `composeCommands`/`refreshActionPreview`):每行动作下面显示「实际发送:(换行)每条命令一行」,输入时实时刷新(`input change` 事件委托);两个字段全空时显示「保存时会忽略这一行」。⚠️ **这是 BuildSteps 规则的 JS 副本,改后端规则要同步改 JS**(后端 `BuildSteps`/`DescribeCommands` 同理)。
- **动作类频道(1C 原创动作 / 1D 情感动作)的采集口径(2026-09-11 用户口径,勿改回)**:
  - **对方的动作不进聊天历史**:存成 `_pendingActionHints`(带时间戳),由 `TakePendingActionHints()` 在下一次请求里以 **system 消息**注入一次,立即清空 → **只存在一轮**;TTL 20 秒(过期丢弃,不让旧动作跑到很久之后的对话);有可回复的普通文本频道时顺带触发一次回应(`NotifyActionHint` → `NotifyChatTurnPending(_lastSpeakChannel, _lastSpeakAddr)`)。
  - **自己的动作直接丢弃**:1C/1D 且 isOwn → 直接 return(不入历史、不回显、不触发)。⚠️ **原来是把自身动作以 "(动作)" 形式当 assistant 台词入库**,模型会照猫画虎在台词里写括号动作(2026-09-11 用户实测);同时删了已废弃的 `_lastLlmEcho*` 回显去重字段。
  - 采集/播报/网页仍照旧(只有 LLM 历史路径改了)。
- **台词里的括号动作会被过滤**(`AuraCanAiCore.StripBracketActions`,在 AppendAssistantAndEcho 里做):模型偶发在台词里写「(轻轻点头)」「*叹气*」(加了动作列表后更容易) → 先反复剔除最内层成对括号(全半角/【】/〔〕等 10 对,最多 3 轮处理嵌套),再清残留单个括号与星号;**整句都是描写 → 不发那条**(日志记「台词被过滤」)。Prompt 侧 OutputFormatRule + 动作工具描述里也加了硬规则。
- 动作只在选了当前角色(有 setting)时才有意义(无角色则不回复也不触发工具);文字/动作都是**游戏原生指令原文**(`/表情名` 与 `/em 文字`),所以游戏占位符(`<t>` 当前目标、`<me>` 自己、`<pos>` 坐标等)天然可用,发送时由游戏替换——插件**不做**任何转义/替换;保存/重置角色配置时 `RoleActions.Reset()` 清空队列与冷却;`/em` 原文动作会进游戏聊天日志,但自己发言会被清洗名过滤掉(不会回声循环)。

## 网页两个「还原默认」的作用范围(2026-09-11 核对)

| 页面 | 按钮 | 接口 | 重置什么 |
|---|---|---|---|
| 角色设定(character.html) | 保存角色设定 / **还原默认角色设定与状态机** | POST `/SaveLLMConfig` / **`/ResetRolesAndStateMachine`** | 重置 `Configuration.LlmConfigJson`(`currentRole`→空、`roles`→默认「白屿涟音」+「皮下」) **以及全部状态机** `SmSets`(→单套「白屿涟音」= 皮下/皮上)。副作用:`ResetChatHistory()` + `RoleActions.Reset()` + `State.ResetIdle()`。**DeepSeek Key 保留**;「启用状态机」开关不动 |
| 消息设置(setting.html) | 保存所有配置 / 还原默认配置 | POST `/SaveConfig` / `/DeleteConfig` | 只重置 `Configuration.MessageSettingsJson`:`privacyMode`、`keywords`、`blockwords`、`defaultFilePath`(→./chatlogs)、`logPeriod`(→每天)、`weekStartDay`(→周一)、`channelConfig`(→默认频道表,播读/记录/AI 采集开关全回默认)。运行时 `_msgSetting` 直接替换 → 即时生效 |

- 两个「还原」互不影响:**不重置** 场景设定(Seats/Obstacles/Houses)、行为设置(Behaviors)、玩家备注(PlayerNotes)、歌单(Playlists)、TTS(开关/音量/语速/并发)、网页端口、LLM 回复节奏、移动参数、DeepSeek Key。
- 场景设定是**编辑即自动保存**(saveHouses/saveSeats/saveObstacles 各自单独接口),不走页面的保存按钮——所以 character.html 的保存按钮只对「角色设定」有效(已改为与状态机同款的小绿按钮「保存角色设定」,放在「角色管理」标题行;旧的大号 sticky 保存条已删)。
- 新增 `POST /ResetRolesAndStateMachine` = 角色设定 + 全部状态机一起还原;旧 `/DeleteLLMConfig` 保留(只重置角色)。

## 小队解散清理 + AI「离开」(2026-09-11 用户口径,已实现待实机验证)

### 1. 小队解散/退出 → 清空上下文与动作队列
- `CheckPartyStateTick()`(500ms tick,挂在 `_timer500`):记录 `_wasInParty`,检测到 **在小队 → 不在小队**(解散/退队/被踢)→ **先判 RP 条件**:`IsRolePlaying()`(前端「角色设定」选中了当前角色)为真才清;未选角色(非 RP 模式)只复位 `_leaveArmed` 并记日志,**不清上下文**。
- RP 中则依次做:`ResetChatHistory()`(清聊天上下文并重建 system 提示) → `RoleActions.Reset()`(清待执行动作与冷却) → 清 `_pendingActionHints` → `_replyPending=false` → 清 `_lastSpeakChannel/_lastSpeakAddr` → `_leaveArmed=false`。
- ⚠️ 副作用(已知且接受):打完本/退了集合队伍也会触发清空(只要从小队变单人)。加入小队不清。

### 2. 新工具 `leave_scene`(「离开」)
- **AI 侧**:工具无参数;描述“告辞/结束互动/不想被围观时用”;归入 `infoCalls`(执行→回填结果→继续循环,模型可再说一句告别)。`RunInfoToolCore` case `"leave_scene"` → `LeaveScene()`。场景提示里也加了“想告辞用 leave_scene”。
- **行为**(`LeaveSceneCore`,框架线程):
  1. 正在进行移动 → 先 `Movement.Stop()`(离开优先);然后 **`ClearLook()` 移开目光**(`TargetManager.Target=null` + `SoftTarget=null`,不看任何人也不看自己——“走开之前先移开目光”用户口径);
     P.S. `TryLook` 已加“不选中自己”防护(`FindPlayerByName` 同名时会返回自己);
  2. 在小队则 `_leaveArmed=true` + 重置倒计时;不在小队则只走位/坐("无需退队");
  3. 找「人少处的空座」(`FindQuietSeat`:当前房子+当前楼层、`!IsSeatOccupied`、离最近其他玩家 >= 4m,取离自己最近的)→ `Movement.SitOnSeat("#id")`;
  4. 没座且最近的人 < 10m → `PickQuietPoint`(以自己为中心 6/9/12/15m × 16 方向环形采样,打分 = 离最近人的距离,超过 12m 每米扣 1.5,避免跑到天边)→ `Movement.MoveToPoint`;
  5. 已经很空(>=10m)且无座 → 原地不动。
- **60 秒静默自动退队**:在 `OnChatMessage` 里 **任何他人发言**(不管频道/是否采集)重置 `_leaveLastChatAt`(自己的话不算);`CheckLeavePartyTick()`(500ms)到 60 秒 → `LeavePartyNow()`。
- **退队实现**:`InfoProxyPartyMember.Instance()->LeaveParty()`(FFXIVClientStructs,**不依赖聊天命令文本/本地化**,比 `/pcmd leave` 稳);失败会记日志(返回 false = 不在小队/状态不允许)。
- **测试命令**:`/aca leavescene`(走「离开」流程)、`/aca party leave`(立即退队)、`/aca party`(小队状态)。

## 对话上下文两个“坑”修复(2026-09-11 晚,用户看日志发现)

### 1. assistant 每条重复两遍(自身回显没去重)
- 现象:`/xllog` 的 BODYREQ 里每条 assistant 台词连着出现两遍(模型看到自己在复读)。
- 根因:`AppendAssistantAndEcho` 已经把台词写进 `_chatHistory`,而台词发出后会被 `ChatGui.ChatMessage` 回显回来 → `ChatLLMHandler` 的 `isOwn` 分支又 `SendMsg(text, "assistant")` 写一遍。
- 修复:**恢复 `_lastLlmEchoContent/_lastLlmEchoAt` 25 秒去重**(上一轮改动作采集口径时被我误删,它当时实际同时保护着普通文本频道)。注意 1C/1D 自身动作仍然直接丢弃(两件事别搞混)。
- ❗ 改动这条路径时务必同时验证“台词不会双写历史”和“自身动作不入历史”。

### 2. 注入的 system 提示词只保留最后一条
- 现象:一个请求里出现 3 条 system(人设卡/场景/补台词指令),实测症状:叫角色“往边上挪挪”时她什么都不做(选了 `rp_body_action(stop)` 后只说了句话)。
- 修复(`AuraCanAiCore`):
  - `SnapshotHistoryWithScene` → 重写为 **`BuildTurnMessages(manyMsgs, extraInstruction)`**:把“攼多条提醒 + 场景 + 现场动作提示 + 额外指令”**合并成一条 system**,并用 `copy.Add(...)` **拼在消息末尾**(以前是 `Insert(Count-1)` 插在最后一条 user 之前)。
  - 新增 `AppendToLastSystem(msgs, text)`:把“本轮额外指令”**并进最后一条 system**(没就追加)。补台词轮、工具调用泄漏纠正轮、非中文重试都已改用它;`BuildRequestMessages`(无人设纯文字路径)也改成拼到末尾。
  - 结果:除历史里的**人设卡(第 0 条 system,持久 persona 锚点)**,一个请求里只会有**最后一条**注入的 system。
  - 工具轮特殊:`msgs.Add(asst)` + `tool` 结果会接在那条 system 之后(工具结果必须在最后)。
- ⚠️ 新增任何“往请求里塞提示”的代码,一律用 `AppendToLastSystem` / `BuildTurnMessages(extraInstruction:)`,**不要再 `msgs.Add(new JObject{role="system"})`**。

### 3. 附带:让开类指令别再选 stop
`rp_body_action` 描述里加了:“对方说‘让一让/挪一挪/别挡着/借过’→ 用 leave(target=对方) 或 leave_scene,**不要用 stop**(stop 只是终止移动,不会真的让开)”。

### 4. 排查工具:`tools/inspect-bodyreq.js`
```bash
node tools/inspect-bodyreq.js        # 看最后 3 条 BODYREQ 的 message 列表(自动标出重复项 / system 过多)
node tools/inspect-bodyreq.js 10     # 看最后 10 条
node tools/inspect-bodyreq.js 3 7    # 额外打印最后一条里 message[7] 全文
```
它从 dalamud.log 抽 BODYREQ,自带括号配平切数组(整条 JSON 只有工具描述处的小毛病也能解析)。

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
- **1C/1D(原创/情感动作)有回应**:原来只进历史不触发回复。现在:他人动作进历史并(若最近有普通文本频道 _lastSpeakChannel)触发一次回复——台词跟随最近普通文本频道(_lastSpeakChannel/_lastSpeakAddr),即用户口径“动作也该回应、发到最近普通聊天处”;从未有普通文本(无可跟随)→ 仅上下文。**1C/1D 的 llm 采集不默认开,按用户在设置页勾选的实际来**(配置里勾了才采集/回应)。自己发的动作去重(防 /em 回显重复进历史)。
- **采集限制**:NoLlmChannels = 跨服贝 25/65-6B + 部队 18 + 新人 1B:**后端 ChatLLMHandler 开头强制不采集**,SaveConfigJson 兜底强制这些频道 llm=false;前端 setting.html:这些频道的大模型辅助聊天勾选 disabled 置灰“(不支持)”,保存强制 false。
- **小队频道 0E 只采本队成员**:GetPartyMemberCleanNames(GroupManager EntityId → ObjectTable 反查清洗名,解析不出=不过滤防误伤)+ IsPartySelf;非本队他人的 0E 消息忽略 LLM 采集。
- **lookup_player 增强(可空)**:不带 name = 列出在场玩家(名字/种族/性别/在线状态/在你这边的方位);带 name = 单查加种族性别状态方位+是否看你(合并了“附近玩家识别种族性别”与“活点地图方位”);RP 时模型可知对方长相/方位。
- **face_player 新工具**:轻动作(转身看向某玩家,不移动),由模型在“多人的时候对谁说话就看谁”时主动调用;target 空=最近接触的人。执行在 RunInfoToolCore(走 info 回填循环,不触发补台词)。rp_body_action.face 仍在(接近/到达时自动面向)。**场景注入不加“刚才谁和你说话”**——聊天上下文里已含说话者,用户口径勿再加(2026-09-06 晚已删)。
- **前端 LLM 采集勾选修复(2026-09-06 晚)**:曾把 disabled 属性无条件加在所有频道的“大模型辅助聊天”勾选上 → 所有频道都点不动(用户反馈)。改为仅 NO_LLM_CHANNELS(跨服贝/部队/新人)条件 disabled。改前端后需 dotnet build 并重载插件(Web 文件复制到 bin)。
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

# ===== 交接记录 2026-09-07(新会话先读这一段) =====
整体目标:人设驱动 RP 机器人,能力=读聊天/回话(频道跟随+悄悄话)+ 移动内核(走/坐/绕障)+ LLM 身体演出。项目唯一 D:AuraCanAI.Dalamud;**git 已含完整历史(见 git log),改动务必 commit**;_ref/ffxiv_navmesh 为参考克隆(不入库)。

## 已完成并(大部分)实机验证
- 回复链路:随来源频道(0D→/t);拟真延迟调度(静默2~5s攒条,MaxWait12s);无人设=不触发AI;台词回显去重;**工具调用文本泄漏(XML <invoke>)=解析恢复执行**(不再整条丢弃,2026-09-06 下午2)。
- 身体演出工具集:**rp_body_action**(approach/follow/leave/face/stop/sit;sit target 支持玩家名=坐 TA 旁最近空座)、**lookup_player**(空 name=列在场玩家含种族/性别/状态/方位;带 name=单查+是否看你)、**list_seats**(near=玩家名,按距离列)、**face_player**(转身看向某人,模型在多人对话时主动调)。
- 坐椅:seatadd/seatstand/seatgo 全链路;**占用判定半径 0.25m**(一排密座中间位不再误判有人,勿调大)。
- 寻路:**NavPathPlanner**(2D 栅格 A*+LOS 拉直,纯逻辑可单测)+ MovementController 前瞻圆弧跟随(替代旧 SideArc);纯逻辑测试全过。
- 采集限制:跨服贝(25/65-6B)/部队(18)/新人(1B)不 LLM 采集(前端禁勾+后端兜底);小队 0E 只采本队成员;1C/1D 动作可回应(跟随最近普通频道),**llm 采集不默认开、按用户勾选**。

## 待办/未验证(新窗口优先)
1. 重载插件后把近期改动实机过一遍(用户会继续测):A* 绕桌、坐人旁、1C/1D 动作回应、lookup 列在场、前端采集勾选是否恢复可点(上轮已修 disabled 误加)。
2. 椅子边缘细节:多人椅占用、斜家具矩形不支持(轴对齐)。
3. 后续打磨(用户提过):人设演出细节(走/坐台词时机、表情配合、座位占用圆场)。
4. 备份 zip 待功能稳定后打((本地备份,已移除) 为旧备份)。

## 易踩点速记
- FFXIV 无"坐着"旗标;坐正=位置偏差;站着时位置≈座位点可能误判。
- /interact 对家具无效→用 /sit(实机确认)。
- Dalamud 版本 ObjectKind 无 HousingFurniture,屋内家具=HousingEventObject/EventObj。
- 工具:assistant 带 N 个 tool_calls 必须逐个 tool id 回填否则 400。
- 模型会把工具调用当台词→XML 泄漏恢复已处理;仍兜底 LooksLikeToolLeak。
- 前端 Web 文件改动需 dotnet build(复制到 bin)+ 插件禁用/启用生效。
- BODYREQ/BODYRSP/寻路日志在 %AppData%XIVLauncherCNdalamud.log。
- 占用/判定半径等小常量若实机发现不对,先问用户口径再改,勿自作主张调阈值。

# ===== 两层状态机(2026-09-12 新增,待实机验证) =====

用户口径:**不用三层,改两层**;全局多套(非按角色);第一层随便切、第二层受路径约束;待机动作按冷却自动轮换;位置不要复用场景设定的座位;第一层纯 AI 判断;位置记录「带名」与「先武装再录」都要。

## 数据模型/配置(Models.cs + Configuration.cs)
- `SmMood`(第一层=角色状态/心情:id/name/desc/scenes)、`SmScene`(第二层=情景:id/name/desc/**roleName(该情景的人设)**/actions/**nextSceneIds(路径)**)、`IdleAction`(name/emote/cooldown/**hasPos,x,y,z,yaw,territoryId**)。全部 camelCase(与前端直接对应)。
- `Configuration`: `StateMachineEnabled`(独立 RP 开关)、`SmCurrentMoodId`、`SmCurrentSceneId`、`SmMoods`(第一层列表,内含情景)。

## 核心(Core/StateMachine.cs)
- `SwitchMood` 自由切;`SwitchScene` 必须在当前第一层内,且 `nextSceneIds` 非空时只允许列出的(空=同第一层不限)。切换后 `SaveConfig` + `ResetIdle` + `ResetChatHistoryPublic`(换人设必须重建 system)。
- `Tick()`(挂 500ms `_timer1500` 链路):当前动作停留满 cooldown 秒 → 随机换同情景另一个动作(只有一个就保持);有位置先 `Movement.MoveToPoint`,到达(`!Movement.IsActive`)后再播表情;动作轮换间隔 = max(3, cooldown 或 30),**必须调用 `RoleActions.Perform(role, ra, ignoreCooldown:true)`**,否则会被角色动作冷却拦住。
- `PerformIdleAction(name)`:AI 工具/手动指定动作。
- 位置记录:`ArmPos(moodId,sceneId,name)` 武装 → 游戏内 `/aca pos`(不带名);`/aca pos 动作名` 直接按名找(先当前情景,再全局)。写入后 `SaveConfig`。
  武装不落盘(内存态),重启失效,正常。

## RP 开关重构(重要)
- 新增 `AuraCanAiCore.GetActiveRoleName()`:状态机开 → `State.CurrentSceneRole`;关 → 旧 `LLMConfig.currentRole`(兼容旧配置)。**全项目所有“当前角色”解析都改走它**(SendMsg 触发判定 / FireReply / NotifyActionHint / rp_emote / ResetChatHistory / GetCurrentRoleName)。
- `IsRolePlaying()` 改为 `GetActiveRoleName() 非空`;行为引擎 rpMode 条件随之。
- 顶部「当前角色」下拉保留:状态机关闭时仍用它。

## AI 工具(加进 infoCalls 回填循环,勿只当纯动作)
- `switch_mood(mood)`(enum=全部第一层名)、`switch_scene(scene)`(enum=当前第一层内允许的情景)、`rp_idle_action(name)`(enum=当前情景动作名)、`party_action(op,target)`(invite/accept/leave)。
- 这四个都加进了 `ProcessBodyReplyAsync` 的 infoCalls 白名单,并在 `RunInfoToolCore` 有 case;`LooksLikeToolLeak` 也加了名字。
- 场景注入(`BuildSceneSnippetCore` 末尾)追加 `State.DescribeStateForAi()`:当前第一层/第二层/人设/可切换情景/动作列表;并新增「队伍:在小队/一个人」。
- 小队变化事件:新增 `_pendingSystemHints` + `PushSystemHint()`,在 `CheckPartyStateTick` 里进队/退队各推一条,`BuildTurnMessages` 注入一轮(与 `_pendingActionHints` 同 TTL 20s);提示模型据此 `switch_mood`/`switch_scene`。

## 组队 API(FFXIVClientStructs,元数据挖出,**待实机验证**)
- 邀请:`InfoProxyPartyInvite.Instance()->InviteToPartyContentId(ulong contentId, ushort worldId)`;同副本另有 `InviteToPartyInInstanceByEntityId(uint)`;失败回退 RunCommand `/invite 名字@服务器`。
- 接受:`RespondToInvitation(string inviter, bool accept)`,邀请人读 `InviterName` / `InviterNameWithHomeworld`(先试前者,失败再试后者)。`EntryCount==0` = 没有邀请。
- 退队沿用 `InfoProxyPartyMember.LeaveParty()`。
- 入口 `AuraCanAiCore.PartyAction(op,target)`;命令 `/aca party [leave|invite 名字|accept]`。

## HTTP / 前端
- 接口:`GetStateMachine` / `SaveStateMachine` / `SetStateMachineEnabled` / `SwitchStateMachine`(body {mood?,scene?}) / `ArmIdlePos` / `ClearIdlePos`(HttpServer 已注册)。
- 前端 `Web/character.html` **顶部新增「状态机」区块**(在角色设定上方,不新开页):启用开关(独立 RP 开关)、当前状态徽章(轮询 2s 只更新徽章)、第一层页签、第二层页签、情景编辑(名称/说明/人设下拉/路径勾选/动作列表「名称/动作/冷却/位置」)、位置「记录位置(武装)/清空」、编辑即自动保存(防抖 600ms)+「保存状态机」按钮。
- 路径语义写死在 UI:**都不勾 = 同状态内不限**。
- help.html 新增「状态机(两层)」帮助节。
- ⚠️ 前端改完必须 `dotnet build`(Web 复制到 bin)+ 插件禁用/启用生效;JS 语法用 `node --check` 抽 `<script>` 检查过。

## 待实机验证点
1. 开启状态机 + 某情景选人设 → AI 应能回话(不再看「当前角色」);切情景后人设/记忆应重建。
2. `switch_mood` / `switch_scene` 工具调用是否被模型正确触发(日志 `[状态机] 第一层/第二层 →`);路径限制是否生效。
3. 待机动作自动轮换:cooldown 到点换动作;有位置应先走位再表情;`/aca idle 名称` 手动测。
4. `/aca pos` 记录位置(带名 / 先武装)是否落到正确动作并能在网页显示(点「刷新」)。
5. `/aca party invite 名字`、`/aca party accept`:**InviteToPartyContentId / RespondToInvitation 的签名/参数语义必须实测**;失败看 xllog `[组队]` 行,必要时改回纯命令方案。

## 前端改版(2026-09-12 追加,用户口径)
- 前端**不要那么多字**:已删掉长篇说明,顶部只留「启用状态机 / 当前状态徽章 / 刷新 / 保存」。
- **默认状态机示例**:`Defaults.DefaultStateMachine()` = 1 个第一层「平常」+ 2 个第二层「待机/对话」,路径互指 → 节点图上呈**三角形**。`AuraCanAiCore` 构造里 `if (_config.SmMoods.Count == 0)` 种入并保存(删光后会再次种入,属预期)。
- 视图改为 **SVG 节点图**(`#smGraph` + `smDrawGraph()`):第一层节点在上、其第二层节点在下,树边为实线、路径为虚弧线;蓝底=当前状态、浅蓝=选中;点节点在下方面板编辑(名称/说明/人设/路径/动作)。去掉了原来的 `#smMoodBar`/`#smSceneBar` 页签与 `#smMoodEditor`/`#smSceneEditor`,统一为 `#smEditor`。新增 `#smAddMoodBtn`(映射 `smAddMood`)/`#smAddSceneBtn`。
- 图每次 `smRender`/输入改名/轮询都会重画(轮询 2s 只更新徽章+高亮,不动表单,避免抢焦点)。

## 状态机默认与触发口径(2026-09-12 二次调整,用户口径)
- **默认状态机**改为:第一层「皮下」(含 待机/接待)+「皮上」(含 待机/对话);皮下情景绑人设「皮下」、皮上绑「白屿涟音」。默认当前 = 皮下/待机。
  - `IsLegacyDefaultStateMachine()`:SmMoods 为空,或仍是旧默认(单个「平常」+待机/对话、无动作无人设)→ 种入新默认(用户已改造过的不会被动)。
- **默认人设加了第二份「皮下」**(`Defaults.DefaultRoleSettingSubskin`):一个普通国服 FF14 玩家的口吻(口语、聊游戏本身、不演角色)。
  迁移:`Configuration.DefaultRolesV2Added`(只做一次)——角色列表里没有「皮下」就补上。
- **说话开关口径**:`AuraCanAiCore.ShouldTriggerAi()` —— 状态机开启 → **只看「启用状态机」开关**,当前情景人设可空也能回话(空人设 = 不演角色,走 `RunChatTurnAsync` 的纯文字分支);状态机关闭 → 旧行为(「当前角色」有 setting 才回话)。
  - `IsRolePlaying()` 改为 `StateMachineEnabled || 当前生效角色名非空`。
  - `SendMsg` / `NotifyActionHint` 都改用 ShouldTriggerAi;`BuildRequestMessages`(无人设纯文字路径)也补上了场景+状态+事件提示注入(以前只有攒条提醒)。
  - ⚠️ 纯文字分支**不带工具**(switch_mood/switch_scene/rp_idle_action 都不可用),所以空人设情景无法自主切状态——这是按“不演角色”口径的取舍;要切状态就给人设。
- **节点图**:第一层/第二层之间加**横向虚线分隔**,左边缘标「状态」「情景」;节点布局左侧留 58px 给标签。默认示例不再是三角形(现在是两组节点)。

## 节点图:第一层虚线(2026-09-12)
- 第一层之间 AI 可自由切换 → 用**虚线**把相邻「状态」节点连起来(与情景间路径同一视觉语义:虚线=可切换)。
- `smDrawGraph` 里收集 `moodNodes`,相邻两个之间 push `type:'moodlink'` 边,渲染成 `#7aa7d8` 虚线直线(y=状态行中线)。

## 状态机前端精简(2026-09-12 三次)
- 移除「刷新」按钮。「设为当前」按钮(状态/情景)也移除——状态切换交给 AI。
- 位置/数据变化的回显改为 **轮询静默重载**:`pollStateMachineCurrent` 里比较 `JSON.stringify(r.moods)` 与本地,不同且 `!smEditing && !smSaveTimer` 时整体重载并重渲编辑器(游戏内 `/aca pos` 后 2 秒内自动显示)。
- `smSave` 的 doSave 里把 `smSaveTimer = null`(供上面的“无待保存”判断)。
- 后端 `SwitchStateMachineJson` 接口保留(UI 不再调用,留给调试)。

## 多套状态机(2026-09-12,用户要求,UI 参考「场景设定」的房子页签)
- 新模型 `SmSet { id, name, moods }`;`Configuration.SmSets` + `SmCurrentSetId`。
  - 旧字段 `Configuration.SmMoods` 保留为**迁移用**(启动时 `EnsureStateMachineState()` 把旧单状态机收进 `SmSets` 一套「状态机1」后清空,新代码不再读写它)。
  - `EnsureStateMachineState()`(构造早期调用,早于 `StateMachine` 构造):迁移旧单套 → 无套则种默认示例 → 唯一且旧默认(平常)则换新默认 → 校准 currentSet/currentMood/currentScene。变动落盘。
- `StateMachine`:新增 `CurrentSet`;`CurrentMood/CurrentScene`/`SwitchMood/SwitchScene`/`AllowedScenes`/`RecordPosition`/`ClearPosition`/`DescribeStateForAi` 全部改为在当前 Set 内查。
  - `ArmPos(setId, moodId, sceneId, name)`(多了 setId);`ArmedSetId`。
- HTTP:`GetStateMachine` 返回 `sets/currentSetId/...`;`SaveStateMachine` body = `{enabled,currentSetId,sets[]}`(对每套做与以前同样的清洗:无名剔除、id 去重、nextSceneIds 过滤);新增 `SetCurrentStateMachine {id}`(切当前套并重置 idle+上下文)。`ArmIdlePos`/`ClearIdlePos` 都要带 `setId`。
- 前端:新增顶部**状态机页签** `#smSetBar`(按钮 + ✎重命名 + ✕删除 + 新建,样式同场景设定房子页签);点页签 = 选中并切为当前套(`/SetCurrentStateMachine`)。节点图/编辑面板都基于 `smActiveSet()`。保存带 `currentSetId: smSelSet`。删除时至少保留一套。
- 运行时只使用「当前套」;跨套切换目前**只能手动点页签**(AI 工具不涉及跨套)。

## 两处小改(2026-09-12)
- 示例/默认状态机套名:「默认」→「**白屿涟音**」(`EnsureStateMachineState` 播种 + `SaveStateMachineJson` 兜底;并把已存在的唯一「默认」套改名)。
- 角色删除校验:`character.html` 删角色前先查 `smRoleInUse(角色名)`,若被任何状态机情景引用则拒绝并提示(列出处);角色**改名**时 `smRenameRoleRefs()` 同步状态机里的 roleName 引用并保存,避免绑定静默失效。
- 跨套切换:用户明确「不希望从一个状态机切到另一个」→ 保持现状(仅前端页签手动切,无 AI 工具)。

## 套名迁移补漏 + /aca smreset(2026-09-12)
- `EnsureStateMachineState` 的套名改名规则扩为:单套且名为「默认」**或「状态机1」** → 改名「白屿涟音」(之前只改「默认」,迁移来的「状态机1」漏了)。
- 新增 `/aca smreset`(`AuraCanAiCore.ResetStateMachine()`):把状态机重置为默认示例(单套「白屿涟音」= 皮下/皮上);命令帮助串已加。
- 已直接修好用户配置里的套名(仅改 name,未动 moods)。

## 「不自动上皮」修复(2026-09-12,用户看日志发现)
- 现象:用户说「陪我磨磨皮?」「就是上皮陪我角色扮演一下」,模型全程不调 `switch_mood`,还以皮下口吻拒绝。
- 根因:①`DescribeStateForAi` 的切换指引只举了「开心/受伤/被冷落」这类**情绪**例子,没告诉模型「处境/身份」也包括“被要求上皮/出戏”,模型无法把“上皮”映射到 switch_mood;②皮下人设本身写着“不演角色”,没人纠正就顺着拒绝;③即使切了,本轮请求已经带着旧人设的 system,回复仍是旧口吻。
- 修复:
  1. `StateMachine.DescribeStateForAi` 用法段重写:明说“处境/身份/心情变了就切;‘变了’以各状态 desc 为准(含角色扮演/上皮 vs 皮下/出戏);**别人要求切身份/进入角色/出戏时不要拒绝、不要反问,直接调工具切过去**”。
  2. `switch_mood` 工具描述同步(加上“别人要求时不要拒绝直接切”)。
  3. `AuraCanAiCore.BuildPersonaSystemMessage()` 抽出(ResetChatHistory 复用);`ProcessBodyReplyAsync` 工具轮里若出现 `switch_mood`/`switch_scene`,把 `msgs[0]`(persona system)**就地换成新人设**,使**本轮**即按新状态回应(日志「状态切换后人设已刷新」)。
- ⚠️ 同轮限制:工具枚举是请求时生成的,所以“要求上皮”的那一轮 `switch_scene` 枚举还是旧状态的情景;要切情景/顶阶情景需下一轮(模型会看到新枚举)。

## 「坐下去又站起来」修复(2026-09-12,用户看日志发现)
- 日志:AI `rp_body_action(sit)`(无 target)→ 选「全息显示器」→ `/sit`(12:45:06.244)→ 报「第1次坐正(偏差 0.00m)」→ 结束。
- 根因:**`/sit` 是开关**。若角色本来已经坐着(用户手动坐下/上一次坐姿未解除),再发 `/sit` 会**站起身**;而旧的成功判定**只看位置**(人站在座位点,偏差同样 0.00m)→ 假报“坐正”,人留在站立状态。
- 关键新工具:`AuraCanAiCore.GetSeatedState()` —— 用 `EmoteController.GetPosture()`(`SittingInChair`/`SittingOnGround`/`Dozing`),返回 `bool?`(null=签名不可用)。FF14 没有简单“坐着”旗标,这是唯一可靠判定;比位置推断准。
- MovementController 座流程改:
  - `TriggerSit`:**已坐着 → 跳过 /sit**(不发就不会站起)。
  - `Confirm`:成功 = 位置≤0.35m **且** `GetSeatedState()!=false`(null 时回退位置判定);位置到了但没坐下 → **原地重发 /sit**(站着→坐下);人还站着且不在座位点 → 回 `Walk` 重走(不再乱发 /sit 坐半路);只有**真的坐着但歪**才走旧的“站起→走→重坐”。
- ⚠️ 待实机:`GetSeatedState()` 的 MemberFunction 是否可用(日志「坐正(... 坐下=True)」);若恒为 null,则回退位置判定(至少不再误判“已坐却站起”仍能受益于 TriggerSit 的跳过逻辑?——不会:null 时不跳过。属待验证项)。

## 「坐错边(左右不分)」修复(2026-09-12,用户实测)
- 日志实证 `GetSeatedState()` 可用(「坐正(偏差 0.00m,坐下=True)」),且「已经坐着,跳过 /sit」已生效 → 上一个修复有效。
- 用户测试:说「请坐在我右边」,AI 调 `rp_body_action(sit)`(**没带 target**)→ 坐了最近的座(玩家的左边)。
- 根因:`list_seats(near=玩家)` 只给「距该玩家 X.Xm」,**没有左右方位**,模型无法挑“那人的右边”;而 `GetLookingDirection` 是相对**自己**的。
- 修复:
  - `list_seats(near=X)` 改成输出「在{X}{方位}(有人/空)」,方位 = `GetLookingDirection(座位, X.Position, X.Rotation)`(相对**那个玩家面朝方向**的左/右/前/后;算法已验证左右不翻)。
  - 末尾提示、`list_seats` 工具描述、`rp_body_action.target` 描述、场景注入句都补上:「要坐某人左/右边 → 先 list_seats(near=那人) 看方位,再挑座位名/#id」。
- ⚠️ 待实机:再说「坐我右边」,模型应先调 `list_seats(near=…)` 再 `sit` 到正确侧的座位。

## 坐位「交给程序选」(2026-09-12,用户口径)
- 用户反馈:模型会自己在台词里念「右边，那必须是 #4 了，坐下了啊」——把选座逻辑/编号写进台词,很假。
- 口径:**模型只传方位参数,程序自己找座,只告诉模型“坐到了什么样的位置”**。
- 实现:
  - `rp_body_action` 新增可选参数 **`side`**(enum: left/right/front/back/near),仅 `sit` 且 `target` 是玩家时用。
  - `AuraCanAiCore`:新增 `NormalizeSide()`(中英方位归一化)、`RelativeAngleDeg()`(相对朝向夹角,+右-左)、`SeatOnSide()`(22.5/157.5 扇区判定)、`DescribeSeatSide()`(如 “奥·乌儿的右侧 1米”)。
  - `ResolveSeatForSitting(selector, side, out error)` 新重载(旧的保留,`/aca seatgo` 走它):target=玩家名 + side → 先在该侧的空座里取最近的;该侧没空座则**退回最近空座**(结果里会说明实际方位)。
  - `MovementController.SitOnSeat(selector, side="")`;新增 `CurrentSitSeat` 供上层描述。
  - `ExecuteBodyAction(action, target, side="")`;`ProcessBodyReplyAsync` 解析 `side`。
  - **工具结果不再给座位编号**:只回「开始走到 {方位} 的空座坐下」,避免模型念 #4。
  - 提示同步:`rp_body_action` 描述/target/side 说明、场景注入句、`OutputFormatRule` 新增「禁止说出座位编号/工具名/参数/过程描述」。
  - `list_seats(near=X)` 仍保留“在 X 的哪侧”输出(其他用途),但坐位主路径已不要求模型查它。
- ⚠️ 待实机:说「坐我右边」→ 模型应调 `rp_body_action(sit, target=奥·乌儿, side=right)`,日志 `去坐 #N` 由程序挑,台词里不应出现 #编号。

## 「皮下」人设改版(2026-09-12,用户口径:温柔+御姐+死宅+上班族,少点人机味)
- 旧版问题:偏冷硬(“有话直说”“不装熟、不过度热情”),像助手/客服,人机味重。
- 新版 `Defaults.DefaultRoleSettingSubskin`:普通上班族、老玩家、说话带姐姐的从容与温吞;温柔有耐心、有点懒;
  死宅梗(新番旧番/单机手游/抽卡/同人);上班族吐槽(通勤/会议/摸鱼/月底忙);会自嘲;多语气词与口语;
  并加了「别这样」段:不冷冰冰噎人、不列点总结、不“有什么可以帮您”、允许平淡走神。
- 迁移:`Configuration.SubskinPersonaV2`(只做一次)——找到角色「皮下」,若其 setting 仍含旧标记 `该上线上线`,
  就换成新版;用户自己改过的不动。
- 已同步直接更新用户配置文件里的「皮下」人设(整份 config JSON 重写了一遍,已校验 SmSets/Key 都在)。

## 台词拆条 + 少提问 + 切状态顺带选情景(2026-09-12,用户看日志要求)
- **台词多条拆发**:`AppendAssistantAndEcho` → `await AppendAssistantAndEchoAsync`(所有调用点已加 await)。
  - `SplitOutgoingLines`:先按换行分段,再按句末标点(。！？!?…)拆,单条尽量 ≤ `LineMaxLen=45`,一轮最多 `LineMaxCount=3` 条(再多并进最后一条);条间 `LineGapMs=650`。
  - **历史里只存整段一条**(避免模型看到自己被拆成多条);发送时逐条 `_recentSelfLines` 记入,回显去重改为按列表匹配(原 `_lastLlmEchoContent/_lastLlmEchoAt` 已删)。
- **少提问**:`Defaults.DefaultRoleSettingSubskin` 的「别这样」加“别句句都问问题:一轮最多留一个小问题…”。迁移标记 `Configuration.SubskinPersonaV3`(只在仍是上一版默认时替换)。已直接更新配置文件。
- **切状态顺带选情景**:
  - `StateMachine.SwitchMood(key, sceneKey="")`:可同时指定情景;不传=第一个情景。
  - `switch_mood` 工具新增可选 `scene` 参数;场景注入列出「每个状态 → 它的情景」,并提示“和人互动选对话/接待,独自用待机,一步切到位”。
  - `RunInfoToolCore` 的 `switch_mood` 解析 `scene`。
- ⚠️ 待实机:长句应变多条(日志 `LLM 台词已发(/p,1/2)…`);切皮上时应落在「对话」而不是「待机」。
