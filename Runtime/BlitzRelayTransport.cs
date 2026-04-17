using FishNet.Managing;
using FishNet.Managing.Transporting;
using FishNet.Transporting;
using BlitzRelay.LiteNetLib;
using BlitzRelay.LiteNetLib.Layers;
using BlitzRelay.Protocol;
using System;
using UnityEngine;

namespace BlitzRelay
{
	public sealed class BlitzRelayTransport : Transport
	{
		[Header("Relay Connection")]
		[SerializeField]
		[Tooltip("Public IP address or hostname of the relay server.")]
		private string relayAddress = "127.0.0.1";

		[SerializeField]
		[Tooltip("Port of the relay server.")]
		private ushort relayPort = 7770;

		[SerializeField]
		[Tooltip("Connection key to use for relay connections.")]
		private string relayKey = string.Empty;

		[SerializeField]
		[Tooltip("Room code to join or claim on the relay server. For host-created rooms, the relay assigns this after room creation.")]
		private string roomCode = string.Empty;

		private string _roomHostToken = string.Empty;

		[Tooltip("While true, forces sockets to send data directly to interface without routing.")]
		[SerializeField]
		private bool doNotRoute;

		[Header("Server")]
		[SerializeField]
		[Tooltip("Maximum number of players which may be connected at once.")]
		[Range(1, 8192)]
		private int maximumClients = 4096;

		private const ushort MaxTimeoutSeconds = 1800;

		private const int MaximumUdpMtu = 1350;

		private PacketLayerBase _packetLayer;

		public readonly HostSocket ServerSocket = new();

		public readonly ClientSocket ClientSocket = new();

		private int _clientTimeout = MaxTimeoutSeconds;

		private int _serverTimeout = MaxTimeoutSeconds;

		private bool _pendingClientStart;

		private bool _pendingPromotedServerStart;

		private string _pendingPromotedClaimToken = string.Empty;

		private int _pendingPromotedMaximumClients;

		private bool _relayHostAvailable = true;

		~BlitzRelayTransport()
		{
			Shutdown();
		}

		public override event Action<ClientConnectionStateArgs> OnClientConnectionState;

		public override event Action<ServerConnectionStateArgs> OnServerConnectionState;

		public override event Action<RemoteConnectionStateArgs> OnRemoteConnectionState;

		public override event Action<ClientReceivedDataArgs> OnClientReceivedData;

		public override event Action<ServerReceivedDataArgs> OnServerReceivedData;

		public event Action<bool> OnRelayHostAvailabilityChanged;

		public bool IsRelayHostAvailable
		{
			get => _relayHostAvailable;
		}

		internal bool DoNotRoute
		{
			get => doNotRoute;
		}

		public override void Initialize(NetworkManager networkManager, int transportIndex)
		{
			base.Initialize(networkManager, transportIndex);

			networkManager.TimeManager.OnUpdate += HandleTimeManagerUpdate;
		}

		public override string GetConnectionAddress(int connectionId)
		{
			return ServerSocket.GetConnectionAddress(connectionId);
		}

		public override LocalConnectionState GetConnectionState(bool server)
		{
			return server ? ServerSocket.SocketConnectionState : ClientSocket.SocketConnectionState;
		}

		public override RemoteConnectionState GetConnectionState(int connectionId)
		{
			return ServerSocket.GetConnectionState(connectionId);
		}

		public override void HandleClientConnectionState(ClientConnectionStateArgs connectionStateArgs)
		{
			OnClientConnectionState?.Invoke(connectionStateArgs);
		}

		public override void HandleServerConnectionState(ServerConnectionStateArgs connectionStateArgs)
		{
			if (connectionStateArgs.ConnectionState is LocalConnectionState.Stopping or LocalConnectionState.Stopped)
			{
				_roomHostToken = string.Empty;
			}

			OnServerConnectionState?.Invoke(connectionStateArgs);
		}

		public override void HandleRemoteConnectionState(RemoteConnectionStateArgs connectionStateArgs)
		{
			OnRemoteConnectionState?.Invoke(connectionStateArgs);
		}

		public override void IterateIncoming(bool asServer)
		{
			if (asServer)
			{
				ServerSocket.IterateIncoming();

				if (!_pendingClientStart) return;

				LocalConnectionState serverState = GetConnectionState(true);

				if (serverState == LocalConnectionState.Started)
				{
					ClearPendingHostPromotion();

					StartClientInternal();
				}
				else if (serverState == LocalConnectionState.Stopped)
				{
					_pendingClientStart = false;

					ClearPendingHostPromotion();
				}
			}
			else
			{
				ClientSocket.IterateIncoming();

				if (_pendingPromotedServerStart && GetConnectionState(false) == LocalConnectionState.Stopped && GetConnectionState(true) == LocalConnectionState.Stopped)
				{
					StartPromotedServerInternal();
				}
			}
		}

		public override void IterateOutgoing(bool asServer)
		{
			if (asServer)
			{
				ServerSocket.IterateOutgoing();
			}
			else
			{
				ClientSocket.IterateOutgoing();
			}
		}

		public override void SendToServer(byte channelId, ArraySegment<byte> segment)
		{
			ClientSocket.SendToServer(SanitizeChannel(channelId), segment);
		}

		public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
		{
			ServerSocket.SendToClient(SanitizeChannel(channelId), segment, connectionId);
		}

		public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs receivedDataArgs)
		{
			OnClientReceivedData?.Invoke(receivedDataArgs);
		}

		public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs receivedDataArgs)
		{
			OnServerReceivedData?.Invoke(receivedDataArgs);
		}

		public override float GetPacketLoss(bool asServer)
		{
			NetManager netManager = asServer switch
			{
				true when ServerSocket != null => ServerSocket.SocketNetManager,

				false when ClientSocket != null => ClientSocket.SocketNetManager,

				_ => null,
			};

			return netManager == null ? 0.0f : netManager.Statistics.PacketLossPercent;
		}

		public override float GetTimeout(bool asServer)
		{
			return MaxTimeoutSeconds;
		}

		public override void SetTimeout(float value, bool asServer)
		{
			int timeoutValue = (int)Math.Ceiling(value);

			if (asServer)
			{
				_serverTimeout = timeoutValue;
			}
			else
			{
				_clientTimeout = timeoutValue;
			}

			UpdateTimeout();
		}

		public override int GetMaximumClients()
		{
			return ServerSocket.GetMaximumClients();
		}

		public override void SetMaximumClients(int value)
		{
			maximumClients = value;

			ServerSocket.SetMaximumClients(value);
		}

		public override void SetClientAddress(string address)
		{
			relayAddress = address;
		}

		public override string GetClientAddress()
		{
			return relayAddress;
		}

		public override void SetServerBindAddress(string address, IPAddressType addressType) { }

		public override string GetServerBindAddress(IPAddressType addressType)
		{
			return relayAddress;
		}

		public override void SetPort(ushort port)
		{
			relayPort = port;
		}

		public override ushort GetPort()
		{
			if (ServerSocket.TryGetPort(out ushort port)) return port;

			return ClientSocket.TryGetPort(out port) ? port : relayPort;
		}

		public override bool StartConnection(bool server)
		{
			return server ? StartServer() : StartClient();
		}

		public override bool StopConnection(bool server)
		{
			return server ? StopServer() : StopClient();
		}

		public override bool StopConnection(int connectionId, bool immediately)
		{
			return ServerSocket.StopConnection(connectionId);
		}

		public override void Shutdown()
		{
			_pendingClientStart = false;

			ClearPendingHostPromotion();

			HandleRelayHostAvailability(false);

			StopConnection(false);

			StopConnection(true);
		}

		public override int GetMTU(byte channel)
		{
			return MaximumUdpMtu - NetConstants.MaxUdpHeaderSize - MessageCodec.MaxRelayHeaderSize;
		}

		public void SetPacketLayer(PacketLayerBase packetLayer)
		{
			_packetLayer = packetLayer;

			if (GetConnectionState(true) != LocalConnectionState.Stopped) NetworkManager.LogWarning("PacketLayer is set but will not be applied until the server stops.");

			if (GetConnectionState(false) != LocalConnectionState.Stopped) NetworkManager.LogWarning("PacketLayer is set but will not be applied until the client stops.");

			InitializeSocket(true);

			InitializeSocket(false);
		}

		public string GetRelayAddress()
		{
			return relayAddress;
		}

		public void SetRelayAddress(string newRelayAddress)
		{
			relayAddress = newRelayAddress;
		}

		public ushort GetRelayPort()
		{
			return relayPort;
		}

		public void SetRelayPort(ushort newRelayPort)
		{
			relayPort = newRelayPort;
		}

		public string GetRoomCode()
		{
			return roomCode;
		}

		public void SetRoomCode(string newRoomCode)
		{
			roomCode = newRoomCode;
		}

		public string GetRoomHostToken()
		{
			return _roomHostToken;
		}

		public void SetRoomHostToken(string newRoomHostToken)
		{
			_roomHostToken = newRoomHostToken;
		}

		internal void HandleClientHostPromoted(string roomCode, int promotedMaximumClients, string claimToken)
		{
			this.roomCode = roomCode;

			maximumClients = promotedMaximumClients;

			_pendingPromotedMaximumClients = promotedMaximumClients;

			_pendingPromotedClaimToken = claimToken;

			_pendingPromotedServerStart = true;

			HandleRelayHostAvailability(false);
		}

		internal void HandleRelayHostAvailability(bool isAvailable)
		{
			if (_relayHostAvailable == isAvailable) return;

			_relayHostAvailable = isAvailable;

			OnRelayHostAvailabilityChanged?.Invoke(isAvailable);
		}

		private void OnDestroy()
		{
			Shutdown();

			if (NetworkManager != null) NetworkManager.TimeManager.OnUpdate -= HandleTimeManagerUpdate;
		}

		private void HandleTimeManagerUpdate()
		{
			ServerSocket?.PollSocket();

			ClientSocket?.PollSocket();
		}

		private void InitializeSocket(bool forServer)
		{
			if (forServer)
			{
				ServerSocket.Initialize(this, MaximumUdpMtu, _packetLayer);
			}
			else
			{
				ClientSocket.Initialize(this, MaximumUdpMtu, _packetLayer);
			}
		}

		private bool StartServer()
		{
			InitializeSocket(true);

			UpdateTimeout();

			bool started = ServerSocket.StartConnection(relayAddress, relayPort, relayKey, maximumClients, _pendingPromotedClaimToken, roomCode);

			if (started && !string.IsNullOrWhiteSpace(_pendingPromotedClaimToken))
			{
				_pendingPromotedClaimToken = string.Empty;
			}

			return started;
		}

		private bool StopServer()
		{
			_roomHostToken = string.Empty;

			return ServerSocket?.StopConnection() ?? false;
		}

		private bool StartClient()
		{
			if (GetConnectionState(true) != LocalConnectionState.Starting) return StartClientInternal();

			_pendingClientStart = true;

			return true;
		}

		private bool StartClientInternal()
		{
			_pendingClientStart = false;

			InitializeSocket(false);

			UpdateTimeout();

			return ClientSocket.StartConnection(relayAddress, relayPort, relayKey, roomCode);
		}

		private bool StopClient()
		{
			_pendingClientStart = false;

			return ClientSocket?.StopConnection() ?? false;
		}

		private void StartPromotedServerInternal()
		{
			_pendingPromotedServerStart = false;

			_pendingClientStart = true;

			maximumClients = _pendingPromotedMaximumClients > 0 ? _pendingPromotedMaximumClients : maximumClients;

			if (StartServer()) return;

			_pendingClientStart = false;

			ClearPendingHostPromotion();
		}

		private void ClearPendingHostPromotion()
		{
			_pendingPromotedServerStart = false;

			_pendingPromotedClaimToken = string.Empty;

			_pendingPromotedMaximumClients = 0;
		}

		private void UpdateTimeout()
		{
			ClientSocket.UpdateTimeout(_clientTimeout);

			ServerSocket.UpdateTimeout(_serverTimeout);
		}

		private byte SanitizeChannel(byte channelId)
		{
			if (channelId < TransportManager.CHANNEL_COUNT) return channelId;

			NetworkManager.LogWarning($"Channel of {channelId} is out of range of supported channels. Channel will be defaulted to reliable.");

			return 0;
		}
	}
}
