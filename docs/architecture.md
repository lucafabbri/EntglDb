# Architecture & Concepts

## Design Philosophy

EntglDb is designed for **Edge Computing** and **Local-First** scenarios.
It does not rely on a central master server. Every node is equal (Peer-to-Peer).

### HLC (Hybrid Logical Clock)
To resolve conflicts without a central authority, we use **Hybrid Logical Clocks**.
This allows us to determine the "happened-before" relationship between events even if system clocks are slightly skewed.
In case of concurrent edits, the "Last Write Wins" (LWW) policy based on HLC is applied.

## Synchronization

### Anti-Entropy
When two nodes connect, they exchange their latest HLC timestamps.
- If Node A is ahead of Node B, Node B "pulls" the missing operations from Node A.
- If Node B is ahead, Node A "pushes" its operations.

### Gossip Protocol
Nodes discover each other via UDP Broadcast (LAN) and then form random TCP connections to gossip updates.
This ensures that updates propagate exponentially through the network (Epidemic Algorithm).

## Security Disclaimer

::: warning NOT FOR PUBLIC INTERNET
**EntglDb is currently a Proof of Concept.**
:::

- **Transport**: Data is transmitted via raw TCP. There is **NO Encryption (TLS/SSL)**.
- **Authentication**: A basic "Shared Key" mechanism is implemented. Nodes must share the same `AuthToken` to sync.
- **Authorization**: Once authenticated, a node has full read/write access to all collections.

**Recommendation**: Use only within a trusted private network (VPN/VPC) or localhost.
