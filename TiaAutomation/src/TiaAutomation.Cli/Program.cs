using System;
using System.Collections.Generic;
using System.IO;
using TiaAutomation.Cli.Commands;

namespace TiaAutomation.Cli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help" || args[0] == "-h")
            {
                PrintUsage();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args);

            try
            {
                switch (command)
                {
                    case "inspect":
                        return new InspectCommand().Run(options);
                    case "inspect-block":
                        return new InspectBlockCommand().Run(options);
                    case "inspect-blocks":
                        return new InspectBlocksCommand().Run(options);
                    case "plan":
                        return new PlanCommand().Run(options);
                    case "apply":
                        return new ApplyCommand().Run(options);
                    default:
                        Console.Error.WriteLine($"Unknown command: {command}");
                        PrintUsage();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--"))
                {
                    continue;
                }

                var key = arg.Substring(2);
                var value = "true";
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    value = args[++i];
                }

                options[key] = value;
            }

            return options;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("TIA Automation MVP");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            Console.WriteLine("  inspect --project <ap20> --gsd-dir <dir> --out <json> [--openness-dll <path>]");
            Console.WriteLine("  inspect-block --project <ap20> --block <name> [--export-dir <dir>] [--out <json>] [--openness-dll <path>]");
            Console.WriteLine("  inspect-blocks --project <ap20> --names \"a,b,c\" [--export-dir <dir>] [--out <json>] [--openness-dll <path>]");
            Console.WriteLine("  plan --job <json> --catalog <json> --gsd-dir <dir> --out <json>");
            Console.WriteLine("  apply --job <json> --catalog <json> --gsd-dir <dir> --source-project <ap20> --target-project-dir <dir> --out <dir> [--enable-tia-write] [--tag-table <name>] [--openness-dll <path>]");
            Console.WriteLine();
            Console.WriteLine("MVP apply is safe by default: it copies the template project and writes export artifacts only.");
            Console.WriteLine("TIA writing only runs when --enable-tia-write is explicitly provided, and targets the copied project.");
        }
    }
}
