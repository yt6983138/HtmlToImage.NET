using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HtmlToImage.NET;

public sealed class HtmlConverter : IDisposable
{
	public record class ChromeCdpInfo(
		string Description,
		string DevToolsFrontendUrl,
		string FaviconUrl,
		string Id,
		string Url,
		string Title,
		string Type,
		string WebSocketDebuggerUrl)
	{
		public string Url { get; internal set; } = Url;

		public string FullFrontendUrl =>
			$"localhost:{this.DevToolsFrontendUrl[(this.DevToolsFrontendUrl.IndexOf("localhost:") + 10)..this.DevToolsFrontendUrl.IndexOf("/devtools/page/")]}" +
			$"{this.DevToolsFrontendUrl}";
	}

	public Process ChromiumProcess { get; private set; }
	public StreamReader ChromiumStandardOut { get; private set; } = new(new MemoryStream());
	public StreamReader ChromiumStandardError { get; private set; } = new(new MemoryStream());
	public ushort CdpPort { get; private set; }
	public HttpClient HttpClient { get; private set; }

	public bool Debug { get; set; }

	internal CustomSemaphoreSlim TakePhotoLock { get; set; } = new(1, 1);

	/// <summary>
	/// Create a new instance of <see cref="HtmlConverter"/>.
	/// </summary>
	/// <param name="chromiumLocation">Path to executable of any chromium based browser.</param>
	/// <param name="cdpPort">The port for communication with browser, put zero to get a (unused) random one.</param>
	/// <param name="windowWidth">Width of the window.</param>
	/// <param name="windowHeight">Height of the window.</param>
	/// <param name="debug">Debug mode (print what the library receives and other things).</param>
	/// <param name="showChromiumOutput">Show chromium output to current console or not.</param>
	/// <param name="extraArgs">Extra arguments to start the browser.</param>
	public HtmlConverter(
		string chromiumLocation,
		ushort cdpPort,
		int windowWidth = 1920,
		int windowHeight = 1080,
		bool debug = false,
		bool showChromiumOutput = false,
		List<string>? extraArgs = null)
	{
		if (cdpPort == 0)
			cdpPort = Helper.GetNewFreePort();

		extraArgs ??= new();
		extraArgs.AddRange([
			"--remote-allow-origins=*",
			$"--window-size={windowWidth},{windowHeight}",
			$"--remote-debugging-port={cdpPort}",
			"--headless=new",
			"--no-first-run",
			"--no-default-browser-check"
		]);

		this.Debug = debug;
		// TODO: add cdp port get from stderr from chromium output
		this.CdpPort = cdpPort;
		this.ChromiumProcess = Process.Start(new ProcessStartInfo(chromiumLocation, extraArgs)
			{
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			})
			.EnsureNotNull();

		if (showChromiumOutput)
		{
			this.ChromiumProcess.ErrorDataReceived += (o, e) => Console.Error.WriteLine(e.Data);
			this.ChromiumProcess.OutputDataReceived += (o, e) => Console.WriteLine(e.Data);
		}

		this.ChromiumProcess.StandardError.ReadLine(); // make sure it started properly
		this.HttpClient = new();

		AppDomain.CurrentDomain.ProcessExit += this.CurrentDomain_ProcessExit;
	}

	private void CurrentDomain_ProcessExit(object? sender, EventArgs e)
	{
		this.ChromiumProcess.Kill();
	}

	~HtmlConverter()
	{
		this.Dispose();
	}
	public void Dispose()
	{
		GC.SuppressFinalize(this);
		this.ChromiumProcess.Kill();
		this.ChromiumProcess.Dispose();
		this.HttpClient.Dispose();
		this.TakePhotoLock.Dispose();
		AppDomain.CurrentDomain.ProcessExit -= this.CurrentDomain_ProcessExit;
	}

	public Tab NewTab(string url = "about:blank")
	{
		return new(this, url);
	}

	public sealed class Tab : IDisposable
	{
		public enum PhotoType
		{
			Jpeg,
			Png,
			Webp
		}
#pragma warning disable IDE1006 // Naming Styles
		public record class ViewPort(double x, double y, double width, double height, double scale);
#pragma warning restore IDE1006 // Naming Styles

		private int _commandId;
		private readonly HtmlConverter _parent;
		private readonly List<JsonNode> _eventQueue = new();

		public ClientWebSocket WSClient { get; private set; }
		public ChromeCdpInfo CdpInfo { get; private set; }
		internal CustomSemaphoreSlim CommonLock { get; private set; } = new(1, 1);
		public IReadOnlyList<JsonNode> Queue => this._eventQueue;

		internal Tab(HtmlConverter parent, string url)
		{
			this._parent = parent;
			string str = parent.HttpClient
				.PutAsync($"http://localhost:{parent.CdpPort}/json/new?{url}", null)
				.Await()
				.Content.ReadAsStringAsync()
				.Await();
			this.CdpInfo = str.FromJson<ChromeCdpInfo>();

			if (parent.Debug)
				Console.WriteLine(this.CdpInfo);
			this.WSClient = new();
			this.WSClient.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
			this.WSClient.ConnectAsync(new(this.CdpInfo.WebSocketDebuggerUrl), CancellationToken.None).Wait();
		}

		internal async Task EnsureWebSocketNotDead(CancellationToken ct = default)
		{
			if (this.WSClient.State == WebSocketState.CloseReceived)
			{
				throw new ApplicationException(
					$"WebSocket is closed from remote unexpectedly. Close status: {this.WSClient.CloseStatus}");
			}

			if (this.WSClient.State == WebSocketState.Aborted ||
				this.WSClient.State == WebSocketState.Closed)
			{
				await this.WSClient.ConnectAsync(new(this.CdpInfo.WebSocketDebuggerUrl), ct);
			}
		}
		internal async Task<int> SendCommandInternal(string commandName,
			Dictionary<string, object>? @params = null,
			CancellationToken cancellationToken = default)
		{
			await this.EnsureWebSocketNotDead(cancellationToken);

			int val = this._commandId++;
			string str = new
			{
				id = val,
				method = commandName,
				@params = @params ?? new()
				// reserved keyword bruh
			}.ToJson();
			byte[] buf = Encoding.UTF8.GetBytes(str);

			await this.WSClient.SendAsync(buf, WebSocketMessageType.Text, true, cancellationToken);

			return val;
		}
		internal async Task<JsonNode> ReadUntilFindIdInternal(int id, CancellationToken cancellationToken = default)
		{
			JsonNode obj;
			do
			{
				obj = (await this.ReadOneMessage(cancellationToken)).Item1;
				if (this._parent.Debug)
					Console.WriteLine(obj);
			}
			while (!(obj["id"] is not null && (int)obj["id"]! == id));

			return obj;
		}
		public async Task<(JsonNode, WebSocketReceiveResult)> ReadOneMessage(CancellationToken cancellationToken =
			default)
		{
			await this.EnsureWebSocketNotDead(cancellationToken);

			using MemoryStream stream = new();

			WebSocketReceiveResult result;
			byte[] buffer = new byte[4096];
			do
			{
				Array.Clear(buffer);
				result = await this.WSClient.ReceiveAsync(buffer, cancellationToken);
				stream.Write(buffer);
			}
			while (!result.EndOfMessage);

			if (this._parent.Debug)
				Console.WriteLine(stream.AsUTF8String());
			stream.Seek(0, SeekOrigin.Begin);

			int length = 0;
			while (stream.ReadByte() > 0) length++;

			stream.SetLength(length);
			stream.Seek(0, SeekOrigin.Begin);

			JsonNode obj = JsonNode.Parse(stream)!;

			if (obj["method"] is not null)
				this._eventQueue.Add(obj);

			return (obj, result);
		}
		public async Task<JsonNode> ReadUntilFindId(int id, CancellationToken cancellationToken = default)
		{
			using IDisposable _ = await this.CommonLock.DisposableLockAsync(cancellationToken);
			JsonNode result = await this.ReadUntilFindIdInternal(id, cancellationToken);
			return result;
		}
		public async Task<int> SendCommand(string commandName,
			Dictionary<string, object>? @params = null,
			CancellationToken cancellationToken = default)
		{
			using IDisposable _ = await this.CommonLock.DisposableLockAsync(cancellationToken);
			int result = await this.SendCommandInternal(commandName, @params, cancellationToken);
			return result;
		}

		public async Task<string> NavigateTo(string url,
			Func<Task>? thingsToDoBeforeWaiting = null,
			CancellationToken cancellationToken = default)
		{
			await this.SendCommand("Page.enable", cancellationToken: cancellationToken);
			this._eventQueue.Clear();
			JsonNode result = await this.ReadUntilFindId(
				await this.SendCommand("Page.navigate",
					new()
					{
						{
							"url", url
						}
					},
					cancellationToken),
				cancellationToken);

			JsonNode? error = result["result"]!["errorText"];
			if (error is not null) throw new InvalidOperationException((string)error!);

			Task? t = thingsToDoBeforeWaiting?.Invoke();
			if (t is not null) await t;

			using IDisposable _ = await this.CommonLock.DisposableLockAsync(cancellationToken);
			JsonNode? loadEvent;
			JsonNode? frameNavigatedEvent;
			bool firstLoop = true;
			do
			{
				loadEvent = this.Queue.FirstOrDefault(x => (string)x["method"]! == "Page.loadEventFired");
				frameNavigatedEvent = this.Queue.FirstOrDefault(x => (string)x["method"]! == "Page.frameNavigated");
				if (!firstLoop)
				{
					await this.ReadOneMessage(cancellationToken);
				}
				else firstLoop = false;
			}
			while (loadEvent is null || frameNavigatedEvent is null);

			JsonNode frame = frameNavigatedEvent["params"]!["frame"]!;
			this.CdpInfo.Url = (string)frame["url"]!;

			return (string)frame["id"]!;
		}
		public async Task HtmlAsPage(string html,
			Action? afterNavigate = null,
			CancellationToken cancellationToken = default)
		{
			string frameId = await this.NavigateTo("about:blank", cancellationToken: cancellationToken);
			afterNavigate?.Invoke();

			using IDisposable _ = await this.CommonLock.DisposableLockAsync(cancellationToken);

			await this.ReadUntilFindIdInternal(
				await this.SendCommandInternal("Page.setDocumentContent",
					new()
					{
						{
							"frameId", frameId
						},
						{
							"html", html
						}
					},
					cancellationToken),
				cancellationToken);
		}
		public async Task SetViewPortSize(int width,
			int height,
			double deviceScaleFactor,
			bool mobile,
			CancellationToken cancellationToken = default)
		{
			await this.SendCommand(
				"Emulation.setDeviceMetricsOverride",
				new()
				{
					{
						"width", width
					},
					{
						"height", height
					},
					{
						"deviceScaleFactor", deviceScaleFactor
					},
					{
						"mobile", mobile
					}
				},
				cancellationToken);
		}
		public async Task<JsonNode> EvaluateJavaScript(string script, CancellationToken cancellationToken = default)
		{
			using IDisposable _ = await this.CommonLock.DisposableLockAsync(cancellationToken);

			JsonNode result = await this.ReadUntilFindIdInternal(
				await this.SendCommandInternal("Runtime.evaluate",
					new()
					{
						{
							"expression", script
						}
					},
					cancellationToken),
				cancellationToken);
			return result["result"]!;
		}
		public async Task<MemoryStream> TakePhotoOfCurrentPage(PhotoType photoType = PhotoType.Png,
			byte quality = 100,
			ViewPort? clip = null,
			CancellationToken ct = default)
		{
			using IDisposable _2 = await this._parent.TakePhotoLock.DisposableLockAsync(ct);
			using IDisposable _ = await this.CommonLock.DisposableLockAsync(ct);

			await this.SendCommandInternal("Page.bringToFront", cancellationToken: ct);
			await this.SendCommandInternal("Page.disable", cancellationToken: ct);

			Dictionary<string, object> arg = new()
			{
				{
					"format", photoType.ToString().ToLower()
				}
			};
			if (photoType == PhotoType.Jpeg) arg.Add("quality", quality);
			if (clip is not null) arg.Add("clip", clip);

			int id = await this.SendCommandInternal("Page.captureScreenshot", arg, ct);

			await this.EnsureWebSocketNotDead(ct);

			using MemoryStream stream = new();
			byte[] buffer = new byte[4096];

			MemoryStream? result = null;
			while (true)
			{
				await ReadCore();

				Span<byte> streamData = MemoryMarshal.CreateSpan(
					ref MemoryMarshal.GetArrayDataReference(stream.GetBuffer()),
					(int)stream.Length); // prevent reallocation

				Func<string> error = () => Encoding.UTF8.GetString(stream.GetBuffer());

				Utf8JsonReader reader = new(streamData);

				// {"id":1,"result":{"debuggerId":"410155931499991781.746677355633306483"}}
				// {"id":2,"result":{"data":"..."}}
				// {"method":"Page.domContentEventFired","params":{"timestamp":955508.734977}}

				reader.Read(); // object start
				reader.Read(); // id or method
				string? type = reader.GetString();
				if (type == "method")
				{
					Utf8JsonReader anotherReader = new(streamData);
					this._eventQueue.Add(JsonNode.Parse(ref anotherReader)!);
					continue;
				}
				else if (type != "id") throw new InvalidDataException();
				// must be id now

				reader.Read(); // id number
				if (reader.GetInt32() != id) continue;

				reader.Read(); // result or error
				if (reader.GetString() != "result") throw new InvalidDataException(error());
				reader.Read(); // object start
				reader.Read(); // data
				if (reader.TokenType == JsonTokenType.EndObject || reader.GetString() != "data")
				{
					result = null;
					break;
				}

				reader.Read();
				result = new(reader.GetBytesFromBase64());
				break;
			}

			return result ?? new();

			async Task ReadCore()
			{
				stream.SetLength(0); // will zero out array internally

				WebSocketReceiveResult wResult;
				do
				{
					Array.Clear(buffer);
					wResult = await this.WSClient.ReceiveAsync(buffer, ct);
					stream.Write(buffer);
				}
				while (!wResult.EndOfMessage);

				stream.Seek(0, SeekOrigin.Begin);

				int length = 0;
				while (stream.ReadByte() > 0) length++;

				stream.SetLength(length);
				stream.Seek(0, SeekOrigin.Begin);
			}
		}

		~Tab()
		{
			this.Dispose();
		}
		public void Dispose()
		{
			GC.SuppressFinalize(this);
			this.SendCommand("Page.close").Wait();
			this.WSClient.Dispose();
			this.CommonLock.Dispose();
		}
	}
}