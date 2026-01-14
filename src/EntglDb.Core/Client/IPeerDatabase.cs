using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EntglDb.Core
{
    public interface IPeerDatabase
    {
        IPeerCollection Collection(string name);
        Task SyncAsync(CancellationToken cancellationToken = default); // Manual trigger
    }

    public interface IPeerCollection
    {
        string Name { get; }

        Task Put(string key, object document, CancellationToken cancellationToken = default);
        Task<T> Get<T>(string key, CancellationToken cancellationToken = default);
        Task Delete(string key, CancellationToken cancellationToken = default);
        
        // Simple query abstraction
        Task<IEnumerable<T>> Find<T>(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
        Task<IEnumerable<T>> Find<T>(Expression<Func<T, bool>> predicate, int? skip, int? take, string? orderBy = null, bool ascending = true, CancellationToken cancellationToken = default);

    }
}
