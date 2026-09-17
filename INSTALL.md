# AuraCanAI 安装教程

> 适用于国服 FF14 + 卫月(XIVLauncherCN)。全程 3 步,2 分钟。

## 前置条件

- 已安装 **XIVLauncherCN(卫月)** 并能正常启动游戏
- Windows 系统

---

## 第 1 步:添加插件源

游戏内打开卫月设置:

```
/xlsettings
```

切到 **「插件仓库 / Plugin Repositories」** 页签,在最下面的 **「自定义插件仓库 / Custom Plugin Repositories」** 输入框里粘贴这一行:

```
https://raw.githubusercontent.com/raine01/AuraCanAI/main/pluginmaster.json
```

点输入框右边的 **`+`** 添加,然后保存关闭。

## 第 2 步:安装

打开 **「插件安装器 / Plugin Installer」**,在搜索框输入 `AuraCanAI`,找到后点 **「安装 / Install」**。

## 第 3 步:启用

安装完成后,在插件列表里找到 **AuraCanAI**,把开关打开(变绿/变蓝即启用)。

---

## 首次使用

| 想做什么 | 怎么做 |
|---|---|
| 打开主面板 | 游戏内输入 `/aca` |
| 用 AI 对话 | 先到 [DeepSeek 平台](https://platform.deepseek.com) 申请 API Key,再在 `/aca setting` 里填入 |
| 用网页控制台调人设 | 浏览器打开 `http://localhost:8051/` |
| 看完整说明 | 网页里的「帮助」页,或 [README](README.md) |

---

## 更新

插件安装器里出现 **「更新 / Update」** 按钮时点一下即可,不用重新添加插件源。

---

## 常见问题

**搜不到 AuraCanAI?**
GitHub 有几分钟 CDN 缓存,等 1~2 分钟点一下刷新;仍不行看 `/xllog` 日志里插件仓库的报错。

**以前用 Dev Plugin Locations 装过?**
先去 卫月设置 → **Experimental** → **Dev Plugin Locations**,把旧的 `bin\Debug` 那条移除,否则会和已安装版本冲突。

**`/aca` 没反应?**
确认插件已启用;或看 `/xllog` 里有没有加载报错。

**AI 不回话?**
检查三点:① DeepSeek Key 是否填了 ② 角色设定页是否选了角色(或开了状态机) ③ 消息设置里对应频道是否勾了「LLM 采集」。

---

## ⚠️ 使用前请阅读

- 开启 AI 功能后,**你勾选频道的聊天内容会发送到 DeepSeek 的服务器**
- 聊天记录会保存在插件目录的 `chatlogs/` 文件夹(未加密)
- 本插件含**自动化功能**,请自行评估风险并遵守游戏用户协议详细说明见 [README](README.md) 的「数据与隐私」「免责声明」。
