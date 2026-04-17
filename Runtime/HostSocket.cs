using BlitzRelay.LiteNetLib;
using BlitzRelay.Protocol;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BlitzRelay
{
	public sealed class HostSocket : SocketBase
	{
		private readonly ConcurrentQueue<RemoteConnectionChange> _remoteConnectionChanges = new();

		private int _maximumClients;

		private readonly HashSet<int> _virtualClients = new();

		private string _claimToken = string.Empty;

		private bool _claimExistingRoom;

		~HostSocket()
		{
			StopConnection();
		}

		internal bool StartConnection(string relayAddress, ushort relayPort, string relayKey, int maximumClients, string claimToken = "", string roomCode = "")
		{
			if (SocketConnectionState != LocalConnectionState.Stopped) StopSocket();

			ConnectionStateChanges.Enqueue(LocalConnectionState.Starting);

			IterateIncoming();

			RelayAddress = relayAddress;

			RelayPort = relayPort;

			RelayKey = relayKey;

			_maximumClients = maximumClients;

			_claimToken = claimToken ?? string.Empty;

			_claimExistingRoom = !string.IsNullOrWhiteSpace(_claimToken);

			RoomCode = _claimExistingRoom ? roomCode : string.Empty;

			ClearQueues();

			_virtualClients.Clear();

			RelayPeer = null;

			_ = Task.Run(ConnectToRelay);

			return true;
		}

		internal bool StopConnection()
		{
			if (SocketNetManager == null || SocketConnectionState is LocalConnectionState.Stopping or LocalConnectionState.Stopped) return false;

			RelayPeer = null;

			_virtualClients.Clear();

			_claimToken = string.Empty;

			_claimExistingRoom = false;

			ConnectionStateChanges.Enqueue(LocalConnectionState.Stopping);

			StopSocket();

			return true;
		}

		internal bool StopConnection(int connectionId)
		{
			if (SocketNetManager == null || SocketConnectionState != LocalConnectionState.Started) return false;

			if (!_virtualClients.Contains(connectionId)) return false;

			if (RelayPeer is not { ConnectionState: ConnectionState.Connected }) return false;

			try
			{
				byte[] kickMessage = MessageCodec.CreateKick(connectionId);

				RelayPeer.Send(kickMessage, 0, kickMessage.Length, DeliveryMethod.ReliableOrdered);
			}
			catch
			{
				return false;
			}

			return true;
		}

		internal void IterateIncoming()
		{
			while (ConnectionStateChanges.TryDequeue(out LocalConnectionState result))
			{
				SetConnectionState(result, true);
			}

			LocalConnectionState localState = SocketConnectionState;

			if (localState != LocalConnectionState.Started)
			{
				ClearQueues();

				if (localState == LocalConnectionState.Stopped)
				{
					StopSocket();

					return;
				}
			}

			while (_remoteConnectionChanges.TryDequeue(out RemoteConnectionChange connectionEvent))
			{
				RemoteConnectionState state = connectionEvent.IsConnected ? RemoteConnectionState.Started : RemoteConnectionState.Stopped;

				RemoteConnectionStateArgs remoteConnectionStateArgs = new(state, connectionEvent.ConnectionId, Transport.Index);

				Transport.HandleRemoteConnectionState(remoteConnectionStateArgs);
			}

			while (IncomingPackets.TryDequeue(out DataPacket incomingPacket))
			{
				if (_virtualClients.Contains(incomingPacket.ConnectionId))
				{
					ArraySegment<byte> segment = incomingPacket.ToArraySegment();

					ServerReceivedDataArgs dataArgs = new(segment, (Channel)incomingPacket.ChannelId, incomingPacket.ConnectionId, Transport.Index);

					Transport.HandleServerReceivedDataArgs(dataArgs);
				}

				incomingPacket.Dispose();
			}
		}

		internal void IterateOutgoing()
		{
			if (SocketConnectionState != LocalConnectionState.Started || RelayPeer == null)
			{
				while (OutgoingPackets.TryDequeue(out DataPacket outgoingPacket))
				{
					outgoingPacket.Dispose();
				}

				return;
			}

			int count = OutgoingPackets.Count;

			for (int i = 0; i < count; i++)
			{
				DataPacket outgoingDataPacket = OutgoingPackets.Dequeue();

				int connectionId = outgoingDataPacket.ConnectionId;

				ArraySegment<byte> segment = outgoingDataPacket.ToArraySegment();

				byte gameChannel = outgoingDataPacket.ChannelId;

				DeliveryMethod deliveryMethod = gameChannel == (byte)Channel.Reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;

				if (gameChannel == (byte)Channel.Unreliable && segment.Count > Mtu)
				{
					Transport.NetworkManager.LogWarning($"Server is sending {segment.Count} bytes on the unreliable channel, while the MTU is only {Mtu}. Channel changed to reliable for this send.");

					deliveryMethod = DeliveryMethod.ReliableOrdered;
				}

				int frameSize = MessageCodec.HostDataHeaderSize + segment.Count;

				byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameSize);

				int targetId = connectionId == NetworkConnection.UNSET_CLIENTID_VALUE ? (int)VirtualClientTarget.Broadcast : connectionId;

				int written = MessageCodec.WriteHostData(frameBuffer, targetId, gameChannel, segment);

				RelayPeer.Send(frameBuffer, 0, written, deliveryMethod);

				ArrayPool<byte>.Shared.Return(frameBuffer);

				outgoingDataPacket.Dispose();
			}
		}

		internal void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
		{
			Send(OutgoingPackets, channelId, segment, connectionId, Mtu);
		}

		internal RemoteConnectionState GetConnectionState(int connectionId)
		{
			return _virtualClients.Contains(connectionId) ? RemoteConnectionState.Started : RemoteConnectionState.Stopped;
		}

		internal string GetConnectionAddress(int connectionId)
		{
			if (SocketConnectionState != LocalConnectionState.Started)
			{
				Transport.NetworkManager.LogWarning("Server socket is not started.");

				return string.Empty;
			}

			if (_virtualClients.Contains(connectionId)) return $"relay:{connectionId}";

			Transport.NetworkManager.LogWarning($"Virtual connection id {connectionId} is not connected.");

			return string.Empty;
		}

		internal int GetMaximumClients()
		{
			return Math.Min(_maximumClients, NetworkConnection.MAXIMUM_CLIENTID_WITHOUT_SIMULATED_VALUE);
		}

		internal void SetMaximumClients(int value)
		{
			_maximumClients = value;
		}

		private void ConnectToRelay()
		{
			EventBasedNetListener listener = new();

			listener.PeerConnectedEvent += PeerConnectedEventHandler;

			listener.PeerDisconnectedEvent += PeerDisconnectedEventHandler;

			listener.NetworkReceiveEvent += NetworkReceiveEventHandler;

			SocketNetManager = new NetManager(listener, PacketLayer)
			{
				DontRoute = Transport.DoNotRoute,

				MtuOverride = Mtu,
			};

			SocketNetManager.Start();

			SocketNetManager.Connect(RelayAddress, RelayPort, RelayKey);
		}

		private void PeerConnectedEventHandler(NetPeer peer)
		{
			RelayPeer = peer;

			byte[] registerMessage = _claimExistingRoom ? MessageCodec.CreateHostClaim(RoomCode, _claimToken) : MessageCodec.CreateHostRegister(_maximumClients);

			peer.Send(registerMessage, 0, registerMessage.Length, DeliveryMethod.ReliableOrdered);
		}

		private void PeerDisconnectedEventHandler(NetPeer peer, DisconnectInfo disconnectInfo)
		{
			RelayPeer = null;

			if (SocketConnectionState is LocalConnectionState.Stopping or LocalConnectionState.Stopped) return;

			Transport.NetworkManager.Log($"Relay host connection lost. Reason: {disconnectInfo.Reason}.");

			ConnectionStateChanges.Enqueue(LocalConnectionState.Stopping);

			ConnectionStateChanges.Enqueue(LocalConnectionState.Stopped);
		}

		private void NetworkReceiveEventHandler(NetPeer fromPeer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
		{
			int dataLength = reader.AvailableBytes;

			if (dataLength < 1)
			{
				reader.Recycle();

				return;
			}

			byte[] rawData = ArrayPool<byte>.Shared.Rent(Math.Max(dataLength, Mtu));

			reader.GetBytes(rawData, dataLength);

			reader.Recycle();

			MessageType messageType = MessageCodec.ReadMessageType(rawData);

			switch (messageType)
			{
				case MessageType.RoomCreated:
				{
					MessageCodec.ReadRoomCreated(rawData, out string roomCode, out string roomHostToken);

					Transport.SetRoomCode(roomCode);

					Transport.SetRoomHostToken(roomHostToken);

					Transport.HandleRelayHostAvailability(true);

					ConnectionStateChanges.Enqueue(LocalConnectionState.Started);

					ArrayPool<byte>.Shared.Return(rawData);

					break;
				}

				case MessageType.Connected:
				{
					int virtualId = MessageCodec.ReadConnected(rawData);

					ArrayPool<byte>.Shared.Return(rawData);

					if (_virtualClients.Count >= _maximumClients)
					{
						byte[] kickMessage = MessageCodec.CreateKick(virtualId);

						RelayPeer?.Send(kickMessage, 0, kickMessage.Length, DeliveryMethod.ReliableOrdered);

						break;
					}

					_virtualClients.Add(virtualId);

					RemoteConnectionChange remoteConnectionChange = new(virtualId, true);

					_remoteConnectionChanges.Enqueue(remoteConnectionChange);

					break;
				}

				case MessageType.Disconnected:
				{
					int virtualId = MessageCodec.ReadDisconnected(rawData);

					ArrayPool<byte>.Shared.Return(rawData);

					_virtualClients.Remove(virtualId);

					RemoteConnectionChange remoteConnectionChange = new(virtualId, false);

					_remoteConnectionChanges.Enqueue(remoteConnectionChange);

					break;
				}

				case MessageType.Data:
				{
					MessageCodec.ReadHostData(rawData, dataLength, out int sourceVirtualId, out byte gameChannel, out int payloadOffset, out int payloadLength);

					if (payloadLength > Mtu)
					{
						_virtualClients.Remove(sourceVirtualId);

						RemoteConnectionChange remoteConnectionChange = new(sourceVirtualId, false);

						_remoteConnectionChanges.Enqueue(remoteConnectionChange);

						byte[] kickMessage = MessageCodec.CreateKick(sourceVirtualId);

						RelayPeer?.Send(kickMessage, 0, kickMessage.Length, DeliveryMethod.ReliableOrdered);

						ArrayPool<byte>.Shared.Return(rawData);
					}
					else
					{
						byte[] payloadData = ArrayPool<byte>.Shared.Rent(Math.Max(payloadLength, Mtu));

						Buffer.BlockCopy(rawData, payloadOffset, payloadData, 0, payloadLength);

						ArrayPool<byte>.Shared.Return(rawData);

						DataPacket dataPacket = new(sourceVirtualId, payloadData, payloadLength, gameChannel);

						IncomingPackets.Enqueue(dataPacket);
					}

					break;
				}

				case MessageType.Error:
				{
					ErrorCode errorCode = MessageCodec.ReadError(rawData);

					ArrayPool<byte>.Shared.Return(rawData);

					Transport.NetworkManager.LogError($"Relay returned error code {errorCode}.");

					StopConnection();

					break;
				}

				default:
				{
					ArrayPool<byte>.Shared.Return(rawData);

					break;
				}
			}
		}

		protected override void ClearQueues()
		{
			base.ClearQueues();

			_remoteConnectionChanges.Clear();
		}
	}
}
