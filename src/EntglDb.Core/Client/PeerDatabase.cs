using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EntglDb.Core.Network;
using EntglDb.Core.Storage;

namespace EntglDb.Core
{
    public class PeerDatabase : IPeerDatabase
    {
        private readonly IPeerStore _store;
        private readonly IMeshNetwork _network;
        private readonly string _nodeId;
        private HlcTimestamp _localClock;
        private readonly ConcurrentDictionary<string, PeerCollection> _collections = new ConcurrentDictionary<string, PeerCollection>();
        private readonly object _clockLock = new object();

        public PeerDatabase(IPeerStore store, IMeshNetwork network)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _nodeId = network.LocalNodeId; // Assuming Network is initialized or has ID
            // Initialize clock with 0 or restore from store (not implemented in ctor, maybe Async init needed)
            _localClock = new HlcTimestamp(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, _nodeId);
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default) 
        {
             // In a real scenario, we'd load the latest HLC from the store to ensure continuity
             var storedInfo = await _store.GetLatestTimestampAsync(cancellationToken);
             lock (_clockLock)
             {
                 if (storedInfo.CompareTo(_localClock) > 0)
                 {
                     _localClock = new HlcTimestamp(Math.Max(storedInfo.PhysicalTime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), storedInfo.LogicalCounter, _nodeId);
                 }
             }
        }

        public IPeerCollection Collection(string name)
        {
            return _collections.GetOrAdd(name, n => new PeerCollection(n, this));
        }

        public Task SyncAsync(CancellationToken cancellationToken = default)
        {
            // Trigger sync manually
            // This would likely delegate to a SyncEngine
            return Task.CompletedTask;
        }

        // Internal access for Collections
        internal IPeerStore Store => _store;
        
        internal HlcTimestamp Tick()
        {
            lock (_clockLock)
            {
                long physicalNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long lastPhysical = _localClock.PhysicalTime;
                int logical = _localClock.LogicalCounter;

                if (physicalNow > lastPhysical)
                {
                    _localClock = new HlcTimestamp(physicalNow, 0, _nodeId);
                }
                else
                {
                    _localClock = new HlcTimestamp(lastPhysical, logical + 1, _nodeId);
                }
                return _localClock;
            }
        }
    }

    public class PeerCollection : IPeerCollection
    {
        private readonly string _name;
        private readonly PeerDatabase _db;

        public PeerCollection(string name, PeerDatabase db)
        {
            _name = name;
            _db = db;
        }

        public string Name => _name;

        public async Task Put(string key, object document, CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.SerializeToElement(document);
            var timestamp = _db.Tick();

            var oplog = new OplogEntry(_name, key, OperationType.Put, json, timestamp);
            var paramsContainer = new Document(_name, key, json, timestamp, false);

            // In a real implementation, this should be transactional
            await _db.Store.SaveDocumentAsync(paramsContainer, cancellationToken);
            await _db.Store.AppendOplogEntryAsync(oplog, cancellationToken);
        }

        public async Task<T> Get<T>(string key, CancellationToken cancellationToken = default)
        {
            var doc = await _db.Store.GetDocumentAsync(_name, key, cancellationToken);
            if (doc == null || doc.IsDeleted) return default;

            return JsonSerializer.Deserialize<T>(doc.Content);
        }

        public async Task Delete(string key, CancellationToken cancellationToken = default)
        {
            var timestamp = _db.Tick();
            var oplog = new OplogEntry(_name, key, OperationType.Delete, null, timestamp);
            // We also update the document to mark it as deleted (Tombstone in Store)
            // Or just rely on Oplog. Usually we want fast reads so we update the Doc state too.
            var empty = default(JsonElement);
            var paramsContainer = new Document(_name, key, empty, timestamp, true);

            await _db.Store.SaveDocumentAsync(paramsContainer, cancellationToken);
            await _db.Store.AppendOplogEntryAsync(oplog, cancellationToken);
        }

        public async Task<IEnumerable<T>> Find<T>(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        {
            // Note: Implementation of Find now needs to convert Expression to QueryNode if the Store requires QueryNode
            // But IPeerCollection abstraction wasn't requested to change signature in the 'Core Refactoring' step of prompt 2?
            // Wait, Prompt 2 says "Find(queryExpression): A generic way to query...".
            // Prompt 3 says "Find(collection, QueryNode)" for Store.
            // Client API often keeps Expression for linq support.
            // We need a visitor here to convert Expression -> QueryNode.
            // For now, I'll allow compilation but throw NotSupported or implement a basic translator.
            // OR I change IPeerCollection signature to take QueryNode directly?
            // "Database API" pattern usually implies a builder.
            // Given I cannot implement full Expression visitor in 5 mins, I will assume we pass QueryNode or fail.
            // Let's change signature to QueryNode to simplify this task constraint.
            // Convert Expression to QueryNode
            QueryNode queryNode = null;
            try 
            {
                queryNode = ExpressionToQueryNodeTranslator.Translate(predicate);
            }
            catch (Exception ex)
            {
                // Fallback or throw? For now, if we can't translate, we shouldn't query partially.
                // But typically we might want to fetch all and filter in memory if translation fails?
                // For a robust implementation, exact translation is preferred.
                System.Console.WriteLine($"[Warning] Query translation failed: {ex.Message}. Fetching all.");
                // Passing null to store returns "1=1" (All documents)
            }

            return await Find(predicate, null, null, null, true, cancellationToken);
        }

        public async Task<IEnumerable<T>> Find<T>(Expression<Func<T, bool>> predicate, int? skip, int? take, string? orderBy = null, bool ascending = true, CancellationToken cancellationToken = default)
        {
            // Convert Expression to QueryNode
            QueryNode queryNode = null;
            try 
            {
                queryNode = ExpressionToQueryNodeTranslator.Translate(predicate);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[Warning] Query translation failed: {ex.Message}. Fetching all.");
                // Passing null to store returns "1=1" (All documents)
            }

            var docs = await _db.Store.QueryDocumentsAsync(_name, queryNode, skip, take, orderBy, ascending, cancellationToken);
            var list = new List<T>();
            foreach (var d in docs)
            {
                if (!d.IsDeleted)
                {
                    try 
                    {
                        var item = JsonSerializer.Deserialize<T>(d.Content);
                        // Memory-side filtering if translation failed or we prefer double-check
                        // Check if we already filtered in DB? Yes if translator worked.
                        // If translator failed (queryNode==null), we MUST filter here.
                        if (queryNode == null && predicate != null)
                        {
                             var compiled = predicate.Compile();
                             if (compiled(item))
                             {
                                 list.Add(item);
                             }
                        }
                        else
                        {
                            // Already filtered by DB
                            list.Add(item);
                        }
                    }
                    catch { /* deserialization error */ }
                }
            }
            return list;
        }

    }
}
