using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EntglDb.Core.Network
{
    public class PeerNode
    {
        public string NodeId { get; }
        public string Address { get; } // IP:Port
        public DateTimeOffset LastSeen { get; }

        public PeerNode(string nodeId, string address, DateTimeOffset lastSeen)
        {
            NodeId = nodeId;
            Address = address;
            LastSeen = lastSeen;
        }
    }

    public interface IMeshNetwork
    {
        // Core Identity
        string LocalNodeId { get; }

        // Discovery & Topology
        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
        IEnumerable<PeerNode> GetActivePeers();

        // Communication Primitives (Abstracted flow)
        Task BroadcastAsync<T>(T message, CancellationToken cancellationToken = default);
        Task SendToPeerAsync<T>(string nodeId, T message, CancellationToken cancellationToken = default);
        
        // Event Hooks (Logic layer subscribes to these)
        event EventHandler<PeerNode> PeerJoined;
        event EventHandler<PeerNode> PeerLeft;
        event EventHandler<(string FromNodeId, object Message)> MessageReceived;
    }
}
