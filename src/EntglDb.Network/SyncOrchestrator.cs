using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using EntglDb.Core;
using EntglDb.Core.Storage;
using EntglDb.Core.Network;

namespace EntglDb.Network
{
    public class SyncOrchestrator
    {
        private readonly UdpDiscoveryService _discovery;
        private readonly IPeerStore _store;
        private readonly string _nodeId;
        private readonly ILogger<SyncOrchestrator> _logger;
        private readonly ILoggerFactory _loggerFactory;
        private CancellationTokenSource? _cts;
        private readonly Random _random = new Random();

        // Persistent clients pool
        private readonly ConcurrentDictionary<string, TcpPeerClient> _clients = new();

        private readonly string _authToken;

        public SyncOrchestrator(
            UdpDiscoveryService discovery, 
            IPeerStore store, 
            string nodeId, 
            string authToken,
            ILoggerFactory loggerFactory)
        {
            _discovery = discovery;
            _store = store;
            _nodeId = nodeId;
            _authToken = authToken;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<SyncOrchestrator>();
        }

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            Task.Run(() => SyncLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
            // Cleanup clients
            foreach(var client in _clients.Values) client.Dispose();
            _clients.Clear();
        }

        private async Task SyncLoopAsync(CancellationToken token)
        {
            _logger.LogInformation("Sync Orchestrator Started (Parallel P2P)");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var peers = _discovery.GetActivePeers().Where(p => p.NodeId != _nodeId).ToList();
                    
                    // Gossip Fanout: Pick 3 random peers
                    var targets = peers.OrderBy(x => _random.Next()).Take(3).ToList();

                    // Parallel Sync to reduce latency
                    await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token }, async (peer, ct) => 
                    {
                        await TrySyncWithPeer(peer, ct);
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sync Loop Error");
                }

                await Task.Delay(2000, token); // Sync every 2s for demo
            }
        }

        private async Task TrySyncWithPeer(PeerNode peer, CancellationToken token)
        {
            // _logger.LogDebug("Initiating Sync with {NodeId} at {Address}", peer.NodeId, peer.Address);
            TcpPeerClient client = null;

            try
            {
                // Get or create persistent client
                client = _clients.GetOrAdd(peer.NodeId, id => new TcpPeerClient(peer.Address, _loggerFactory.CreateLogger<TcpPeerClient>()));

                await client.ConnectAsync(token);

                // Handshake (idempotent due to client change)
                if (!await client.HandshakeAsync(_nodeId, _authToken, token))
                {
                     // If rejected, maybe credentials changed? For now, just warn.
                    _logger.LogWarning("Handshake rejected by {NodeId}", peer.NodeId);
                    return;
                }

                // 1. Anti-Entropy (Get Clock)
                var remoteClock = await client.GetClockAsync(token);
                var localClock = await _store.GetLatestTimestampAsync(token); 

                if (remoteClock.CompareTo(localClock) > 0)
                {
                     // Remote is ahead (wall or logic)
                    _logger.LogInformation("Pulling changes from {NodeId} (Remote: {Remote}, Local: {Local})", peer.NodeId, remoteClock, localClock);
                    var changes = await client.PullChangesAsync(localClock, token);
                    if (changes.Count > 0)
                    {
                        _logger.LogInformation("Received {Count} changes from {NodeId}", changes.Count, peer.NodeId);
                        await _store.ApplyBatchAsync(System.Linq.Enumerable.Empty<Document>(), changes, token);
                    }
                }
                else if (localClock.CompareTo(remoteClock) > 0)
                {
                     // Local is ahead (Push)
                     // Note: Optimistic push. Ideally we check if they need our data.
                     _logger.LogInformation("Pushing changes to {NodeId}", peer.NodeId);
                     var changes = await _store.GetOplogAfterAsync(remoteClock, token);
                     await client.PushChangesAsync(changes, token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Sync failed with {NodeId}: {Message}. Resetting connection.", peer.NodeId, ex.Message);
                
                // If connection failed, remove from pool and dispose so we retry fresh next time
                if (client != null)
                {
                    _clients.TryRemove(peer.NodeId, out _);
                    client.Dispose();
                }
            }
        }
    }
}
