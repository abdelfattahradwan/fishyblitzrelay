# Fishy Blitz Relay

A relay transport for [Fish-Networking](https://fish-networking.fishnet.dev/). Fishy Blitz Relay routes all client
traffic through a Blitz Relay server instance, removing the need for clients to have publicly reachable IP addresses or
open
ports. This makes it suitable for deployments where NAT traversal or firewall restrictions prevent direct peer-to-peer
connections.

The transport uses a modified build of [LiteNetLib](https://github.com/RevenantX/LiteNetLib) internally for its UDP
connections to the relay.

## Requirements

- Unity 2022.3 or later
- [Fish-Networking](https://fish-networking.fishnet.dev/)

## Installation

Open the Unity Package Manager (Window > Package Manager) and add the package via git URL:

```
https://github.com/WinterboltGames/Fishy-Blitz-Relay.git?path=/Packages/com.winterboltgames.blitzrelay
```

Or add the following entry to your project's `Packages/manifest.json`:

```json
"com.winterboltgames.fishyblitzrelay": "https://github.com/WinterboltGames/Fishy-Blitz-Relay.git?path=/Packages/com.winterboltgames.blitzrelay"
```

## Features

- **Relay-based architecture**: All game traffic flows through a relay server, so clients never need to expose their
  IP addresses or open firewall ports.
- **Room-based**: Hosts create rooms and receive a room code from the relay; clients join by supplying
  that room code.
- **Basic Host migration**: When the current host disconnects, the relay can promote an existing client to become the
  new host.

## Setup

1. Add a GameObject with a `NetworkManager` component to your scene.
2. Add a `TransportManager` component to the same GameObject.
3. Add a `BlitzRelayTransport` component to the same GameObject.
4. Assign the `BlitzRelayTransport` component to the `TransportManager` component's `Transport` field.

## Configuration

Add the `BlitzRelayTransport` component to the same GameObject as your Fish-Networking `NetworkManager`. The following
fields are exposed in the Inspector:

| Field               | Type     | Description                                                                                                                                       |
|---------------------|----------|---------------------------------------------------------------------------------------------------------------------------------------------------|
| **Relay Address**   | `string` | IP address or hostname of the relay server. Defaults to `127.0.0.1`.                                                                              |
| **Relay Port**      | `ushort` | Port number the relay server is listening on. Defaults to `7770`.                                                                                 |
| **Relay Key**       | `string` | Connection key used to authenticate with the relay.                                                                                               |
| **Room Code**       | `string` | Room code to join on the relay. For hosts, this is assigned by the relay after room creation and can be retrieved at runtime via `GetRoomCode()`. |
| **Do Not Route**    | `bool`   | When enabled, packets are sent directly to the network interface without OS-level routing.                                                        |
| **Maximum Clients** | `int`    | Maximum number of simultaneous client connections. Defaults to `4096`.                                                                            |

### Runtime API

Several methods are available for script-driven configuration after the transport has been initialized:

- `SetRelayAddress(string)` / `GetRelayAddress()` -- Change or read the relay server address.
- `SetRelayPort(ushort)` / `GetRelayPort()` -- Change or read the relay server port.
- `SetRoomCode(string)` / `GetRoomCode()` -- Set or retrieve the current room code.
- `SetRoomHostToken(string)` / `GetRoomHostToken()` -- Manage the host token assigned by the relay (used internally for
  room ownership).
- `SetMaximumClients(int)` / `GetMaximumClients()` -- Adjust or read the client limit.
- `IsRelayHostAvailable` -- Indicates whether the local player is, or can become, the relay host.
- `OnRelayHostAvailabilityChanged` -- Event that fires when host availability changes (e.g. after a host migration).

## License

MIT License. See [LICENSE](LICENSE) for details.

Copyright 2026 Abdelfattah Radwan
