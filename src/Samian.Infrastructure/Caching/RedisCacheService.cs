using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Samian.Application.Contracts;

namespace Samian.Infrastructure.Caching;

// ICacheService as a TWO-TIER cache: L1 in-process memory, L2 Redis through IDistributedCache.
//
// Why two tiers, since the challenge only asks for Redis: an in-process hit is ~100 ns and a Redis hit
// is a network round trip (~0.5-1 ms). For a read model like "product by id" that a screen requests
// repeatedly, L1 removes most of the traffic and L2 keeps the servers consistent. The cost is the part
// every caching design has to answer: an L1 entry on server A is stale until its TTL expires after
// server B invalidated Redis. L1 therefore gets a SHORT lifetime on purpose, and the write paths below
// remove BOTH tiers so a single-instance application (this sample) never serves a stale row.
//
// Why Redis failures do not fail requests: every L2 call is wrapped, logged and treated as a miss. The
// alternative — letting a Redis outage become a 500 — turns a performance component into an availability
// dependency, which is the argument for a cache being optional by design.
public sealed class RedisCacheService : ICacheService
{
    // Web defaults: camelCase property names and case-insensitive deserialization, so a cached DTO
    // round-trips identically to what the API returns over HTTP.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // L1 never outlives the caller's TTL, and never outlives 30 seconds: short enough that a stale read
    // model on another instance self-heals quickly, long enough to absorb a screen's repeated polling.
    private static readonly TimeSpan MaxInProcessLifetime = TimeSpan.FromSeconds(30);

    private readonly IDistributedCache _distributed;
    private readonly IMemoryCache _inProcess;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(IDistributedCache distributed, IMemoryCache inProcess, ILogger<RedisCacheService> logger)
    {
        ArgumentNullException.ThrowIfNull(distributed);
        ArgumentNullException.ThrowIfNull(inProcess);
        ArgumentNullException.ThrowIfNull(logger);

        _distributed = distributed;
        _inProcess = inProcess;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        if (_inProcess.TryGetValue(key, out var entry) && entry is T inProcessHit)
            return inProcessHit;

        try
        {
            var json = await _distributed.GetStringAsync(key, cancellationToken);

            if (json is null)
                return null;

            var value = JsonSerializer.Deserialize<T>(json, Json);

            if (value is not null)
                _inProcess.Set(key, value, InProcessLifetime(TimeSpan.FromMinutes(5)));

            return value;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Redis read failed for {Key}; treating it as a cache miss.", key);

            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan timeToLive, CancellationToken cancellationToken = default)
        where T : class
    {
        _inProcess.Set(key, value, InProcessLifetime(timeToLive));

        try
        {
            await _distributed.SetStringAsync(
                key,
                JsonSerializer.Serialize(value, Json),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = timeToLive },
                cancellationToken);
        }
        catch (Exception exception)
        {
            // A failed write means the read will simply miss and repopulate from the database: the
            // cache is allowed to be empty, never allowed to be wrong.
            _logger.LogWarning(exception, "Redis write failed for {Key}; the next read will hit the database.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        // BOTH tiers, always. Removing only from Redis is the bug where the instance that wrote the
        // change keeps showing its own stale copy until the TTL expires.
        _inProcess.Remove(key);

        try
        {
            await _distributed.RemoveAsync(key, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Redis remove failed for {Key}; the entry will expire on its own.", key);
        }
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan timeToLive,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var cached = await GetAsync<T>(key, cancellationToken);

        if (cached is not null)
            return cached;

        var loaded = await factory();

        await SetAsync(key, loaded, timeToLive, cancellationToken);

        return loaded;
    }

    private static TimeSpan InProcessLifetime(TimeSpan requested) =>
        requested < MaxInProcessLifetime ? requested : MaxInProcessLifetime;
}
