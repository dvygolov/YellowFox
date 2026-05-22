using YellowFox.Cli;

Environment.ExitCode = await AgentCli.RunAsync(args, Console.Out);
