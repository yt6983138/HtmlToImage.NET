using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;

namespace HtmlToImage.NET;

public sealed class HtmlConverter : IDisposable
{
	public record class ChromeCdpInfo(
		string Description, string DevToolsFrontendUrl, string FaviconUrl, string Id, string Url, string Title, string Type, string WebSocketDebuggerUrl)
	{
		public string Url { get; internal set; } = Url;
	}

	public Process ChromiumProcess { get; private set; }
	public ushort CdpPort { get; private set; }
	public HttpClient HttpClient { get; private set; }

	public bool Debug { get; set; }

	internal SemaphoreSlim TakePhotoLock { get; set; } = new(1, 1);

	/// <summary>
	/// Create a new instance of <see cref="HtmlConverter"/>.
	/// </summary>
	/// <param name="chromiumLocation">Path to executable of any chromium based browser.</param>
	/// <param name="cdpPort">The port for communication with browser, put zero to get a (unused) random one.</param>
	/// <param name="windowWidth">Width of the window.</param>
	/// <param name="windowHeight">Height of the window.</param>
	/// <param name="extraArgs">Extra arguments to start the browser.</param>
	public HtmlConverter(string chromiumLocation, ushort cdpPort, int windowWidth = 1920, int windowHeight = 1080, bool debug = false, List<string>? extraArgs = null)
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
			"--no-default-browser-check",
		]);

		this.Debug = debug;
		this.CdpPort = cdpPort;
		this.ChromiumProcess = Process.Start(new ProcessStartInfo(chromiumLocation, extraArgs)
		{
			UseShellExecute = false,
			RedirectStandardOutput = !debug,
			RedirectStandardError = !debug
		}).EnsureNotNull();

		this.HttpClient = new();
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
	}

	public Tab NewTab(string url = "about:blank") => new(this, url);

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
		private HtmlConverter _parent;

		public ClientWebSocket WSClient { get; private set; }
		public ChromeCdpInfo CdpInfo { get; private set; }
		internal SemaphoreSlim CommonLock { get; private set; } = new(1, 1);

		internal Tab(HtmlConverter parent, string url)
		{
			this._parent = parent;
			this.CdpInfo = parent.HttpClient
				.PutAsync($"http://localhost:{parent.CdpPort}/json/new?{url}", null).Await()
				.Content.ReadAsStringAsync().Await()
				.FromJson<ChromeCdpInfo>();
			if (parent.Debug)
				Console.WriteLine(this.CdpInfo);
			this.WSClient = new();
			this.WSClient.ConnectAsync(new(this.CdpInfo.WebSocketDebuggerUrl), CancellationToken.None).Wait();
		}

		internal async Task<int> SendCommandInternal(string commandName, Dictionary<string, object>? @params = null)
		{
			byte[] buf = Encoding.UTF8.GetBytes(new
			{
				id = this._commandId,
				method = commandName,
				@params = @params ?? new()
				// reserved keyword bruh
			}.ToJson());

			await this.WSClient.SendAsync(buf, WebSocketMessageType.Text, true, CancellationToken.None);

			return this._commandId++;
		}
		internal JObject ReadUntilFindIdInternal(int id)
		{
			string message;
			JObject obj;
			do
			{
				message = this.WSClient.ReadOneMessage().Await().Item1.AsUTF8String();
				if (this._parent.Debug)
					Console.WriteLine(message);
				obj = JObject.Parse(message);
			}
			while (!(obj["id"] is not null && (int)obj["id"]! == id));

			return obj;
		}
		public JObject ReadUntilFindId(int id)
		{
			this.CommonLock.Wait();
			JObject result = this.ReadUntilFindIdInternal(id);
			this.CommonLock.Release();
			return result;
		}
		public async Task<int> SendCommand(string commandName, Dictionary<string, object>? @params = null)
		{
			await this.CommonLock.WaitAsync();
			int result = await this.SendCommandInternal(commandName, @params);
			this.CommonLock.Release();
			return result;
		}

		public async Task<string> NavigateTo(string url, Func<Task>? thingsToDoBeforeWaiting = null)
		{

			await this.SendCommand("Page.enable");
			await this.SendCommand("Page.navigate", new() { { "url", url } });

			Task? t = thingsToDoBeforeWaiting?.Invoke();
			if (t is not null) await t;

			this.CommonLock.Wait();
			string message;
			string frameNavigatedMessage;
			JObject? obj = null;
			do
			{
				message = this.WSClient.ReadOneMessage().Await().Item1.AsUTF8String();
				if (message.Contains("Page.frameNavigated"))
				{
					frameNavigatedMessage = message;
					obj = JObject.Parse(frameNavigatedMessage);
				}
				if (this._parent.Debug)
					Console.WriteLine(message);
			}
			while (!(message.Contains("Page.loadEventFired") && obj is not null)); // waiting for load complete

			JToken frame = obj["params"]!["frame"]!;
			this.CdpInfo.Url = (string)frame["url"]!;

			this.CommonLock.Release();

			return (string)frame["id"]!;
		}
		public async Task HtmlAsPage(string html, Action? afterNavigate = null)
		{
			string frameId = await this.NavigateTo("about:blank");
			afterNavigate?.Invoke();

			await this.CommonLock.WaitAsync();
			this.ReadUntilFindIdInternal(await this.SendCommandInternal("Page.setDocumentContent", new() { { "frameId", frameId }, { "html", html } }));
			this.CommonLock.Release();
		}
		[Experimental("HTI0001")]
		public async Task SetViewPortSize(int width, int height, double deviceScaleFactor, bool mobile)
		{
			await this.SendCommand(
				"Page.setDeviceMetricsOverride",
				new()
				{
					{ "width", width },
					{ "height", height },
					{ "deviceScaleFactor", deviceScaleFactor },
					{ "mobile", mobile }
				});
		}
		public async Task<JToken> EvaluateJavaScript(string script)
		{
			await this.CommonLock.WaitAsync();
			JObject result = this.ReadUntilFindIdInternal(await this.SendCommandInternal("Runtime.evaluate", new() { { "expression", script } }));
			this.CommonLock.Release();
			return result["result"]!;
		}
		public async Task<byte[]> TakePhotoOfCurrentPage(PhotoType photoType = PhotoType.Png, byte quality = 100, ViewPort? clip = null)
		{
			await this._parent.TakePhotoLock.WaitAsync();
			await this.CommonLock.WaitAsync();
			await this.SendCommandInternal("Page.bringToFront");
			await this.SendCommandInternal("Page.disable");
			byte[] result = ((string)this.ReadUntilFindIdInternal(await this.SendCommandInternal("Page.captureScreenshot"))["result"]!["data"]!).DecodeAsBase64();
			this.CommonLock.Release();
			this._parent.TakePhotoLock.Release();
			return result;
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
		}
	}
}
