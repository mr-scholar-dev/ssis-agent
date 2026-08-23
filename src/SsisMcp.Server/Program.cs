using System;
using System.IO;
using System.Text;

namespace SsisMcp.Server
{
    /// <summary>
    /// Entry point: an MCP server speaking JSON-RPC 2.0 over newline-delimited stdio.
    ///
    /// stdout carries ONLY JSON-RPC — nothing else. To guarantee that even when a dependency
    /// (SSIS/COM interop, the assembly resolver, etc.) does a stray Console.Write, we:
    ///   1. bind a dedicated UTF-8 (no BOM), '\n'-terminated writer to the REAL stdout for the server,
    ///   2. redirect Console.Out to stderr, so any accidental Console.Write lands on stderr, not the
    ///      protocol stream.
    /// Diagnostics go to stderr, and additionally to a file when SSIS_MCP_LOG is set.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // Real streams, before we repoint Console.
            var rawOut = Console.OpenStandardOutput();
            var rawIn = Console.OpenStandardInput();

            var protocolOut = new StreamWriter(rawOut, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            var protocolIn = new StreamReader(rawIn, new UTF8Encoding(false));

            // Any stray Console.Write from dependencies must NOT pollute stdout → send it to stderr.
            var err = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetOut(err);
            Console.SetError(err);

            Log("SSIS MCP server starting (pid " + System.Diagnostics.Process.GetCurrentProcess().Id + ")");
            try
            {
                new McpServer().Run(protocolIn, protocolOut);
                Log("SSIS MCP server stopped (stdin EOF)");
                return 0;
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex.GetType().Name + ": " + ex.Message);
                Log(ex.StackTrace ?? "");
                return 1;
            }
        }

        private static void Log(string message)
        {
            var line = "[ssis-mcp " + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            try { Console.Error.WriteLine(line); } catch { /* ignore */ }
            var path = Environment.GetEnvironmentVariable("SSIS_MCP_LOG");
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { File.AppendAllText(path, line + Environment.NewLine); } catch { /* best effort */ }
            }
        }
    }
}
