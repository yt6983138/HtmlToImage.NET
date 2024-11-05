namespace HtmlToImage.NET;
public class CustomSemaphoreSlim : SemaphoreSlim
{
	private class LockBlock(Action<CustomSemaphoreSlim> _onDispose, CustomSemaphoreSlim _instance) : IDisposable
	{
		private bool _disposed;

		public void Dispose()
		{
			if (this._disposed) return;
			this._disposed = true;
			_onDispose.Invoke(_instance);
		}
	}

	public CustomSemaphoreSlim(int initialCount) : base(initialCount) { }
	public CustomSemaphoreSlim(int initialCount, int maxCount) : base(initialCount, maxCount) { }

	public IDisposable DisposableLock(CancellationToken ct = default)
	{
		this.Wait(ct);
		return new LockBlock(static s => s.Release(), this);
	}
	public async Task<IDisposable> DisposableLockAsync(CancellationToken ct = default)
	{
		await this.WaitAsync(ct);
		return new LockBlock(static s => s.Release(), this);
	}
}
