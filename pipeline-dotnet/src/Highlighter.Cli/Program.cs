using Highlighter.Pipeline;

const string usage =
    """
    usage: highlighter <command> [args]

    commands:
      ingest    Capture a Twitch/YouTube stream or video, transcribe chunks, and produce clips.
      revise    Revise a finished long-form edit with a natural-language request.
      reclip    Render a clip from a project's archived source video in S3.
      cleanup   Delete media objects referenced by pending media_cleanup_jobs rows.
      db-smoke  Insert a smoke-test project into Supabase.
    """;

var command = args.Length > 0 ? args[0] : null;
var rest = args.Skip(1).ToArray();

try
{
    switch (command)
    {
        case "ingest":
            Ingest.Main(rest);
            break;
        case "revise":
        {
            var positionals = new List<string>();
            string? outputRoot = null;
            for (var i = 0; i < rest.Length; i++)
            {
                var (flag, inlineValue) = Argv.SplitFlag(rest[i]);
                if (flag == "--output-root")
                    outputRoot = inlineValue ?? Argv.NextValue(rest, ref i, flag);
                else
                    Argv.Positional(flag, positionals);
            }
            if (positionals.Count != 2)
                throw new PipelineError(
                    "the following arguments are required: project_id, request");
            Revise.Main(positionals[0], positionals[1], outputRoot);
            break;
        }
        case "reclip":
            Reclip.Main(rest);
            break;
        case "cleanup":
            Cleanup.Main(rest);
            break;
        case "db-smoke":
            DbSmoke.Main();
            break;
        case "-h" or "--help" or "help":
            Console.WriteLine(usage);
            break;
        default:
            Console.Error.WriteLine(usage);
            Environment.Exit(2);
            break;
    }
}
catch (Exception exc)
{
    // Python prints the traceback and exits 1; mirror the exit code and put the
    // failure on stderr.
    Console.Error.WriteLine(exc.ToString());
    Environment.Exit(1);
}
