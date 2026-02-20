namespace SAGIDE.Core.DTOs;

public class StartWorkflowRequest
{
    public string DefinitionId { get; set; } = string.Empty;

    public Dictionary<string, string> Inputs { get; set; } = [];

    public List<string> FilePaths { get; set; } = [];
    public string DefaultModelId { get; set; } = string.Empty;
    public string DefaultModelProvider { get; set; } = string.Empty;

    public string? ModelEndpoint { get; set; }

    public string? WorkspacePath { get; set; }
}

public class GetWorkflowsRequest
{
    public string? WorkspacePath { get; set; }
}

public class CancelWorkflowRequest
{
    public string InstanceId { get; set; } = string.Empty;
}

public class WorkflowStartedResponse
{
    public string InstanceId { get; set; } = string.Empty;
}
