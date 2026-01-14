# EntglDb

**EntglDb** (formerly PeerDb) is a lightweight, embeddable Peer-to-Peer (P2P) database for .NET.  
It allows you to build decentralized applications where every node has a local database that synchronizes automatically with other peers in the mesh.

## Features
- **Mesh Networking**: Nodes discover each other automatically via UDP Broadcast or Gossip.
- **Offline First**: All data is local. Reads and writes work without connection.
- **Eventual Consistency**: Updates propagate exponentially through the network.
- **Conflict Resolution**: Uses Hybrid Logical Clocks (HLC) aka "Last Write Wins".
- **Scalable**: Supports "Gossip Sync" to handle hundreds of nodes with constant overhead.

## Quick Start

### Installation
```bash
dotnet add package EntglDb.Core
dotnet add package EntglDb.Network
dotnet add package EntglDb.Persistence.Sqlite
```

### Usage
```csharp
// 1. Storage
var store = new SqlitePeerStore("my-node.db");
await store.InitializeAsync();

// 2. Network
var host = new UdpDiscoveryService(nodeId, tcpPort, logger);
var network = new TcpSyncServer(tcpPort, store, discovery, logger);

// 3. Orchestrator
var syncer = new SyncOrchestrator(network, network, store, logger);

// 4. Start
host.Start();
network.Start();
syncer.Start();
```
