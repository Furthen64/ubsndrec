using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

return await CliApplication.RunAsync(args);

internal static class CliApplication
{
    private const int PollAttempts = 20;
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(250);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = ParseArguments(args);

            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            EnsureCommandExists("pw-record", "pw-record not found. Install pipewire.");
            EnsureCommandExists("pw-link", "pw-link not found. Install pipewire.");

            if (!CommandExists("wpctl") && !CommandExists("pw-cli"))
            {
                throw new CliException("Neither wpctl nor pw-cli found. Install wireplumber or pipewire-utils for sink detection.");
            }

            return options.Mode switch
            {
                CliMode.ListSinks => await ListSinksAsync(),
                CliMode.Wizard => await RunWizardAsync(options),
                _ => await RunRecordAsync(options)
            };
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static CaptureOptions ParseArguments(string[] args)
    {
        if (args.Length == 0)
        {
            return new CaptureOptions { Mode = CliMode.Wizard };
        }

        var options = new CaptureOptions();
        var index = 0;
        var modeToken = args[0];

        switch (modeToken)
        {
            case "record":
                options.Mode = CliMode.Record;
                index = 1;
                break;
            case "wizard":
                options.Mode = CliMode.Wizard;
                index = 1;
                break;
            case "list-sinks":
                options.Mode = CliMode.ListSinks;
                index = 1;
                break;
            case "help":
            case "--help":
            case "-h":
                options.ShowHelp = true;
                return options;
            default:
                options.Mode = CliMode.Record;
                break;
        }

        for (; index < args.Length; index++)
        {
            var arg = args[index];

            if (arg is "--help" or "-h")
            {
                options.ShowHelp = true;
                return options;
            }

            if (arg is "-o" or "--output")
            {
                options.Output = RequireValue(args, ref index, arg);
                continue;
            }

            if (arg is "-s" or "--sink")
            {
                options.Sink = RequireValue(args, ref index, arg);
                continue;
            }

            if (arg is "-c" or "--channel")
            {
                options.Channel = ParseChannel(RequireValue(args, ref index, arg));
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                throw new CliException($"Unknown option: {arg}");
            }

            if (!string.IsNullOrWhiteSpace(options.Output))
            {
                throw new CliException($"Unexpected argument: {arg}");
            }

            options.Output = arg;
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        var next = index + 1;
        if (next >= args.Length)
        {
            throw new CliException($"Missing value for {option}.");
        }

        index = next;
        return args[index];
    }

    private static CaptureChannel ParseChannel(string value) =>
        value.ToLowerInvariant() switch
        {
            "stereo" => CaptureChannel.Stereo,
            "left" => CaptureChannel.Left,
            "right" => CaptureChannel.Right,
            _ => throw new CliException($"Unsupported channel mode '{value}'. Choose stereo, left, or right.")
        };

    private static async Task<int> ListSinksAsync()
    {
        var sinks = await GetAvailableSinksAsync();
        if (sinks.Count == 0)
        {
            throw new CliException("Could not find any PipeWire sinks.");
        }

        Console.WriteLine("Available sinks:");
        foreach (var sink in sinks)
        {
            Console.WriteLine($"{(sink.IsDefault ? "* " : "  ")}{sink.Name}");
        }

        return 0;
    }

    private static async Task<int> RunWizardAsync(CaptureOptions seed)
    {
        var sinks = await GetAvailableSinksAsync();
        var chosenSink = await PromptForSinkAsync(sinks, seed.Sink);
        var output = Prompt("Output file", seed.Output ?? GetDefaultOutputPath(), allowEmpty: false);
        var channel = PromptForChannel(seed.Channel);

        return await RunRecordAsync(new CaptureOptions
        {
            Mode = CliMode.Record,
            Output = output,
            Sink = chosenSink,
            Channel = channel
        });
    }

    private static async Task<string> PromptForSinkAsync(IReadOnlyList<SinkInfo> sinks, string? seedSink)
    {
        if (!string.IsNullOrWhiteSpace(seedSink))
        {
            return seedSink;
        }

        Console.WriteLine("Select the PipeWire sink to capture:");

        if (sinks.Count == 0)
        {
            return Prompt("Sink name", string.Empty, allowEmpty: false);
        }

        for (var i = 0; i < sinks.Count; i++)
        {
            var sink = sinks[i];
            Console.WriteLine($"{i + 1}. {sink.Name}{(sink.IsDefault ? " (default)" : string.Empty)}");
        }

        Console.WriteLine("M. Enter a sink name manually");

        var defaultIndex = sinks
            .Select((sink, index) => (sink, index))
            .FirstOrDefault(tuple => tuple.sink.IsDefault)
            .index;

        while (true)
        {
            var response = Prompt("Choice", (defaultIndex + 1).ToString(), allowEmpty: true);

            if (string.Equals(response, "m", StringComparison.OrdinalIgnoreCase))
            {
                return Prompt("Sink name", string.Empty, allowEmpty: false);
            }

            if (int.TryParse(response, out var selected) && selected >= 1 && selected <= sinks.Count)
            {
                return sinks[selected - 1].Name;
            }

            Console.WriteLine("Please choose a listed sink number or M.");
        }
    }

    private static CaptureChannel PromptForChannel(CaptureChannel defaultChannel)
    {
        Console.WriteLine("Capture mode:");
        Console.WriteLine("1. stereo");
        Console.WriteLine("2. left");
        Console.WriteLine("3. right");

        var defaultChoice = defaultChannel switch
        {
            CaptureChannel.Stereo => "1",
            CaptureChannel.Left => "2",
            _ => "3"
        };

        while (true)
        {
            var response = Prompt("Choice", defaultChoice, allowEmpty: true);
            switch (response)
            {
                case "1":
                    return CaptureChannel.Stereo;
                case "2":
                    return CaptureChannel.Left;
                case "3":
                    return CaptureChannel.Right;
                default:
                    Console.WriteLine("Please choose 1, 2, or 3.");
                    break;
            }
        }
    }

    private static string Prompt(string label, string defaultValue, bool allowEmpty)
    {
        while (true)
        {
            Console.Write(string.IsNullOrEmpty(defaultValue) ? $"{label}: " : $"{label} [{defaultValue}]: ");
            var response = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(response))
            {
                if (!string.IsNullOrWhiteSpace(defaultValue))
                {
                    return defaultValue;
                }

                if (allowEmpty)
                {
                    return string.Empty;
                }
            }
            else
            {
                return response.Trim();
            }

            Console.WriteLine($"{label} is required.");
        }
    }

    private static async Task<int> RunRecordAsync(CaptureOptions options)
    {
        var output = Path.GetFullPath(options.Output ?? GetDefaultOutputPath());
        var sink = options.Sink;

        if (string.IsNullOrWhiteSpace(sink))
        {
            Console.WriteLine("Detecting default PipeWire audio sink...");
            sink = await DetectDefaultSinkAsync();
            Console.WriteLine($"Detected sink: {sink}");
        }
        else
        {
            Console.WriteLine($"Using sink: {sink}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? Directory.GetCurrentDirectory());

        var nodeName = $"speaker_capture_{Environment.ProcessId}";

        Console.WriteLine($"Output file : {output}");
        Console.WriteLine($"Sink        : {sink}");
        Console.WriteLine($"Channel     : {options.Channel.ToString().ToLowerInvariant()}");
        Console.WriteLine($"Recorder    : {nodeName}");

        using var recorder = StartRecorder(nodeName, output);
        Console.WriteLine("Waiting for recorder ports to appear...");
        await WaitForRecorderPortsAsync(nodeName, recorder);

        await CreateLinksAsync(sink, nodeName, options.Channel);

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancelHandler = null;
        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;

        try
        {
            Console.WriteLine("Recording... Press Ctrl+C to stop.");
            await WaitForRecorderExitAsync(recorder, cancellation.Token);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            await StopRecorderAsync(recorder);
            Console.WriteLine($"Done. WAV file: {output}");
        }
    }

    private static Process StartRecorder(string nodeName, string output)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pw-record",
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--properties");
        startInfo.ArgumentList.Add($"node.name={nodeName},media.name={nodeName}");
        startInfo.ArgumentList.Add(output);

        try
        {
            return Process.Start(startInfo) ?? throw new CliException("Failed to start pw-record.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new CliException($"Failed to start pw-record: {ex.Message}");
        }
    }

    private static async Task WaitForRecorderPortsAsync(string nodeName, Process recorder)
    {
        for (var attempt = 0; attempt < PollAttempts; attempt++)
        {
            if (recorder.HasExited)
            {
                throw new CliException("pw-record exited before the recorder ports became available.");
            }

            var inputPorts = await RunProcessForOutputAsync("pw-link", "-i");
            if (inputPorts.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Any(line => line.StartsWith($"{nodeName}:", StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(PollDelay);
        }

        throw new CliException($"Timed out after {PollAttempts * PollDelay.TotalMilliseconds / 1000:0.##}s waiting for recorder ports (node: {nodeName}). Is PipeWire running?");
    }

    private static async Task CreateLinksAsync(string sink, string nodeName, CaptureChannel channel)
    {
        var leftSource = $"{sink}:monitor_FL";
        var rightSource = $"{sink}:monitor_FR";
        var leftTarget = $"{nodeName}:input_FL";
        var rightTarget = $"{nodeName}:input_FR";

        switch (channel)
        {
            case CaptureChannel.Stereo:
                await LinkAsync(leftSource, leftTarget);
                await LinkAsync(rightSource, rightTarget);
                break;
            case CaptureChannel.Left:
                await LinkAsync(leftSource, leftTarget);
                await LinkAsync(leftSource, rightTarget);
                break;
            case CaptureChannel.Right:
                await LinkAsync(rightSource, leftTarget);
                await LinkAsync(rightSource, rightTarget);
                break;
        }
    }

    private static async Task LinkAsync(string source, string target)
    {
        Console.WriteLine($"Linking {source} -> {target}");
        var exitCode = await RunProcessAsync("pw-link", source, target);
        if (exitCode != 0)
        {
            throw new CliException($"Failed to link {source} -> {target}. Check the sink name with 'dotnet run -- list-sinks'.");
        }
    }

    private static async Task<string> DetectDefaultSinkAsync()
    {
        var sinks = await GetAvailableSinksAsync();
        var sink = sinks.FirstOrDefault(sink => sink.IsDefault) ?? sinks.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(sink?.Name))
        {
            throw new CliException(
                "Could not automatically detect the default audio sink.\n\n" +
                "To find available sinks, run:\n" +
                "  dotnet run -- list-sinks\n" +
                "  pw-link -o");
        }

        return sink.Name;
    }

    private static async Task<IReadOnlyList<SinkInfo>> GetAvailableSinksAsync()
    {
        if (CommandExists("wpctl"))
        {
            var sinks = await GetSinksFromWpctlAsync();
            if (sinks.Count > 0)
            {
                return sinks;
            }
        }

        return await GetSinksFromPwLinkAsync();
    }

    private static async Task<IReadOnlyList<SinkInfo>> GetSinksFromWpctlAsync()
    {
        var status = await RunProcessForOutputAsync("wpctl", "status");
        var results = new List<SinkInfo>();
        var inSinksSection = false;

        foreach (var line in status.Split(Environment.NewLine, StringSplitOptions.None))
        {
            var trimmed = line.Trim();

            if (trimmed.Contains("Sinks:", StringComparison.Ordinal))
            {
                inSinksSection = true;
                continue;
            }

            if (!inSinksSection)
            {
                continue;
            }

            if (trimmed.Length == 0)
            {
                if (results.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (trimmed.EndsWith(':') && !trimmed.Contains('.', StringComparison.Ordinal))
            {
                break;
            }

            var match = Regex.Match(trimmed, @"^(?<default>\*)?\s*(?<id>\d+)\.");
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value;
            var name = await GetNodeNameFromWpctlAsync(id);
            if (!string.IsNullOrWhiteSpace(name))
            {
                results.Add(new SinkInfo(name, match.Groups["default"].Success));
            }
        }

        return results
            .GroupBy(sink => sink.Name, StringComparer.Ordinal)
            .Select(group => new SinkInfo(group.Key, group.Any(sink => sink.IsDefault)))
            .ToList();
    }

    private static async Task<string?> GetNodeNameFromWpctlAsync(string id)
    {
        var inspect = await RunProcessForOutputAsync("wpctl", "inspect", id);

        foreach (var line in inspect.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("node.name", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = trimmed.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            return parts[1].Trim().Trim('"');
        }

        return null;
    }

    private static async Task<IReadOnlyList<SinkInfo>> GetSinksFromPwLinkAsync()
    {
        var output = await RunProcessForOutputAsync("pw-link", "-o");
        return output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.EndsWith(":monitor_FL", StringComparison.Ordinal))
            .Select(line => line[..^":monitor_FL".Length].Trim())
            .Distinct(StringComparer.Ordinal)
            .Select(name => new SinkInfo(name, false))
            .ToList();
    }

    private static async Task WaitForRecorderExitAsync(Process recorder, CancellationToken cancellationToken)
    {
        try
        {
            await recorder.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Stopping recorder...");
        }
    }

    private static async Task StopRecorderAsync(Process recorder)
    {
        if (recorder.HasExited)
        {
            return;
        }

        await RunProcessAsync("kill", "-INT", recorder.Id.ToString());
        await recorder.WaitForExitAsync();
    }

    private static void EnsureCommandExists(string command, string errorMessage)
    {
        if (!CommandExists(command))
        {
            throw new CliException(errorMessage);
        }
    }

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(segment, command);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<int> RunProcessAsync(string fileName, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<string> RunProcessForOutputAsync(string fileName, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var output = await stdout;
        var error = await stderr;

        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new CliException($"{fileName} failed with exit code {process.ExitCode}: {detail.Trim()}");
        }

        return output;
    }

    private static string GetDefaultOutputPath() =>
        Path.GetFullPath($"speaker_capture_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

    private static void PrintHelp()
    {
        var builder = new StringBuilder();
        builder.AppendLine("ubsndrec - PipeWire speaker capture");
        builder.AppendLine();
        builder.AppendLine("Usage:");
        builder.AppendLine("  dotnet run -- [output.wav]");
        builder.AppendLine("  dotnet run -- record [output.wav] [--sink <name>] [--channel stereo|left|right]");
        builder.AppendLine("  dotnet run -- wizard");
        builder.AppendLine("  dotnet run -- list-sinks");
        builder.AppendLine();
        builder.AppendLine("Modes:");
        builder.AppendLine("  record      Record immediately using the provided options.");
        builder.AppendLine("  wizard      Guide the user through sink, output, and channel selection.");
        builder.AppendLine("  list-sinks  Print available PipeWire sink names.");
        builder.AppendLine();
        builder.AppendLine("Channel modes:");
        builder.AppendLine("  stereo  Record left and right monitors as-is.");
        builder.AppendLine("  left    Record the left monitor into both output channels.");
        builder.AppendLine("  right   Record the right monitor into both output channels.");
        Console.Write(builder.ToString());
    }
}

internal sealed class CliException(string message) : Exception(message);

internal sealed record SinkInfo(string Name, bool IsDefault);

internal sealed class CaptureOptions
{
    public CliMode Mode { get; set; } = CliMode.Record;
    public bool ShowHelp { get; set; }
    public string? Output { get; set; }
    public string? Sink { get; set; }
    public CaptureChannel Channel { get; set; } = CaptureChannel.Stereo;
}

internal enum CliMode
{
    Record,
    Wizard,
    ListSinks
}

internal enum CaptureChannel
{
    Stereo,
    Left,
    Right
}
