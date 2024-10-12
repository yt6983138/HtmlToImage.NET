using Newtonsoft.Json;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace HtmlToImage.NET;
internal static class Helper
{
	internal static T Await<T>(this Task<T> task)
	{
		return task.GetAwaiter().GetResult();
	}
	internal static T FromJson<T>(this string json)
	{
		return JsonConvert.DeserializeObject<T>(json).EnsureNotNull();
	}
	internal static string ToJson(this object obj, bool humanReadable = true)
	{
		return JsonConvert.SerializeObject(obj, humanReadable ? Formatting.Indented : Formatting.None);
	}
	internal static string AsUTF8String(this MemoryStream stream)
	{
		return Encoding.UTF8.GetString(stream.ToArray());
	}
	internal static byte[] DecodeAsBase64(this string data)
	{
		return Convert.FromBase64String(data);
	}
	public static async Task<(MemoryStream, WebSocketReceiveResult)> ReadOneMessage(this ClientWebSocket client, CancellationToken cancellationToken = default, MemoryStream? stream = null)
	{
		stream ??= new();

		WebSocketReceiveResult result;
		byte[] buffer = new byte[1024];
		do
		{
			Array.Clear(buffer);
			result = await client.ReceiveAsync(buffer, cancellationToken);
			stream.Write(buffer);
		}
		while (!result.EndOfMessage);

		return (stream, result);
	}

	[return: NotNull]
	internal static T EnsureNotNull<T>(this T obj)
	{
		if (obj is null) throw new ArgumentNullException(nameof(obj));
		return obj;
	}

	internal static ushort GetNewFreePort()
	{
		TcpListener l = new(IPAddress.Loopback, 0);
		l.Start();
		int port = ((IPEndPoint)l.LocalEndpoint).Port;
		l.Stop();
		l.Dispose();
		return (ushort)port;
	}
}
