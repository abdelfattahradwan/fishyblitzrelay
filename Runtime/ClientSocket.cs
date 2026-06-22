using BlitzRelay.LiteNetLib;
using BlitzRelay.Protocol;
using FishNet.Managing;
using FishNet.Transporting;
using System;
using System.Buffers;
using System.Threading.Tasks;

namespace BlitzRelay
{
	public sealed class ClientSocket : SocketBase
	{
		private bool _pendingPromotionDisconnect;

		~ClientSocket()
		{
			StopConnection();
		}

		internal bool StartConnection(string relayAddress, ushort relayPort, string relayKey, string roomCode)
		{
			if (SocketConnectionState != LocalConnectionState.Stopped) StopSocket();

			ConnectionStateChanges.Enqueue(LocalConnectionState.Starting);

			IterateIncoming();

			RelayAddress = relayAddress;

			RelayPort = relayPort;

			RelayKey = relayKey;

			RoomCode = roomCode;

			RelayPeer = null;

			_pendingPromotionDisconnect = false;

			ClearQueues();

			_ = Task.Run(ConnectToRelay);

			return true;
		}

		internal bool StopConnection(DisconnectInfo? info = null)
		{
			if (SocketConnectionState is LocalConnectionState.Stopping or LocalConnectionState.Stopped) return false;

			bool pendingPromotionDisconnect = _pendingPromotionDisconnect;

			if (info != null && !pendingPromotionDisconnect) Transport.NetworkManager.Log($"Local client disconnect reason: {info.Value.Reason}.");

			RelayPeer = null;

			_pendingPromotionDisconnect = false;

			SetConnectionState(LocalConnectionState.Stopping, false);

			StopSocket();

			return true;
		}

		internal void IterateIncoming()
		{
			while (ConnectionStateChanges.TryDequeue(out LocalConnectionState result))
			{
				SetConnectionState(result, false);
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

			while (IncomingPackets.TryDequeue(out DataPacket incomingPacket))
			{
				ArraySegment<byte> incomingPacketSegment = incomingPacket.ToArraySegment();

				ClientReceivedDataArgs clientReceivedDataArgs = new(incomingPacketSegment, (Channel)incomingPacket.ChannelId, Transport.Index);

				Transport.HandleClientReceivedDataArgs(clientReceivedDataArgs);

				incomingPacket.Dispose();
			}
		}

		internal void IterateOutgoing()
		{
			if (RelayPeer is not { ConnectionState: ConnectionState.Connected })
			{
				while (OutgoingPackets.TryDequeue(out DataPacket outgoing))
				{
					outgoing.Dispose();
				}

				return;
			}

			int count = OutgoingPackets.Count;

			for (int i = 0; i < count; i++)
			{
				DataPacket outgoingDataPacket = OutgoingPackets.Dequeue();

				ArraySegment<byte> outgoingPacketSegment = outgoingDataPacket.ToArraySegment();

				byte gameChannel = outgoingDataPacket.ChannelId;

				DeliveryMethod deliveryMethod = gameChannel == (byte)Channel.Reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;

				if (gameChannel == (byte)Channel.Unreliable && outgoingPacketSegment.Count > Mtu)
				{
					Transport.NetworkManager.LogWarning($"Client is sending {outgoingPacketSegment.Count} bytes on the unreliable channel, while the MTU is only {Mtu}. Channel changed to reliable for this send.");

					deliveryMethod = DeliveryMethod.ReliableOrdered;
				}

				int frameSize = MessageCodec.ClientDataHeaderSize + outgoingPacketSegment.Count;

				byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameSize);

				int written = MessageCodec.WriteClientData(frameBuffer, gameChannel, outgoingPacketSegment);

				RelayPeer.Send(frameBuffer, 0, written, deliveryMethod);

				ArrayPool<byte>.Shared.Return(frameBuffer);

				outgoingDataPacket.Dispose();
			}
		}

		internal void SendToServer(byte channelId, ArraySegment<byte> segment)
		{
			if (SocketConnectionState == LocalConnectionState.Started) Send(OutgoingPackets, channelId, segment, -1, Mtu);
		}

		private void ConnectToRelay()
		{
			EventBasedNetListener listener = new();

			listener.PeerConnectedEvent += PeerConnectedEventHandler;

			listener.PeerDisconnectedEvent += PeerDisconnectedEventHandler;

			listener.NetworkReceiveEvent += NetworkReceiveEventHandler;

			SocketNetManager = new NetManager(listener, PacketLayer)
			{
				DisconnectTimeout = DisconnectTimeoutMilliseconds,

				DontRoute = Transport.DoNotRoute,

				MtuOverride = Mtu,
			};

			ConnectionStateChanges.Enqueue(LocalConnectionState.Starting);

			SocketNetManager.Start();

			SocketNetManager.Connect(RelayAddress, RelayPort, RelayKey);
		}

		private void PeerConnectedEventHandler(NetPeer peer)
		{
			RelayPeer = peer;

			byte[] joinMessage = MessageCodec.CreateClientJoin(RoomCode);

			peer.Send(joinMessage, 0, joinMessage.Length, DeliveryMethod.ReliableOrdered);
		}

		private void PeerDisconnectedEventHandler(NetPeer peer, DisconnectInfo disconnectInfo)
		{
			RelayPeer = null;

			StopConnection(disconnectInfo);
		}

		private void NetworkReceiveEventHandler(NetPeer fromPeer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
		{
			try
			{
				int dataLength = reader.UserDataSize;

				if (dataLength < 1) return;

				ReadOnlySpan<byte> relayPayload = new(reader.RawData, reader.UserDataOffset, dataLength);

				MessageType messageType = MessageCodec.ReadMessageType(relayPayload);

				switch (messageType)
				{
					case MessageType.JoinSuccess:
					{
						Transport.HandleRelayHostAvailability(true);

						ConnectionStateChanges.Enqueue(LocalConnectionState.Started);

						break;
					}

					case MessageType.HostPromoted:
					{
						MessageCodec.ReadHostPromoted(relayPayload, out string roomCode, out int maximumClients, out string claimToken);

						Transport.HandleRelayHostAvailability(false);

						Transport.HandleClientHostPromoted(roomCode, maximumClients, claimToken);

						_pendingPromotionDisconnect = true;

						byte[] acknowledgement = MessageCodec.CreateHostPromotionAck(roomCode, claimToken);

						fromPeer.Send(acknowledgement, 0, acknowledgement.Length, DeliveryMethod.ReliableOrdered);

						break;
					}

					case MessageType.HostUnavailable:
					{
						Transport.HandleRelayHostAvailability(false);

						break;
					}

					case MessageType.HostAvailable:
					{
						Transport.HandleRelayHostAvailability(true);

						break;
					}

					case MessageType.Data:
					{
						MessageCodec.ReadClientData(relayPayload, out byte gameChannel, out ReadOnlySpan<byte> gamePayload);

						byte[] payloadData = ArrayPool<byte>.Shared.Rent(Math.Max(gamePayload.Length, Mtu));

						gamePayload.CopyTo(payloadData.AsSpan(0, gamePayload.Length));

						IncomingPackets.Enqueue(DataPacket.TakeRentedBuffer(0, payloadData, gamePayload.Length, gameChannel));

						break;
					}

					case MessageType.Disconnected:
					{
						_pendingPromotionDisconnect = false;

						StopConnection();

						break;
					}

					case MessageType.Error:
					{
						ErrorCode errorCode = MessageCodec.ReadError(relayPayload);

						_pendingPromotionDisconnect = false;

						Transport.NetworkManager.LogError($"Relay returned error code {errorCode}.");

						StopConnection();

						break;
					}
				}
			}
			finally
			{
				reader.Recycle();
			}
		}
	}
}
