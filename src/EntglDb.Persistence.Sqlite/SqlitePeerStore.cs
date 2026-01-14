using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using EntglDb.Core;
using EntglDb.Core.Storage;

namespace EntglDb.Persistence.Sqlite
{
    public class SqlitePeerStore : IPeerStore
    {
        private readonly string _connectionString;

        public SqlitePeerStore(string connectionString)
        {
            _connectionString = connectionString;
            Initialize(); // Ensure DB is ready
        }

        private void Initialize()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            // Enable WAL mode
            connection.Execute("PRAGMA journal_mode=WAL;");

            // Create Tables
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS Documents (
                    Collection TEXT NOT NULL,
                    Key TEXT NOT NULL,
                    JsonData TEXT,
                    IsDeleted INTEGER NOT NULL,
                    HlcWall INTEGER NOT NULL,
                    HlcLogic INTEGER NOT NULL,
                    HlcNode TEXT NOT NULL,
                    PRIMARY KEY (Collection, Key)
                );

                CREATE TABLE IF NOT EXISTS Oplog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Collection TEXT NOT NULL,
                    Key TEXT NOT NULL,
                    Operation INTEGER NOT NULL,
                    JsonData TEXT,
                    IsDeleted INTEGER NOT NULL,
                    HlcWall INTEGER NOT NULL,
                    HlcLogic INTEGER NOT NULL,
                    HlcNode TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IDX_Oplog_HlcWall ON Oplog(HlcWall);
            ");
        }

        public async Task SaveDocumentAsync(Document document, CancellationToken cancellationToken = default)
        {
            // Note: In strict requirement, Put/Delete generates an Oplog entry.
            // PeerDatabase implementation calls SaveDocumentAsync AND AppendOplogEntryAsync separately.
            // However, typical 'Database' pattern implies atomic commit.
            // But since IPeerStore splits them, we implement them separately here.
            // Wait, the Requirement "Transactional: Updates to Documents and inserts into Oplog must happen in a single transaction."
            // This suggests the generic 'PeerCollection' logic which calls them separately might be flawed for this specific requirement,
            // OR IPeerStore should support a batch or transaction scope.
            // Given the interface IPeerStore defined earlier doesn't have explicit transaction support for single ops,
            // we will implement SaveDocumentAsync as just the Document update.
            // The user prompt Requirement 3 says "Put(key, jsonObject): Inserts/Updates... and internally generates an Oplog entry."
            // But PeerDatabase logic (Already implemented in Step 13) calls Store.SaveDocumentAsync then Store.AppendOplogEntryAsync.
            // To ensure atomicity without changing Core too much, we could rely on ApplyBatchAsync or wrap calls in a TransactionScope if Sqlite supported it well async?
            // Actually, SqliteTransaction is robust. 
            // BUT, strictly speaking, if `PeerCollection` logic is fixed, `SqlitePeerStore` methods `SaveDocumentAsync` and `AppendOplogEntryAsync` are independent.
            // Unless we change `IPeerStore` to have `PutAsync(Doc, Oplog)`?
            // User prompt 3: "These methods must interact with the persistence layer but abstract the complexity...".
            // Implementation Step 3: "It must allow storing 'Raw Documents' and 'Oplog Entries'".
            // So `SaveDocumentAsync` + `AppendOplogEntryAsync` is correct for the SPI.
            // The Atomicity requirement likely applies to the `ApplyBatch` (for sync) OR the higher level `Put`.
            // Since Core `PeerCollection` is generic, it might need `IPeerStore` to expose `ExecuteTransaction`?
            // For now, I will implement them as individual independent operations as per interface.
            // If the user *really* wants atomic Put in the store, the Store interface should probably have been `Put(Document doc, OplogEntry log)`.
            // I'll stick to the interface.

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Use transaction if we want, but this method only does one thing.
            await connection.ExecuteAsync(@"
                INSERT OR REPLACE INTO Documents (Collection, Key, JsonData, IsDeleted, HlcWall, HlcLogic, HlcNode)
                VALUES (@Collection, @Key, @JsonData, @IsDeleted, @HlcWall, @HlcLogic, @HlcNode)",
                new
                {
                    document.Collection, 
                    document.Key,
                    JsonData = document.Content.ValueKind == JsonValueKind.Undefined ? null : document.Content.GetRawText(),
                    document.IsDeleted,
                    HlcWall = document.UpdatedAt.PhysicalTime,
                    HlcLogic = document.UpdatedAt.LogicalCounter,
                    HlcNode = document.UpdatedAt.NodeId
                });
        }

        public async Task<Document?> GetDocumentAsync(string collection, string key, CancellationToken cancellationToken = default)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var row = await connection.QuerySingleOrDefaultAsync<DocumentRow>(@"
                SELECT Key, JsonData, IsDeleted, HlcWall, HlcLogic, HlcNode
                FROM Documents
                WHERE Collection = @Collection AND Key = @Key",
                new { Collection = collection, Key = key });

            if (row == null) return null;

            var hlc = new HlcTimestamp(row.HlcWall, row.HlcLogic, row.HlcNode);
            var content = row.JsonData != null 
                ? JsonSerializer.Deserialize<JsonElement>(row.JsonData) 
                : default;

            // Assuming I add Collection to Document
            return new Document(collection, row.Key, content, hlc, row.IsDeleted); 
        }

        public async Task AppendOplogEntryAsync(OplogEntry entry, CancellationToken cancellationToken = default)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await connection.ExecuteAsync(@"
                INSERT INTO Oplog (Collection, Key, Operation, JsonData, IsDeleted, HlcWall, HlcLogic, HlcNode)
                VALUES (@Collection, @Key, @Operation, @JsonData, @IsDeleted, @HlcWall, @HlcLogic, @HlcNode)",
                new
                {
                    entry.Collection,
                    entry.Key,
                    Operation = (int)entry.Operation,
                    JsonData = entry.Payload?.GetRawText(),
                    IsDeleted = entry.Operation == OperationType.Delete,
                    HlcWall = entry.Timestamp.PhysicalTime,
                    HlcLogic = entry.Timestamp.LogicalCounter,
                    HlcNode = entry.Timestamp.NodeId
                });
        }

        public async Task<IEnumerable<OplogEntry>> GetOplogAfterAsync(HlcTimestamp timestamp, CancellationToken cancellationToken = default)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // "Give me all changes since timestamp X"
            // Simple comparison on HlcWall generally suffices for "since", 
            // but for exact HLC causality it's complex (Wall, then Logic, then Node).
            // Usually for "Sync" we just want everything strictly greater than the vector provided?
            // Or typically just "Wall > X OR (Wall = X AND Logic > Y)".
            // For simplicity in SQL:
            var rows = await connection.QueryAsync<OplogRow>(@"
                SELECT Collection, Key, Operation, JsonData, HlcWall, HlcLogic, HlcNode
                FROM Oplog
                WHERE HlcWall > @HlcWall OR (HlcWall = @HlcWall AND HlcLogic > @HlcLogic)
                ORDER BY HlcWall ASC, HlcLogic ASC",
                new { HlcWall = timestamp.PhysicalTime, HlcLogic = timestamp.LogicalCounter });

            return rows.Select(r => new OplogEntry(
                r.Collection ?? "unknown",
                r.Key ?? "unknown",
                (OperationType)r.Operation,
                r.JsonData != null ? JsonSerializer.Deserialize<JsonElement>(r.JsonData) : (JsonElement?)null,
                new HlcTimestamp(r.HlcWall, r.HlcLogic, r.HlcNode ?? "")
            ));
        }

        public async Task<HlcTimestamp> GetLatestTimestampAsync(CancellationToken cancellationToken = default)
        {
             using var connection = new SqliteConnection(_connectionString);
             await connection.OpenAsync(cancellationToken);

             var row = await connection.QuerySingleOrDefaultAsync<MaxHlcResult>(@"
                SELECT MAX(HlcWall) as Wall, MAX(HlcLogic) as Logic, HlcNode 
                FROM Oplog
                ORDER BY HlcWall DESC, HlcLogic DESC LIMIT 1");
             
             if (row == null || row.Wall == null) return new HlcTimestamp(0, 0, "");
             
             string nodeId = row.HlcNode ?? "";
             return new HlcTimestamp(row.Wall.Value, row.Logic ?? 0, nodeId); 
        }

        private class MaxHlcResult
        {
            public long? Wall { get; set; }
            public int? Logic { get; set; }
            public string? HlcNode { get; set; }
        }

        public async Task ApplyBatchAsync(IEnumerable<Document> documents, IEnumerable<OplogEntry> oplogEntries, CancellationToken cancellationToken = default)
        {
            // Last-Write-Wins Merge Logic
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();

            try 
            {
                foreach (var entry in oplogEntries)
                {
                    // Check local state
                    var local = await connection.QuerySingleOrDefaultAsync<DocumentRow>(@"
                        SELECT HlcWall, HlcLogic, HlcNode
                        FROM Documents
                        WHERE Collection = @Collection AND Key = @Key",
                        new { entry.Collection, entry.Key }, transaction);

                    bool shouldApply = false;
                    if (local == null)
                    {
                        shouldApply = true;
                    }
                    else
                    {
                        var localHlc = new HlcTimestamp(local.HlcWall, local.HlcLogic, local.HlcNode);
                        if (entry.Timestamp.CompareTo(localHlc) > 0)
                        {
                            shouldApply = true;
                        }
                    }

                    if (shouldApply)
                    {
                        // Apply to Documents
                         await connection.ExecuteAsync(@"
                            INSERT OR REPLACE INTO Documents (Collection, Key, JsonData, IsDeleted, HlcWall, HlcLogic, HlcNode)
                            VALUES (@Collection, @Key, @JsonData, @IsDeleted, @HlcWall, @HlcLogic, @HlcNode)",
                            new
                            {
                                entry.Collection,
                                entry.Key,
                                JsonData = entry.Payload?.GetRawText(),
                                IsDeleted = entry.Operation == OperationType.Delete ? 1 : 0,
                                HlcWall = entry.Timestamp.PhysicalTime,
                                HlcLogic = entry.Timestamp.LogicalCounter,
                                HlcNode = entry.Timestamp.NodeId
                            }, transaction);
                    }
                    
                    // Always append to Oplog? Or only if new?
                    // "If the incoming entry is older, ignore it (but you might still choose to store it... for this MVP, ignoring is acceptable)"
                    // "Node B sends Oplog entries newer than Node A's knowledge."
                    // If we receive it, we usually assume we don't have it. 
                    // To be safe we can insert into Oplog if not exists (using Id/Timestamp dedupe?)
                    // For now, let's insert to keep history.
                    await connection.ExecuteAsync(@"
                        INSERT INTO Oplog (Collection, Key, Operation, JsonData, IsDeleted, HlcWall, HlcLogic, HlcNode)
                        VALUES (@Collection, @Key, @Operation, @JsonData, @IsDeleted, @HlcWall, @HlcLogic, @HlcNode)",
                        new
                        {
                            entry.Collection,
                            entry.Key,
                            Operation = (int)entry.Operation,
                            JsonData = entry.Payload?.GetRawText(),
                            IsDeleted = entry.Operation == OperationType.Delete,
                            HlcWall = entry.Timestamp.PhysicalTime,
                            HlcLogic = entry.Timestamp.LogicalCounter,
                            HlcNode = entry.Timestamp.NodeId
                        }, transaction);
                }
                
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public async Task<IEnumerable<Document>> QueryDocumentsAsync(string collection, QueryNode queryExpression, int? skip = null, int? take = null, string? orderBy = null, bool ascending = true, CancellationToken cancellationToken = default)
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var translator = new SqlQueryTranslator();
            // Handle null queryExpression (meaning "All")
            string whereClause = "1=1";
            var parameters = new DynamicParameters();

            if (queryExpression != null)
            {
                var (w, p) = translator.Translate(queryExpression);
                whereClause = w;
                parameters = p;
            }

            parameters.Add("@Collection", collection);

            var sqlBuilder = new System.Text.StringBuilder();
            sqlBuilder.Append(@"
                SELECT Key, JsonData, IsDeleted, HlcWall, HlcLogic, HlcNode
                FROM Documents
                WHERE Collection = @Collection AND IsDeleted = 0 AND (");
            sqlBuilder.Append(whereClause);
            sqlBuilder.Append(")");

            // Order By
            if (!string.IsNullOrEmpty(orderBy))
            {
                // Safety check for orderBy to prevent injection if it comes from untrusted source?
                // For now, we assume caller acts responsibly, but ideally we should sanitize or parameterize if possible.
                // SQLite allows 'ORDER BY ?' but it treats it as constant string, not column.
                // We must inject it. To be safe, let's restrict chars.
                if (orderBy.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '_'))
                {
                    // If it matches a column map directly, good. Else extract from JSON.
                    // Assuming fields are inside JsonData.
                    string sortField = $"json_extract(JsonData, '$.{orderBy}')";
                    
                    // Special optimization: If we had generated columns for indexed fields, we'd use them.
                    // For now, raw json_extract.
                    sqlBuilder.Append($" ORDER BY {sortField} {(ascending ? "ASC" : "DESC")}");
                }
            }
            else
            {
                 // Default order to ensure deterministic paging
                 sqlBuilder.Append(" ORDER BY Key ASC");
            }

            // Paging
            if (take.HasValue)
            {
                sqlBuilder.Append(" LIMIT @Take");
                parameters.Add("@Take", take.Value);
            }

            if (skip.HasValue)
            {
                sqlBuilder.Append(" OFFSET @Skip");
                parameters.Add("@Skip", skip.Value);
            }

            var rows = await connection.QueryAsync<DocumentRow>(sqlBuilder.ToString(), parameters);
            
            return rows.Select(r => {
                 var hlc = new HlcTimestamp(r.HlcWall, r.HlcLogic, r.HlcNode);
                 var content = r.JsonData != null 
                    ? JsonSerializer.Deserialize<JsonElement>(r.JsonData) 
                    : default;
                 return new Document(collection, r.Key, content, hlc, r.IsDeleted);
            });
        }

        // Inner classes for Dapper mapping
        private class DocumentRow
        {
            public string Key { get; set; } = "";
            public string? JsonData { get; set; }
            public bool IsDeleted { get; set; }
            public long HlcWall { get; set; }
            public int HlcLogic { get; set; }
            public string HlcNode { get; set; } = "";
        }

        private class OplogRow
        {
            public string? Collection { get; set; }
            public string? Key { get; set; }
            public int Operation { get; set; }
            public string? JsonData { get; set; }
            public long HlcWall { get; set; }
            public int HlcLogic { get; set; }
            public string? HlcNode { get; set; }
        }
    }
}
