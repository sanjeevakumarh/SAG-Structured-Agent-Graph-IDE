using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

public interface IActivityRepository
{
    Task SaveActivityAsync(ActivityEntry entry);
    Task<IReadOnlyList<ActivityEntry>> GetActivitiesByHourAsync(string workspacePath, string hourBucket);
    Task<IReadOnlyList<ActivityEntry>> GetActivitiesByTimeRangeAsync(string workspacePath, DateTime start, DateTime end);
    Task<IReadOnlyList<string>> GetHourBucketsAsync(string workspacePath, int limit = 100);
    Task<ActivityLogConfig?> GetConfigAsync(string workspacePath);
    Task SaveConfigAsync(ActivityLogConfig config);
}
