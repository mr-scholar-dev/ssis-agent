using System;

namespace SsisMcp.Server
{
    /// <summary>
    /// Entry point: an MCP server speaking JSON-RPC over stdio. READ-ONLY tools only.
    /// Configure an MCP client to launch this executable; see docs/mcp-tools.md.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var server = new McpServer();
            var stdin = Console.In;
            var stdout = Console.Out;
            server.Run(stdin, stdout);
            return 0;
        }
    }
}
