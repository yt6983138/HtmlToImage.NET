using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace HtmlToImage.NET;

public sealed class HtmlConverter : IDisposable
{
	public record class ChromeCdpInfo(
		string Description, string DevToolsFrontendUrl, string FaviconUrl, string Id, string Url, string Title, string Type, string WebSocketDebuggerUrl)
	{
		public string Url { get; set; } = Url;
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
		extraArgs ??= new();
		extraArgs.AddRange([
			"--remote-allow-origins=*",
			$"--window-size={windowWidth},{windowHeight}",
			$"--remote-debugging-port={cdpPort}",
			"--headless=new",
			"--no-first-run",
			"--no-default-browser-check",
		]);

		if (cdpPort == 0)
			cdpPort = Helper.GetNewFreePort();

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

	public Tab NewTab() => new(this);

	public sealed class Tab
	{
		private record struct ResponseData(string Data);
		private record struct ScreenShotResponse(int Id, ResponseData Result);

		private int _commandId;
		private HtmlConverter _parent;

		public ClientWebSocket WSClient { get; private set; }
		public ChromeCdpInfo CdpInfo { get; private set; }

		internal Tab(HtmlConverter parent)
		{
			this._parent = parent;
			this.CdpInfo = parent.HttpClient
				.PutAsync($"http://localhost:{parent.CdpPort}/json/new?about:blank", null).Await()
				.Content.ReadAsStringAsync().Await()
				.FromJson<ChromeCdpInfo>();
			if (parent.Debug)
				Console.WriteLine(this.CdpInfo);
			this.WSClient = new();
			this.WSClient.ConnectAsync(new(this.CdpInfo.WebSocketDebuggerUrl), CancellationToken.None).Wait();
		}

		public async Task SendCommand(string commandName, Dictionary<string, string>? @params = null)
		{
			byte[] buf = Encoding.UTF8.GetBytes(new
			{
				id = this._commandId++,
				method = commandName,
				@params = @params ?? new()
				// reserved keyword bruh
			}.ToJson());

			await this.WSClient.SendAsync(buf, WebSocketMessageType.Text, true, CancellationToken.None);
		}

		public async Task NavigateTo(string url)
		{
			//await this._parent.Semaphore.WaitAsync();
			await this.SendCommand("Page.enable");
			await this.SendCommand("Page.navigate", new() { { "url", url } });

			string message;
			string? frameNavigatedMessage = null;
			do
			{
				message = this.WSClient.ReadOneMessage().Await().Item1.AsUTF8String();
				if (message.Contains("Page.frameNavigated"))
					frameNavigatedMessage = message;
				if (this._parent.Debug)
					Console.WriteLine(message);
			}
			while (!message.Contains("Page.loadEventFired")); // waiting for load complete

			if (frameNavigatedMessage is not null)
			{
				JToken frame = JObject.Parse(frameNavigatedMessage)["params"]!["frame"]!;
				this.CdpInfo.Url = (string)frame["url"]!;
			}

			//this._parent.Semaphore.Release();
		}
		public async Task<byte[]> TakePhotoOfCurrentPage()
		{
			await this._parent.TakePhotoLock.WaitAsync();
			await this.SendCommand("Page.bringToFront");
			await this.SendCommand("Page.disable");
			await this.SendCommand("Page.captureScreenshot");
			while (true)
			{
				(MemoryStream? stream, WebSocketReceiveResult? result) = await this.WSClient.ReadOneMessage();
				string str = stream.AsUTF8String();
				if (this._parent.Debug)
					Console.WriteLine(str);
				if (str.Contains("result") && str.Contains("data"))
				{
					this._parent.TakePhotoLock.Release();
					return str.FromJson<ScreenShotResponse>().Result.Data.DecodeAsBase64();
				}
			}
		}
	}
}
