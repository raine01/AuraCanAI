using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AuraCanAI.Dalamud.Core;

/// <summary>
/// 游戏聊天/命令发送(纯卫月内置,与 ChatTwo 一致):
/// 直接创建 Utf8String 调 UIModule.ProcessChatBoxEntry,不碰输入框、saveToHistory=false。
/// 须在游戏框架线程调用;所有异常已捕获,失败返回 false,不会导致游戏崩溃。
/// </summary>
public static class GameCommandSender
{
	/// <summary>发送聊天消息或游戏命令(如 "/p 你好"、"/e 测试")。返回是否成功。</summary>
	public static unsafe bool Send(string commandOrMessage)
	{
		if (string.IsNullOrWhiteSpace(commandOrMessage)) return false;
		Utf8String* mes = null;
		try
		{
			mes = Utf8String.FromString(commandOrMessage);
			UIModule.Instance()->ProcessChatBoxEntry(mes, 0, false);
			return true;
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"发送消息失败: {e.Message}");
			return false;
		}
		finally
		{
			if (mes != null)
			{
				try { mes->Dtor(true); } catch { }
			}
		}
	}
}
