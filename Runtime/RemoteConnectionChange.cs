namespace BlitzRelay
{
	/// <summary>
	/// A lightweight event describing a remote client's connect or disconnect.
	/// </summary>
	public readonly struct RemoteConnectionChange
	{
		/// <summary>
		/// The virtual connection ID assigned by the relay.
		/// </summary>
		public readonly int ConnectionId;

		/// <summary>
		/// True if the client connected; false if disconnected.
		/// </summary>
		public readonly bool IsConnected;

		/// <summary>
		/// Creates a RemoteConnectionEvent with the given connection ID and connection
		/// state.
		/// </summary>
		public RemoteConnectionChange(int connectionId, bool isConnected)
		{
			ConnectionId = connectionId;

			IsConnected = isConnected;
		}
	}
}
