using System.Collections.Concurrent;
using DiscoveryHit = SkyEyePMS.Core.Collecting.DiscoveryHit;

namespace SkyEyePMS.Infrastructure.Collecting;

/// <summary>网段扫描任务状态</summary>
public class DiscoveryJob
{
    public string Id { get; init; } = "";
    public int Total { get; init; }
    public int Scanned;
    public bool Done;
    public string? Error;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<DiscoveryHit> Hits { get; } = new();
    private readonly object _lock = new();

    public void AddHits(IEnumerable<DiscoveryHit> hits)
    {
        lock (_lock) Hits.AddRange(hits);
    }

    public (int Scanned, int Total, bool Done, string? Error, List<DiscoveryHit> Hits) Snapshot()
    {
        lock (_lock) return (Scanned, Total, Done, Error, Hits.ToList());
    }
}

/// <summary>发现任务内存存储（进程级；重启即失效，扫描本身是一次性动作）</summary>
public class DiscoveryJobStore
{
    private readonly ConcurrentDictionary<string, DiscoveryJob> _jobs = new();
    private const int MaxJobs = 20;

    public DiscoveryJob Create(int total)
    {
        var job = new DiscoveryJob
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Total = total
        };
        _jobs[job.Id] = job;
        // 只保留最近 20 个任务，防止堆积（先加后裁，逐出最旧）
        while (_jobs.Count > MaxJobs)
        {
            var oldestKey = _jobs.Values.OrderBy(j => j.StartedAt).First().Id;
            _jobs.TryRemove(oldestKey, out _);
        }
        return job;
    }

    public DiscoveryJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;
}
