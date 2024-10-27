using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HtmlToImage.NET;
internal static class Helper
{
	private static readonly JsonSerializerOptions _indentedOption = new()
	{
		WriteIndented = true
	};
	private static readonly JsonSerializerOptions _deserializeOption = new()
	{
		PropertyNameCaseInsensitive = true,
	};

	internal static T Await<T>(this Task<T> task)
	{
		return task.GetAwaiter().GetResult();
	}
	internal static T FromJson<T>(this string json)
	{
		return JsonSerializer.Deserialize<T>(json, _deserializeOption).EnsureNotNull();
	}
	internal static string ToJson<T>(this T obj, bool humanReadable = true)
	{
		return JsonSerializer.Serialize(obj, humanReadable ? _indentedOption : null);
	}
	internal static string AsUTF8String(this MemoryStream stream)
	{
		return Encoding.UTF8.GetString(stream.ToArray());
	}
	internal static byte[] DecodeAsBase64(this string data)
	{
		return Convert.FromBase64String(data);
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
