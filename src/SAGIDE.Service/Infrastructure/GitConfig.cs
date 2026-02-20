namespace SAGIDE.Service.Infrastructure;

public class GitConfig
{
    public bool AutoCommitResults { get; set; } = false;
    public string Branch { get; set; } = "sag-agent-log";
}
