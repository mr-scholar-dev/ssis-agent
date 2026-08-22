namespace SsisMcp.Core.Environment
{
    /// <summary>Probes the host for everything the SSIS MCP core needs. Backs <c>environment.detect</c>.</summary>
    public interface IEnvironmentDetector
    {
        EnvironmentReport Detect();
    }
}
