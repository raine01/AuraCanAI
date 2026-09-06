using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;

namespace AuraCanAI.Dalamud.Core;

/// <summary>本地 HTTP + WebSocket 服务(端口 8051),提供网页控制台</summary>
public class HttpServer : IDisposable
{
	private HttpListener? _listener;
	private WebSocket? _webSocketClient; // 网页客户端(单)
	private readonly object _webSocketLock = new();
	private readonly ConcurrentQueue<WebSocketMessage> _webSocketMsgQueue = new();
	private readonly Dictionary<string, Func<string, object>> _methods;
	private readonly AuraCanAiCore _core;
	private readonly Dictionary<string, string> _staticPages = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, byte[]> _staticAssets = new(StringComparer.OrdinalIgnoreCase);
	private readonly int _port;
	private readonly string _webDir;
	private volatile bool _running;

	/// <summary>静态资源 Content-Type 映射</summary>
	private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
	{
		{ ".jpg", "image/jpeg" },
		{ ".jpeg", "image/jpeg" },
		{ ".png", "image/png" },
		{ ".gif", "image/gif" },
		{ ".webp", "image/webp" },
		{ ".ico", "image/x-icon" },
		{ ".svg", "image/svg+xml" },
		{ ".css", "text/css; charset=utf-8" },
		{ ".js", "application/javascript; charset=utf-8" },
		{ ".woff2", "font/woff2" },
		{ ".woff", "font/woff" },
		{ ".ttf", "font/ttf" },
	};

	/// <summary>收到 WebSocket 消息时回调(用于 GetChats 返回队列数据)</summary>
	public ConcurrentQueue<WebSocketMessage> MessageQueue => _webSocketMsgQueue;

	public HttpServer(int port, string webDir, Dictionary<string, Func<string, object>> methods, AuraCanAiCore core)
	{
		_port = port;
		_webDir = webDir;
		_methods = methods;
		_core = core;
		_methods["GetConfig"] = _ => _core.GetConfigJson();
		_methods["SaveConfig"] = _core.SaveConfigJson;
		_methods["DeleteConfig"] = _core.DeleteConfigJson;
		_methods["GetLLMConfig"] = _ => _core.GetLlmConfigJson();
		_methods["SaveLLMConfig"] = _core.SaveLlmConfigJson;
		_methods["DeleteLLMConfig"] = _core.DeleteLlmConfigJson;
		_methods["GetChats"] = _core.GetChatsJson;
		_methods["SendMessage"] = _core.SendMessageJson;
		_methods["SendTell"] = _core.SendTellJson;
		_methods["ListDirectory"] = _core.ListDirectoryJson;
		_methods["GetPlaylists"] = _ => _core.GetPlaylistsJson();
		_methods["SavePlaylists"] = _core.SavePlaylistsJson;
		_methods["PlaylistPlay"] = _core.PlaylistPlayJson;
		_methods["PlaylistControl"] = _core.PlaylistControlJson;
		_methods["PlaylistStatus"] = _ => _core.PlaylistStatusJson();
		_methods["ListMidiFiles"] = _core.ListMidiFilesJson;
		_methods["GetMidiTracks"] = _core.GetMidiTracksJson;
		_methods["SearchChatHistory"] = _core.SearchChatHistoryJson;
		_methods["GetSeats"] = _ => _core.GetSeatsJson();
		_methods["SaveSeats"] = _core.SaveSeatsJson;
		_methods["GetSeatMap"] = _ => _core.GetSeatMapJson();
		_methods["SaveHouses"] = _core.SaveHousesJson;
		_methods["SetCurrentHouse"] = _core.SetCurrentHouseJson;
		_methods["SaveObstacles"] = _core.SaveObstaclesJson;
		LoadStaticPages();
	}

	private void LoadStaticPages()
	{
		try
		{
			foreach (var file in Directory.GetFiles(_webDir, "*.html"))
			{
				var name = Path.GetFileName(file);
				_staticPages[name] = File.ReadAllText(file, Encoding.UTF8);
			}
			var nav = Path.Combine(_webDir, "nav.html");
			var footer = Path.Combine(_webDir, "footer.html");
			_staticPages["_nav"] = File.Exists(nav) ? File.ReadAllText(nav, Encoding.UTF8) : "";
			_staticPages["_footer"] = File.Exists(footer) ? File.ReadAllText(footer, Encoding.UTF8) : "";
			// 静态资源(图片/字体等),路径相对 Web 目录
			foreach (var file in Directory.GetFiles(_webDir, "*.*", SearchOption.AllDirectories))
			{
				var ext = Path.GetExtension(file).ToLowerInvariant();
				if (ext is ".html" or ".htm") continue;
				if (!ContentTypes.ContainsKey(ext)) continue;
				var rel = Path.GetRelativePath(_webDir, file).Replace('\\', '/');
				_staticAssets[rel] = File.ReadAllBytes(file);
			}
			Plugin.Log?.Information($"已加载 {_staticPages.Count - 2} 个网页文件,{_staticAssets.Count} 个静态资源");
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"加载网页文件失败: {e.Message}");
		}
	}

	/// <summary>启动服务;若旧服务占用端口则先请求其停止</summary>
	public bool Start()
	{
		// 尝试停止可能残留的旧实例(如旧版插件/ACT 版)
		try
		{
			using var wc = new HttpClient();
			wc.GetStringAsync($"http://localhost:{_port}/stop").Wait(2000);
			Thread.Sleep(500);
		}
		catch { /* 没有旧服务 */ }

		try
		{
			_listener = new HttpListener();
			// 只绑定 localhost,避免需要管理员权限(URLACL)
			_listener.Prefixes.Add($"http://localhost:{_port}/");
			_listener.Start();
			_running = true;
			_ = Task.Run(AcceptLoop);
			Plugin.Log?.Information($"AuraCanAI 网页服务已启动: http://localhost:{_port}/");
			return true;
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"网页服务启动失败(端口 {_port}): {e.Message}");
			return false;
		}
	}

	private async Task AcceptLoop()
	{
		while (_running && _listener != null)
		{
			try
			{
				var context = await _listener.GetContextAsync();
				_ = Task.Run(() => HandleContext(context));
			}
			catch (HttpListenerException ex) when (ex.ErrorCode == 995) { break; } // 正常停止
			catch (ObjectDisposedException) { break; }
			catch (Exception e)
			{
				Plugin.Log?.Error($"请求异常 [{e.GetType().Name}]: {e.Message}");
			}
		}
	}

	private async Task HandleContext(HttpListenerContext context)
	{
		try
		{
			if (context.Request.IsWebSocketRequest)
			{
				await HandleWebSocket(context);
				return;
			}
			var path = context.Request.Url?.AbsolutePath.Trim('/') ?? "";
			var httpMethod = context.Request.HttpMethod;

			if (path.Equals("stop", StringComparison.OrdinalIgnoreCase))
			{
				SendResponse(context, "{\"message\":\"服务停止中...\",\"result\":\"success\"}", "application/json");
				Stop();
				return;
			}

			if (httpMethod == "POST")
			{
				if (_methods.TryGetValue(path, out var method))
				{
					using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
					string body = reader.ReadToEnd();
					object result;
					try { result = method(body); }
					catch (Exception e) { result = new { message = e.Message, result = "error" }; }
					SendResponse(context, JsonConvert.SerializeObject(result), "application/json");
				}
				else
				{
					SendResponse(context, "<h1>404 - 没这方法</h1>", status: 404);
				}
			}
			else if (httpMethod == "GET")
			{
				if (string.IsNullOrEmpty(path)) path = "index.html";
				if (!_staticPages.TryGetValue(path, out var html))
				{
					// 静态资源(图片等)
					if (_staticAssets.TryGetValue(path, out var bytes))
					{
						var ext = Path.GetExtension(path);
						var ct = ContentTypes.TryGetValue(ext, out var c) ? c : "application/octet-stream";
						SendBytes(context, bytes, ct);
						return;
					}
					SendResponse(context, "ミ( ' ▽ ' )彡<br>是有活力的你呀！呜啪！", status: 404);
					return;
				}
				html = html.Replace("<!-- 导航栏 -->", _staticPages["_nav"])
						   .Replace("<!-- 页脚 -->", _staticPages["_footer"]);
				// 动态占位符(端口/路径修改后实时反映到帮助页等)
				html = html.Replace("{{webUrl}}", _core.WebUrl)
						   .Replace("{{port}}", _core.HttpPort.ToString())
						   .Replace("{{logPath}}", _core.ChatLogPath);
				// 行为语法单一数据源:帮助页说明/条件表格由 BehaviorSyntaxDoc 动态生成(与解析器、AI 助手提示同步)
				html = html.Replace("{{behaviorSyntax}}", BehaviorSyntaxDoc.BuildSyntaxHtml())
						   .Replace("{{behaviorSayChannels}}", BehaviorSyntaxDoc.BuildSayChannelsHtml())
						   .Replace("{{behaviorVariables}}", BehaviorSyntaxDoc.BuildSayVarsHtml())
						   .Replace("{{behaviorConditions}}", BehaviorSyntaxDoc.BuildConditionsTableHtml());
				SendResponse(context, html);
			}
		}
		catch (Exception e)
		{
			SendResponse(context, $"<h1>500 - 服务器错误</h1><p>{e}</p>", status: 500);
		}
	}

	private static void SendResponse(HttpListenerContext context, string content, string contentType = "text/html; charset=utf-8", int status = 200)
	{
		try
		{
			var response = context.Response;
			response.StatusCode = status;
			byte[] buffer = Encoding.UTF8.GetBytes(content);
			response.ContentType = contentType;
			response.ContentLength64 = buffer.Length;
			// 页面/接口都是动态内容,禁止浏览器缓存(避免改文件后刷新没反应)
			response.Headers["Cache-Control"] = "no-store";
			response.OutputStream.Write(buffer, 0, buffer.Length);
			response.Close();
		}
		catch { /* 客户端已断开 */ }
	}

	/// <summary>发送二进制静态资源</summary>
	private static void SendBytes(HttpListenerContext context, byte[] buffer, string contentType)
	{
		try
		{
			var response = context.Response;
			response.StatusCode = 200;
			response.ContentType = contentType;
			response.ContentLength64 = buffer.Length;
			// 静态资源同样禁止缓存
			response.Headers["Cache-Control"] = "no-store";
			response.OutputStream.Write(buffer, 0, buffer.Length);
			response.Close();
		}
		catch { /* 客户端已断开 */ }
	}


	/// <summary>向网页推送聊天消息</summary>
	public void PushChat(WebSocketMessage wsMsg)
	{
		_webSocketMsgQueue.Enqueue(wsMsg);
		while (_webSocketMsgQueue.Count > 30) _webSocketMsgQueue.TryDequeue(out _);
		SendWebSocketMessage(wsMsg);
	}

	private async Task HandleWebSocket(HttpListenerContext context)
	{
		WebSocket? webSocket = null;
		try
		{
			webSocket = (await context.AcceptWebSocketAsync(null)).WebSocket;
			lock (_webSocketLock)
			{
				try { _webSocketClient?.CloseAsync(WebSocketCloseStatus.NormalClosure, "新连接建立", CancellationToken.None).Wait(1000); } catch { }
				_webSocketClient = webSocket;
				Plugin.Log?.Information("WebSocket 客户端连接");
			}
			// 保持连接直到断开
			var buffer = new byte[1024];
			while (webSocket.State == WebSocketState.Open)
			{
				var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
				if (result.MessageType == WebSocketMessageType.Close) break;
			}
		}
		catch (Exception e)
		{
			Plugin.Log?.Error($"WebSocket 错误: {e.Message}");
		}
		finally
		{
			try { webSocket?.Dispose(); } catch { }
			lock (_webSocketLock)
			{
				if (ReferenceEquals(_webSocketClient, webSocket)) _webSocketClient = null;
			}
		}
	}

	private void SendWebSocketMessage(WebSocketMessage wsMsg)
	{
		lock (_webSocketLock)
		{
			try
			{
				if (_webSocketClient?.State == WebSocketState.Open)
				{
					var buffer = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(wsMsg));
					_ = _webSocketClient.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
				}
			}
			catch { }
		}
	}

	public void Stop()
	{
		_running = false;
		try { _listener?.Stop(); } catch { }
		try { _listener?.Close(); } catch { }
		lock (_webSocketLock)
		{
			try { _webSocketClient?.CloseAsync(WebSocketCloseStatus.NormalClosure, "服务停止", CancellationToken.None).Wait(1000); } catch { }
			_webSocketClient = null;
		}
		_listener = null;
	}

	public void Dispose() => Stop();
}
