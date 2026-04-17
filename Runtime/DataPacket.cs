using System;
using System.Buffers;

namespace BlitzRelay
{
	/// <summary>
	/// Immutable game-data packet with optional pooled backing buffer. Implements
	/// IDisposable.
	/// </summary>
	public readonly struct DataPacket : IDisposable
	{
		/// <summary>
		/// The virtual connection ID of the sender or target.
		/// </summary>
		public readonly int ConnectionId;

		/// <summary>
		/// True if the backing buffer was rented from the array pool.
		/// </summary>
		private readonly bool _isPooled;

		/// <summary>
		/// The backing byte array. May be pooled.
		/// </summary>
		private readonly byte[] _data;

		/// <summary>
		/// Number of bytes in use. Maybe less than the backing array length.
		/// </summary>
		private readonly int _length;

		/// <summary>
		/// The Fish-Networking channel this packet was received on or should be sent on.
		/// </summary>
		public readonly byte ChannelId;

		/// <summary>
		/// Creates a non-pooled packet from an owned byte array.
		/// </summary>
		public DataPacket(int connectionId, byte[] data, int length, byte channelId)
		{
			ConnectionId = connectionId;

			_isPooled = false;

			_data = data;

			_length = length;

			ChannelId = channelId;
		}

		/// <summary>
		/// Creates a pooled packet by renting an array from the shared pool and copying
		/// the span into it.
		/// </summary>
		public DataPacket(int connectionId, Span<byte> span, byte channelId, int mtu)
		{
			ConnectionId = connectionId;

			_isPooled = true;

			_data = ArrayPool<byte>.Shared.Rent(Math.Max(span.Length, mtu));

			span.CopyTo(_data);

			_length = span.Length;

			ChannelId = channelId;
		}

		/// <summary>
		/// Returns the packet data as an ArraySegment.
		/// </summary>
		public ArraySegment<byte> ToArraySegment()
		{
			return new ArraySegment<byte>(_data, 0, _length);
		}

		/// <summary>
		/// Returns the backing array to the pool if it was pooled; otherwise a no-op.
		/// </summary>
		public void Dispose()
		{
			if (_isPooled) ArrayPool<byte>.Shared.Return(_data);
		}
	}
}
