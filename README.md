# AuraCanAI

FF14 国服 **卫月(Dalamud / XIVLauncherCN)** 插件:游戏内 AI 角色扮演助手。

在游戏里挂上一个可以自己说话、自己做动作、自己走路的 AI 角色,配一个本地网页控制台来调人设、看聊天记录、剪歌单。

> 本插件是 **自建插件源** 分发,不在官方插件仓库中(原因见下方「免责声明」)。

---

## 功能

### 游戏内面板(`/aca`)

| 页签 | 说明 |
|---|---|
| **附近玩家** | 场景玩家列表(名字@服务器、种族/性别、在线状态),可按状态过滤,点击行复制名字 |
| **活点地图** | 1:1 正方形区域内显示附近玩家位置;自己金色、正在看你的人橘黄、其他人蓝;带名字模糊查询 |
| **玩家备注** | 给玩家记多行备注,**绑定 ContentId 防改名**;备注过的玩家名字前有 ★ |
| **回忆检索** | 按角色名检索历史聊天记录 → AI 归纳大话题 → 点进去看该时段全部对话 |
| **行为设置** | 条件触发宏自动化:条件满足时触发宏 / 在指定频道发言 / 选中目标 |
| **演奏** | 导入 MIDI 自动演奏,支持双开合奏(主/辅端分轨) |

### AI 角色扮演

- **状态机**:任意数量的「状态」,每个状态绑自己的人设、说明(换过去的触发条件)、可切换目标、可用工具
- **LLM**:DeepSeek 大模型,支持工具调用(身体动作 / 看人 / 走近 / 组队 / 离开 / 换身份)
- **拟真节奏**:按对方说话节奏延迟回话,台词按句拆分、按字数模拟打字时间
- **频道跟随**:按对方说话的频道原路回复(小队/团队/跨服贝/悄悄话)
- **自定义动作**:给角色配 `/em` 文字 + 游戏表情 + 冷却,AI 可主动执行

### 其他

- **TTS**:System.Speech(SAPI)中文语音播报聊天内容,多线程串行队列
- **聊天记录**:分频道开关,按周归档,支持全文检索
- **移动内核**:走近 / 跟随 / 走开 / 到点 / 坐下,带寻路与避障、卡住自动结束、用户按键打断
- **本地网页控制台**:`http://localhost:8051/`,手机/电脑浏览器都能开

---

## 环境要求

- **国服 FF14 + 卫月(XIVLauncherCN)**,Dalamud API Level **15**
- Windows(x86-64)。TTS 依赖 Windows SAPI,系统需装中文语音包
- 若要使用 AI 功能:自备 **DeepSeek API Key**(在 [platform.deepseek.com](https://platform.deepseek.com) 申请)

---

## 安装

### 方式一:添加自定义插件仓库(推荐)

1. 游戏内输入 `/xlsettings` 打开卫月设置 → **插件仓库(Plugin Repositories)** 页签
2. 在「自定义插件仓库」里填入:

   ```
   https://raw.githubusercontent.com/raine01/AuraCanAI/main/pluginmaster.json
   ```

3. 点 `+` 保存 → 回到 **插件安装器** 搜索 `AuraCanAI` → 安装
4. 安装后在插件列表里**启用**它

### 方式二:手动安装

1. 到 [Releases](https://github.com/raine01/AuraCanAI/releases) 下载最新 `latest.zip`(不要解压)
2. 卫月设置 → **Experimental** → **Dev Plugin Locations** → 添加该 zip 所在目录
3. 插件列表里启用 `AuraCanAI`

> 更新:方式一在插件安装器里点更新;方式二重新下载 zip 覆盖即可。

---

## 首次配置

1. **填 DeepSeek API Key**:游戏内 `/aca setting` 打开设置面板,或打开网页控制台 → 设置页 → 填入 Key。Key 独立保存,**不会被「还原默认角色」重置**
2. **选人设**:网页 → 角色设定页,默认内置三个人设:「白屿涟音」(角色扮演)、「皮下」(当个普通玩家)、「被当成AI」(顺着演)。也可以自己写
3. **开状态机**(可选):角色设定页顶部「启用状态机」开关;打开后 AI 按状态的「说明」自己切换身份
4. **选频道**:消息设置页勾选哪些频道要采集给 AI(只有勾了 LLM 采集的频道才会被处理)

---

## 指令速查

| 指令 | 说明 |
|---|---|
| `/aca` | 打开主面板 |
| `/aca setting` | 设置面板 |
| `/aca list` | 附近玩家 |
| `/aca map [名字]` | 活点地图 |
| `/aca note` | 查看/编辑当前选中玩家的备注 |
| `/aca search [名字]` | 回忆检索 |
| `/aca behavior` | 行为设置 |
| `/aca music` | 演奏(MIDI) |
| `/aca macro N` | 触发宏(`sN` 为共享宏) |
| `/aca action [名称]` | 执行当前角色的自定义动作 |
| `/aca face` / `approach` / `follow` / `leave` `[名字]` | 看向 / 走近 / 跟随 / 走开 |
| `/aca move x y z` / `/aca stop` | 移动到坐标 / 停止移动 |
| `/aca seatadd` / `seatstand` / `seatgo` | 记录坐点 / 校准站定点 / 去坐 |
| `/aca party [leave\|invite 名字\|accept]` | 小队操作 |

---

## 🔒 数据与隐私(请务必阅读)

使用本插件前请知悉以下数据处理行为:

1. **聊天内容会上传到 DeepSeek**
   开启 AI 功能后,被你勾选频道的聊天内容会作为上下文 **发送到 DeepSeek 的 API 服务器**(`api.deepseek.com`)。不要在不希望出境的频道上开启 LLM 采集。

2. **聊天记录会落盘**
   聊天记录默认写入插件目录下的 `chatlogs/` 文件夹(按周分文件)。该目录**未加密**,不要把它同步到网盘或提交到 git。

3. **本地网页服务无鉴权**
   网页控制台只监听 `localhost:8051`(不会暴露到局域网),但**没有任何身份校验**。本机上的其他程序、以及浏览器里打开的网页,理论上都能访问它的接口。请勿在不可信环境使用;**DeepSeek Key 会被网页读取**,建议只在自己的机器上使用。

4. **玩家备注 / 场景设定 / 行为设置**等数据都存在卫月插件配置目录,仅本机。

5. 本插件**不内置任何 API Key**,也不会上传你的任何数据到作者服务器(作者没有服务器)。

---

## ⚠️ 免责声明

- 本插件是第三方游戏辅助工具,**使用即代表你自行承担风险**,包括但不限于账号被处罚的可能。请自行评估并遵守 Square Enix 的用户协议。
- **本插件包含自动化功能**(条件触发宏、自动发言、自动移动、自动演奏)。卫月官方插件仓库明确不接受此类「无用户直接交互即与服务器交互」的插件,因此本插件**以自建插件源分发,不提交官方仓库**。这是作者的主动选择,不是审核未通过。
- AI 生成的所有文本由使用者负责。请勿用于骚扰、冒充他人或任何违反游戏规则与公序良俗的用途。
- 软件按 MIT 许可「按原样」提供,不提供任何担保。

---

## 从源码构建

```bash
git clone https://github.com/raine01/AuraCanAI.git
cd AuraCanAI
dotnet build -c Release
```

产物:

```
bin/Release/AuraCanAI.Dalamud/latest.zip   # 可分发的插件包
```

需要 .NET SDK(项目使用 `Dalamud.NET.Sdk/15.0.0`,目标框架 `net10.0-windows`),以及本机已安装卫月(`$(AppData)\XIVLauncherCN\addon\Hooks\dev\` 下有 Dalamud 引用程序集)。

### 发布新版本(维护者)

```powershell
# 1. 改 AuraCanAI.Dalamud.csproj 里的 <Version>
# 2. 构建 + 同步 pluginmaster.json(AssemblyVersion / LastUpdate)
powershell -ExecutionPolicy Bypass -File tools\release.ps1
# 3. 把 bin\Release\AuraCanAI.Dalamud\latest.zip 上传到同名 tag 的 GitHub Release
```

> 版本号规则:必须**固定**(同一份代码永远产出同一版本号),不允许用时间戳或自增 build 号。
> 下载地址使用 `releases/latest/download/latest.zip`,因此每次发版**不需要**改 pluginmaster.json 里的链接。

---

## 许可

[MIT](LICENSE) © 2026 ThousanRaine

第三方依赖:

- [Melanchall.DryWetMidi](https://github.com/melanchall/drywetmidi) — MIT
- [System.Speech](https://www.nuget.org/packages/System.Speech) — MIT
