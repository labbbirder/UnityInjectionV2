using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;

namespace BBBirder.UnityInjection.Tools;
using static ToolsConstants;

public class ProxyLinkerProgram
{
    record ArgumentData
    {
        public string outdir;
        public string[] allowedAssemblies;
    }

    static void ParseArguments(string[] args, out ArgumentData argumentData)
    {
        Common.Assert(args.Length >= 1, "no argument provided");
        string outdir = null;
        var allowedAssemblies = new List<string>();
        if (args[0].StartsWith('@'))
        {
            var path = args[0].Trim('@');
            var content = File.ReadAllText(path);
            var matches = Regex.Matches(content, @"([^ =]+)=?(""[^""]*""|[^ ""]*)");
            Common.Assert(matches != null, "not a valid arguments file");

            for (int i = 0; i < matches!.Count; i++)
            {
                var match = matches[i]!;
                OnProcessArgument(match);
            }
        }
        else
        {
            foreach (var arg in args)
            {
                var match = Regex.Match(arg, @"([^ =]+)=?(""[^""]*""|[^ ""]*)");
                if (!match.Success)
                {
                    continue;
                }
                OnProcessArgument(match);
            }
        }
        argumentData = new()
        {
            outdir = outdir,
            allowedAssemblies = allowedAssemblies.ToArray()
        };
        void OnProcessArgument(Match match)
        {
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim('\'', '\"');
            switch (key)
            {
                case "--out":
                    outdir = value;
                    break;
                case "--allowed-assembly":
                    allowedAssemblies.Add(value);
                    break;
                default:
                    break;
            }
        }
    }
    public static int Main(string[] args)
    {
        ParseArguments(args, out var argumentData);
        var outDir = argumentData.outdir;
        Common.Assert(outDir != null, "no out argument");

        using var client = new NamedPipeClientStream(LINKER_PIPE_NAME);
        client.Connect(8_000);
        if (!client.IsConnected)
        {
            return (int)ProxyLinkerResultCode.ConnectionTimeout;
        }
        var bytes = Encoding.UTF8.GetBytes(outDir!);
        foreach (var b in bytes)
        {
            client.WriteByte(b);
        }
        client.WriteByte(0);
        client.Flush();

        return client.ReadByte();
    }
}