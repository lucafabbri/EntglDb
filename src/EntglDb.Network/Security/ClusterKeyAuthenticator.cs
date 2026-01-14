using System.Threading.Tasks;

namespace EntglDb.Network.Security
{
    public class ClusterKeyAuthenticator : IAuthenticator
    {
        private readonly string _sharedKey;

        public ClusterKeyAuthenticator(string sharedKey)
        {
            _sharedKey = sharedKey;
        }

        public Task<bool> ValidateAsync(string nodeId, string token)
        {
            // Simple equality check. In real world, use HMAC or time-based tokens.
            return Task.FromResult(token == _sharedKey);
        }
    }
}
