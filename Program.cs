using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length == 0 || IsHelp(args[0]))
{
    PrintHelp();
    return 0;
}

try
{
    var command = args[0].ToLowerInvariant();
    var rest = args.Skip(1).ToArray();

    return command switch
    {
        "extract" => ExtractCommand(rest),
        "apply" => ApplyCommand(rest),
        "translate" => TranslateCommand(rest),
        "process" => ProcessCommand(rest),
        "inspect" => InspectCommand(rest),
        _ => Fail($"Unknown command: {args[0]}")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return 1;
}

static int ExtractCommand(string[] args)
{
    if (args.Length < 2)
    {
        return Fail("Usage: ScenarioTextLocator extract <input.txt> <output.tsv> [options]");
    }

    var inputPath = args[0];
    var outputPath = args[1];
    var options = Options.Parse(args.Skip(2));
    var inputEncoding = GetEncoding(options.Get("encoding", "shift-jis"));
    var outputEncoding = GetEncoding(options.Get("tsv-encoding", "utf-8-bom"));
    var includeComments = options.Has("include-comments");

    var entries = ExtractEntries(inputPath, inputEncoding, includeComments);
    WriteExtractTsv(outputPath, outputEncoding, entries);

    Console.WriteLine($"Extracted {entries.Count} text segment(s).");
    Console.WriteLine($"Input : {inputPath}");
    Console.WriteLine($"Output: {outputPath}");
    Console.WriteLine($"Input encoding : {inputEncoding.WebName}");
    Console.WriteLine($"TSV encoding   : {DescribeEncoding(outputEncoding)}");
    return 0;
}

static int ApplyCommand(string[] args)
{
    if (args.Length < 3)
    {
        return Fail("Usage: ScenarioTextLocator apply <input.txt> <translated.tsv> <output.txt> [options]");
    }

    var inputPath = args[0];
    var tsvPath = args[1];
    var outputPath = args[2];
    var options = Options.Parse(args.Skip(3));
    var inputEncoding = GetEncoding(options.Get("encoding", "shift-jis"));
    var tsvEncoding = GetEncoding(options.Get("tsv-encoding", "utf-8-bom"));
    var outputEncoding = GetEncoding(options.Get("output-encoding", "utf-8-bom"));
    var allowChangedSource = options.Has("allow-changed-source");

    var lines = ReadAllLinesPreserveEndings(inputPath, inputEncoding);
    var rows = ReadTranslationRows(tsvPath, tsvEncoding)
        .Where(row => !string.IsNullOrEmpty(row.Translation))
        .OrderByDescending(row => row.Line)
        .ThenByDescending(row => row.Start)
        .ToList();

    var changed = 0;
    var skipped = 0;

    foreach (var row in rows)
    {
        if (row.Line < 1 || row.Line > lines.Count)
        {
            throw new InvalidOperationException($"Row {row.Id}: line {row.Line} is outside input file.");
        }

        var line = lines[row.Line - 1].Text;
        if (row.Start < 0 || row.Length < 0 || row.Start + row.Length > line.Length)
        {
            throw new InvalidOperationException($"Row {row.Id}: range Start={row.Start}, Length={row.Length} is outside line {row.Line}.");
        }

        var originalAtPosition = line.Substring(row.Start, row.Length);
        var hashAtPosition = TextHash.Compute(originalAtPosition);
        if (!TextHash.Equals(hashAtPosition, row.Hash))
        {
            if (!allowChangedSource)
            {
                throw new InvalidOperationException(
                    $"Row {row.Id}: source text changed at line {row.Line}. Expected hash {row.Hash}, found {hashAtPosition}. " +
                    "Use --allow-changed-source only if you checked the mismatch manually.");
            }

            skipped++;
            continue;
        }

        lines[row.Line - 1] = lines[row.Line - 1] with
        {
            Text = line[..row.Start] + row.Translation + line[(row.Start + row.Length)..]
        };
        changed++;
    }

    WriteAllLinesPreserveEndingsAtomic(outputPath, lines, outputEncoding);

    Console.WriteLine($"Applied {changed} translation(s).");
    if (skipped > 0)
    {
        Console.WriteLine($"Skipped {skipped} changed source segment(s).");
    }
    Console.WriteLine($"Input : {inputPath}");
    Console.WriteLine($"TSV   : {tsvPath}");
    Console.WriteLine($"Output: {outputPath}");
    Console.WriteLine($"Output encoding: {DescribeEncoding(outputEncoding)}");
    return 0;
}

static int TranslateCommand(string[] args)
{
    if (args.Length < 2)
    {
        return Fail("Usage: ScenarioTextLocator translate <input.tsv> <output.tsv> [options]");
    }

    var inputTsvPath = args[0];
    var outputTsvPath = args[1];
    var options = Options.Parse(args.Skip(2));
    var tsvEncoding = GetEncoding(options.Get("tsv-encoding", "utf-8-bom"));
    var endpoint = options.Get("endpoint", "http://localhost:1234/v1/chat/completions");
    var model = options.Get("model", "");
    var temperature = ParseDouble(options.Get("temperature", "0.2"), "temperature");
    var delayMs = ParseInt(options.Get("delay-ms", "0"), "delay-ms");
    var retryCount = ParseInt(options.Get("retries", "2"), "retries");
    var timeoutSeconds = ParseInt(options.Get("timeout", "0"), "timeout");
    var maxRows = options.Has("max-rows") ? ParseInt(options.Get("max-rows", "0"), "max-rows") : 0;
    var repeatMinCopies = ParseInt(options.Get("repeat-min-copies", "3"), "repeat-min-copies");
    var overwrite = options.Has("overwrite");
    var stream = !options.Has("no-stream");

    if (string.IsNullOrWhiteSpace(model))
    {
        Console.Write("LM Studio model name: ");
        model = Console.ReadLine()?.Trim() ?? "";
    }

    if (string.IsNullOrWhiteSpace(model))
    {
        return Fail("Model name is required. Use --model <name> or type it when prompted.");
    }

    var table = TsvTable.Read(inputTsvPath, tsvEncoding);
    if (File.Exists(outputTsvPath) && !Path.GetFullPath(outputTsvPath).Equals(Path.GetFullPath(inputTsvPath), StringComparison.OrdinalIgnoreCase))
    {
        var existingOutput = TsvTable.Read(outputTsvPath, tsvEncoding);
        table.MergeTranslationsFrom(existingOutput);
    }

    var originalIndex = table.GetColumnIndex("Original");
    var translationIndex = table.GetColumnIndex("Translation");
    var idIndex = table.TryGetColumnIndex("Id");
    var lineIndex = table.TryGetColumnIndex("Line");

    using var httpClient = new HttpClient
    {
        Timeout = timeoutSeconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(timeoutSeconds)
    };

    var translated = 0;
    var skipped = 0;
    var failed = 0;

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputTsvPath)) ?? ".");
    table.WriteAtomic(outputTsvPath, tsvEncoding);

    for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
    {
        var row = table.Rows[rowIndex];
        var original = Tsv.Unescape(Tsv.GetCell(row.Cells, originalIndex));
        var currentTranslation = Tsv.Unescape(Tsv.GetCell(row.Cells, translationIndex));

        if (string.IsNullOrWhiteSpace(original))
        {
            skipped++;
            continue;
        }

        if (!overwrite && !string.IsNullOrWhiteSpace(currentTranslation))
        {
            skipped++;
            continue;
        }

        if (maxRows > 0 && translated >= maxRows)
        {
            break;
        }

        var label = idIndex >= 0 ? Tsv.GetCell(row.Cells, idIndex) : $"row {rowIndex + 1}";
        var lineLabel = lineIndex >= 0 ? Tsv.GetCell(row.Cells, lineIndex) : "";
        if (!string.IsNullOrWhiteSpace(lineLabel))
        {
            label += $" line {lineLabel}";
        }

        Console.Write($"[{translated + 1}] {label} ... ");

        try
        {
            var result = TranslateWithLmStudioAsync(httpClient, endpoint, model, original, temperature, retryCount, stream).GetAwaiter().GetResult();
            result = CleanTranslation(result, repeatMinCopies);
            row.SetCell(translationIndex, Tsv.Escape(result));
            translated++;
            Console.WriteLine("ok");

            table.WriteAtomic(outputTsvPath, tsvEncoding);

            if (delayMs > 0)
            {
                Thread.Sleep(delayMs);
            }
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine("failed");
            Console.Error.WriteLine($"  {ex.Message}");
            table.WriteAtomic(outputTsvPath, tsvEncoding);
            return 1;
        }
    }

    table.WriteAtomic(outputTsvPath, tsvEncoding);
    Console.WriteLine();
    Console.WriteLine($"Translated: {translated}");
    Console.WriteLine($"Skipped   : {skipped}");
    Console.WriteLine($"Failed    : {failed}");
    Console.WriteLine($"Output    : {outputTsvPath}");
    return failed == 0 ? 0 : 1;
}

static int ProcessCommand(string[] args)
{
    if (args.Length < 2)
    {
        return Fail("Usage: ScenarioTextLocator process <input.txt> <output.txt> [options]");
    }

    var inputPath = args[0];
    var outputPath = args[1];
    var options = Options.Parse(args.Skip(2));

    var inputEncoding = GetEncoding(options.Get("encoding", "shift-jis"));
    var tsvEncoding = GetEncoding(options.Get("tsv-encoding", "utf-8-bom"));
    var outputEncoding = GetEncoding(options.Get("output-encoding", "utf-8-bom"));
    var endpoint = options.Get("endpoint", "http://localhost:1234/v1/chat/completions");
    var model = options.Get("model", "");
    var temperature = ParseDouble(options.Get("temperature", "0.2"), "temperature");
    var delayMs = ParseInt(options.Get("delay-ms", "0"), "delay-ms");
    var retryCount = ParseInt(options.Get("retries", "2"), "retries");
    var timeoutSeconds = ParseInt(options.Get("timeout", "0"), "timeout");
    var maxRows = options.Has("max-rows") ? ParseInt(options.Get("max-rows", "0"), "max-rows") : 0;
    var repeatMinCopies = ParseInt(options.Get("repeat-min-copies", "3"), "repeat-min-copies");
    var includeComments = options.Has("include-comments");
    var overwrite = options.Has("overwrite");
    var stream = !options.Has("no-stream");
    var workDir = options.Get("work-dir", GetDefaultWorkDir(outputPath));

    if (string.IsNullOrWhiteSpace(model))
    {
        Console.Write("LM Studio model name: ");
        model = Console.ReadLine()?.Trim() ?? "";
    }

    if (string.IsNullOrWhiteSpace(model))
    {
        return Fail("Model name is required. Use --model <name> or type it when prompted.");
    }

    Directory.CreateDirectory(workDir);
    var extractPath = Path.Combine(workDir, "extract.tsv");
    var translatedPath = Path.Combine(workDir, "translated.tsv");
    var logPath = Path.Combine(workDir, "process.log");

    LogLine(logPath, $"[{DateTimeOffset.Now:O}] process start");
    LogLine(logPath, $"input={inputPath}");
    LogLine(logPath, $"output={outputPath}");
    LogLine(logPath, $"work={workDir}");

    if (!File.Exists(extractPath) || IsSourceNewer(inputPath, extractPath) || IsSourceNewer(inputPath, translatedPath))
    {
        LogLine(logPath, "extracting...");
        var entries = ExtractEntries(inputPath, inputEncoding, includeComments);
        WriteExtractTsv(extractPath, tsvEncoding, entries);
    }
    else
    {
        LogLine(logPath, "reuse extract.tsv");
    }

    var translateArgs = new List<string>
    {
        extractPath,
        translatedPath,
        "--endpoint", endpoint,
        "--model", model,
        "--temperature", temperature.ToString(CultureInfo.InvariantCulture),
        "--timeout", timeoutSeconds.ToString(CultureInfo.InvariantCulture),
        "--retries", retryCount.ToString(CultureInfo.InvariantCulture),
        "--delay-ms", delayMs.ToString(CultureInfo.InvariantCulture),
        "--repeat-min-copies", repeatMinCopies.ToString(CultureInfo.InvariantCulture)
    };
    if (maxRows > 0)
    {
        translateArgs.Add("--max-rows");
        translateArgs.Add(maxRows.ToString(CultureInfo.InvariantCulture));
    }
    if (overwrite)
    {
        translateArgs.Add("--overwrite");
    }
    if (!stream)
    {
        translateArgs.Add("--no-stream");
    }

    LogLine(logPath, "translating...");
    var translateExit = TranslateCommand(translateArgs.ToArray());
    if (translateExit != 0)
    {
        LogLine(logPath, $"translate failed: {translateExit}");
        return translateExit;
    }

    LogLine(logPath, "applying...");
    var applyExit = ApplyCommand(new[]
    {
        inputPath,
        translatedPath,
        outputPath,
        "--encoding", inputEncoding.WebName,
        "--tsv-encoding", DescribeEncoding(tsvEncoding),
        "--output-encoding", DescribeEncoding(outputEncoding)
    });
    LogLine(logPath, $"apply exit={applyExit}");
    LogLine(logPath, $"[{DateTimeOffset.Now:O}] process end");

    return applyExit;
}

static int InspectCommand(string[] args)
{
    if (args.Length < 1)
    {
        return Fail("Usage: ScenarioTextLocator inspect <input.txt> [options]");
    }

    var inputPath = args[0];
    var options = Options.Parse(args.Skip(1));
    var inputEncoding = GetEncoding(options.Get("encoding", "shift-jis"));
    var includeComments = options.Has("include-comments");

    var lines = ReadAllLinesPreserveEndings(inputPath, inputEncoding);
    var entries = new List<TextEntry>();
    var lineKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
    {
        var text = lines[lineIndex].Text;
        AddCount(lineKinds, SegmentFinder.ClassifyLine(text));
        entries.AddRange(SegmentFinder.Find(text, lineIndex + 1, includeComments));
    }

    Console.WriteLine($"Lines          : {lines.Count}");
    Console.WriteLine($"Text segments  : {entries.Count}");
    Console.WriteLine($"Input encoding : {inputEncoding.WebName}");
    Console.WriteLine();
    Console.WriteLine("Line kinds:");
    foreach (var item in lineKinds.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
    {
        Console.WriteLine($"  {item.Key,-12} {item.Value}");
    }

    Console.WriteLine();
    Console.WriteLine("Segment kinds:");
    foreach (var item in entries.GroupBy(x => x.Kind).OrderByDescending(x => x.Count()).ThenBy(x => x.Key))
    {
        Console.WriteLine($"  {item.Key,-12} {item.Count()}");
    }

    return 0;
}

static void AddCount(Dictionary<string, int> counts, string key)
{
    counts.TryGetValue(key, out var count);
    counts[key] = count + 1;
}

static List<TextEntry> ExtractEntries(string inputPath, Encoding inputEncoding, bool includeComments)
{
    var lines = ReadAllLinesPreserveEndings(inputPath, inputEncoding);
    var entries = new List<TextEntry>();

    for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
    {
        var line = lines[lineIndex];
        foreach (var segment in SegmentFinder.Find(line.Text, lineIndex + 1, includeComments))
        {
            entries.Add(segment);
        }
    }

    return entries;
}

static void WriteExtractTsv(string outputPath, Encoding outputEncoding, IEnumerable<TextEntry> entries)
{
    WriteAtomic(outputPath, outputEncoding, writer =>
    {
        writer.WriteLine("# ScenarioTextLocator TSV v1");
        writer.WriteLine("# Fill Translation. Leave it blank to keep Original.");
        writer.WriteLine("# Do not edit Id, Line, Start, Length, Kind, Speaker, Original, Hash.");
        writer.WriteLine("Id\tLine\tStart\tLength\tKind\tSpeaker\tOriginal\tTranslation\tHash");

        foreach (var entry in entries)
        {
            writer.WriteLine(string.Join('\t',
                Tsv.Escape(entry.Id),
                entry.LineNumber.ToString(),
                entry.Start.ToString(),
                entry.Length.ToString(),
                Tsv.Escape(entry.Kind),
                Tsv.Escape(entry.Speaker),
                Tsv.Escape(entry.Original),
                "",
                Tsv.Escape(entry.Hash)));
        }
    });
}

static void WriteAllLinesPreserveEndingsAtomic(string path, IReadOnlyList<LineRecord> lines, Encoding encoding)
{
    WriteAtomic(path, encoding, writer =>
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            writer.Write(line.Text);
            writer.Write(line.Ending);
        }
    });
}

static void WriteAtomic(string path, Encoding encoding, Action<StreamWriter> writeAction)
{
    var fullPath = Path.GetFullPath(path);
    var directory = Path.GetDirectoryName(fullPath) ?? ".";
    Directory.CreateDirectory(directory);
    var tempPath = Path.Combine(directory, Path.GetFileName(fullPath) + ".tmp");

    using (var writer = new StreamWriter(tempPath, false, encoding))
    {
        writeAction(writer);
    }

    if (File.Exists(fullPath))
    {
        File.Replace(tempPath, fullPath, null);
    }
    else
    {
        File.Move(tempPath, fullPath);
    }
}

static bool IsSourceNewer(string sourcePath, string derivedPath)
{
    if (!File.Exists(derivedPath))
    {
        return true;
    }

    return File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(derivedPath);
}

static string GetDefaultWorkDir(string outputPath)
{
    var fullPath = Path.GetFullPath(outputPath);
    var directory = Path.GetDirectoryName(fullPath) ?? ".";
    var name = Path.GetFileNameWithoutExtension(fullPath);
    return Path.Combine(directory, name + ".ScenarioTextLocator-work");
}

static void LogLine(string logPath, string message)
{
    File.AppendAllText(logPath, message + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}

static async Task<string> TranslateWithLmStudioAsync(
    HttpClient httpClient,
    string endpoint,
    string model,
    string original,
    double temperature,
    int retryCount,
    bool stream)
{
    Exception? lastError = null;

    for (var attempt = 0; attempt <= retryCount; attempt++)
    {
        try
        {
            var requestBody = new
            {
                model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = original
                    }
                },
                temperature,
                stream
            };

            using var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = jsonContent
            };
            using var response = await httpClient.SendAsync(
                request,
                stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"LM Studio returned {(int)response.StatusCode}: {responseText}");
            }

            if (stream)
            {
                return await ReadSseCompletionAsync(response).ConfigureAwait(false);
            }

            var jsonText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(jsonText);
            var root = document.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("LM Studio response has no choices.");
            }

            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                return content.GetString() ?? "";
            }

            if (first.TryGetProperty("text", out var text))
            {
                return text.GetString() ?? "";
            }

            throw new InvalidOperationException("LM Studio response has no message.content.");
        }
        catch (Exception ex) when (attempt < retryCount)
        {
            lastError = ex;
            Thread.Sleep(1000 * (attempt + 1));
        }
        catch (Exception ex)
        {
            lastError = ex;
            break;
        }
    }

    throw new InvalidOperationException(lastError?.Message ?? "LM Studio request failed.");
}

static async Task<string> ReadSseCompletionAsync(HttpResponseMessage response)
{
    var builder = new StringBuilder();
    try
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? currentEvent = null;

        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                currentEvent = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                currentEvent = line[6..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (line.StartsWith("{", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal))
                {
                    AppendStreamPayload(builder, line, currentEvent);
                }
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            if (payload == "[DONE]")
            {
                break;
            }

            AppendStreamPayload(builder, payload, currentEvent);
        }
    }
    catch (IOException) when (builder.Length > 0)
    {
        return builder.ToString();
    }
    catch (HttpRequestException) when (builder.Length > 0)
    {
        return builder.ToString();
    }
    catch (JsonException) when (builder.Length > 0)
    {
        return builder.ToString();
    }

    return builder.ToString();
}

static void AppendStreamPayload(StringBuilder builder, string payload, string? currentEvent)
{
    using var document = JsonDocument.Parse(payload);
    var root = document.RootElement;

    if (root.ValueKind == JsonValueKind.Object)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var deltaContent))
            {
                builder.Append(deltaContent.GetString());
                return;
            }

            if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var messageContent))
            {
                builder.Append(messageContent.GetString());
                return;
            }

            if (choice.TryGetProperty("text", out var text))
            {
                builder.Append(text.GetString());
                return;
            }
        }

        if (root.TryGetProperty("type", out var typeProperty) &&
            typeProperty.GetString() is string typeValue &&
            typeValue.EndsWith(".delta", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("content", out var contentProperty))
        {
            builder.Append(contentProperty.GetString());
            return;
        }

        if (string.Equals(currentEvent, "message.delta", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("content", out var eventContent))
        {
            builder.Append(eventContent.GetString());
        }
    }
}

static string CleanTranslation(string text, int repeatMinCopies)
{
    var trimmed = text.Trim();

    if (trimmed.StartsWith("```", StringComparison.Ordinal))
    {
        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd >= 0)
        {
            trimmed = trimmed[(firstLineEnd + 1)..];
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3];
        }
    }

    trimmed = trimmed.Trim();
    trimmed = TrimTailRepeats(trimmed, repeatMinCopies);
    return trimmed.Trim();
}

static string TrimTailRepeats(string text, int repeatMinCopies)
{
    if (text.Length < 6)
    {
        return text;
    }

    var best = text;
    for (var unitLen = 2; unitLen <= Math.Min(8, text.Length / 3); unitLen++)
    {
        var candidate = TrimTailRepeatUnit(text, unitLen, repeatMinCopies);
        if (candidate.Length < best.Length)
        {
            best = candidate;
        }
    }

    return best;
}

static string TrimTailRepeatUnit(string text, int unitLen, int repeatMinCopies)
{
    if (unitLen <= 0 || repeatMinCopies < 3 || text.Length < unitLen * repeatMinCopies)
    {
        return text;
    }

    var end = text.Length;
    var copies = 1;

    while (end >= unitLen * 2)
    {
        var suffix = text[(end - unitLen)..end];
        var prev = text[(end - unitLen * 2)..(end - unitLen)];
        if (!string.Equals(suffix, prev, StringComparison.Ordinal))
        {
            break;
        }

        copies++;
        end -= unitLen;
    }

    if (copies < repeatMinCopies)
    {
        return text;
    }

    var prefix = text[..end];
    return string.IsNullOrEmpty(prefix) ? text[..unitLen] : prefix;
}

static List<LineRecord> ReadAllLinesPreserveEndings(string path, Encoding encoding)
{
    var content = File.ReadAllText(path, encoding);
    var records = new List<LineRecord>();
    var index = 0;

    while (index < content.Length)
    {
        var nextLf = content.IndexOf('\n', index);
        if (nextLf < 0)
        {
            records.Add(new LineRecord(content[index..], ""));
            return records;
        }

        var lineEndStart = nextLf;
        if (lineEndStart > index && content[lineEndStart - 1] == '\r')
        {
            lineEndStart--;
        }

        var text = content[index..lineEndStart];
        var ending = content[lineEndStart..(nextLf + 1)];
        records.Add(new LineRecord(text, ending));
        index = nextLf + 1;
    }

    return records;
}

static List<TranslationRow> ReadTranslationRows(string path, Encoding encoding)
{
    var rows = new List<TranslationRow>();
    using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);

    string? headerLine = null;
    while (reader.ReadLine() is { } line)
    {
        if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        headerLine = line;
        break;
    }

    if (headerLine is null)
    {
        throw new InvalidOperationException("TSV has no header row.");
    }

    var headers = Tsv.Split(headerLine);
    var index = headers
        .Select((name, i) => new { name, i })
        .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

    string[] required = ["Id", "Line", "Start", "Length", "Original", "Translation", "Hash"];
    foreach (var name in required)
    {
        if (!index.ContainsKey(name))
        {
            throw new InvalidOperationException($"TSV missing required column: {name}");
        }
    }

    while (reader.ReadLine() is { } line)
    {
        if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var cells = Tsv.Split(line);
        rows.Add(new TranslationRow(
            Tsv.GetCell(cells, index["Id"]),
            ParseInt(Tsv.GetCell(cells, index["Line"]), "Line"),
            ParseInt(Tsv.GetCell(cells, index["Start"]), "Start"),
            ParseInt(Tsv.GetCell(cells, index["Length"]), "Length"),
            Tsv.Unescape(Tsv.GetCell(cells, index["Original"])),
            Tsv.Unescape(Tsv.GetCell(cells, index["Translation"])),
            Tsv.GetCell(cells, index["Hash"])));
    }

    return rows;
}

static int ParseInt(string text, string name)
{
    if (!int.TryParse(text, out var value))
    {
        throw new InvalidOperationException($"Invalid integer in {name}: {text}");
    }

    return value;
}

static double ParseDouble(string text, string name)
{
    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
    {
        throw new InvalidOperationException($"Invalid number in {name}: {text}");
    }

    return value;
}

static Encoding GetEncoding(string name)
{
    return name.Trim().ToLowerInvariant() switch
    {
        "utf8" or "utf-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        "utf8-bom" or "utf-8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "shift-jis" or "shift_jis" or "sjis" or "cp932" or "932" => Encoding.GetEncoding(932),
        "unicode" or "utf-16" or "utf-16le" => Encoding.Unicode,
        _ => Encoding.GetEncoding(name)
    };
}

static string DescribeEncoding(Encoding encoding)
{
    if (encoding is UTF8Encoding utf8)
    {
        return utf8.GetPreamble().Length > 0 ? "utf-8-bom" : "utf-8";
    }

    return encoding.WebName;
}

static bool IsHelp(string arg)
{
    return arg is "-h" or "--help" or "help" or "/?";
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine();
    PrintHelp();
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
ScenarioTextLocator

Locate translatable Japanese text in visual-novel style scenario scripts without touching control fields.

Commands:
  extract <input.txt> <output.tsv> [options]
      Export dialogue/narration text segments to TSV.

  apply <input.txt> <translated.tsv> <output.txt> [options]
      Apply non-empty Translation cells from TSV and write a new script.

  translate <input.tsv> <output.tsv> [options]
      Send Original cells to LM Studio and write model output into Translation.

  process <input.txt> <output.txt> [options]
      One-command pipeline: extract, translate, then apply with checkpoint files.
      Work files live beside the output file unless --work-dir is set.

  inspect <input.txt> [options]
      Print line/segment statistics.

Options:
  --encoding <name>          Source encoding. Default: shift-jis / cp932.
  --tsv-encoding <name>      TSV encoding. Default: utf-8-bom.
  --output-encoding <name>   Output script encoding for apply. Default: utf-8-bom.
  --include-comments         Also extract Japanese text from ';' comment lines.
  --allow-changed-source     In apply, skip rows whose source hash no longer matches.
  --endpoint <url>           LM Studio endpoint. Default: http://localhost:1234/v1/chat/completions.
  --model <name>             LM Studio model name. If omitted, asks in the console.
  --temperature <number>     Translation request temperature. Default: 0.2.
  --timeout <seconds>        HTTP timeout. Default: 0 (infinite).
  --retries <count>          Retry count for failed LM Studio requests. Default: 2.
  --delay-ms <number>        Delay after each successful request. Default: 0.
  --max-rows <number>        Translate at most this many rows in this run.
  --overwrite                Re-translate rows that already have Translation.
  --no-stream                Disable SSE streaming and use a normal JSON response.
  --repeat-min-copies <n>    Trim only if the same tail repeats at least n times. Default: 3.
  --work-dir <path>          Work directory for process mode. Default: beside output file.

Examples:
  ScenarioTextLocator extract "02 - 副本.txt" "02.todo.tsv"
  ScenarioTextLocator translate "02.todo.tsv" "02.translated.tsv" --model "your-model-name"
  ScenarioTextLocator process "02 - 副本.txt" "02.zh.txt" --model "your-model-name"
  ScenarioTextLocator apply "02 - 副本.txt" "02.todo.tsv" "02.zh.txt"

TSV columns:
  Id, Line, Start, Length, Kind, Speaker, Original, Translation, Hash

Only edit the Translation column.
""");
}

internal static partial class SegmentFinder
{
    private static readonly Regex BracketLineRegex = new(@"^\s*\[(?<speaker>[^\]]+)\](?<text>.*)$", RegexOptions.Compiled);
    private static readonly Regex CommandLineRegex = new(@"^\s*[A-Za-z][A-Za-z0-9_]*\b", RegexOptions.Compiled);
    private static readonly Regex JapaneseRegex = JapaneseTextRegex();

    public static IEnumerable<TextEntry> Find(string line, int lineNumber, bool includeComments)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            yield break;
        }

        var trimmedStart = line.TrimStart();

        if (trimmedStart.StartsWith('@'))
        {
            yield break;
        }

        if (trimmedStart.StartsWith(';'))
        {
            if (!includeComments)
            {
                yield break;
            }

            var commentStart = line.IndexOf(';') + 1;
            var commentText = line[commentStart..];
            if (ContainsJapanese(commentText))
            {
                yield return CreateEntry(lineNumber, commentStart, commentText.Length, "Comment", "", commentText);
            }

            yield break;
        }

        var bracketMatch = BracketLineRegex.Match(line);
        if (bracketMatch.Success)
        {
            var speaker = bracketMatch.Groups["speaker"].Value;
            var textGroup = bracketMatch.Groups["text"];
            var text = textGroup.Value;
            if (ContainsJapanese(text))
            {
                yield return CreateEntry(lineNumber, textGroup.Index, text.Length, "Dialogue", speaker, text);
            }

            yield break;
        }

        if (CommandLineRegex.IsMatch(line))
        {
            yield break;
        }

        if (ContainsJapanese(line))
        {
            yield return CreateEntry(lineNumber, 0, line.Length, "Narration", "", line);
        }
    }

    public static string ClassifyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "Blank";
        }

        var trimmedStart = line.TrimStart();

        if (trimmedStart.StartsWith('@'))
        {
            return "Scene";
        }

        if (trimmedStart.StartsWith(';'))
        {
            return "Comment";
        }

        if (BracketLineRegex.IsMatch(line))
        {
            return "Bracket";
        }

        if (CommandLineRegex.IsMatch(line))
        {
            return "Command";
        }

        if (ContainsJapanese(line))
        {
            return "Narration";
        }

        return "Other";
    }

    private static bool ContainsJapanese(string text)
    {
        return JapaneseRegex.IsMatch(text);
    }

    private static TextEntry CreateEntry(int lineNumber, int start, int length, string kind, string speaker, string original)
    {
        return new TextEntry(
            Id: $"L{lineNumber:D5}C{start:D03}",
            LineNumber: lineNumber,
            Start: start,
            Length: length,
            Kind: kind,
            Speaker: speaker,
            Original: original,
            Hash: TextHash.Compute(original));
    }

    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}\u3000-\u303F\uFF66-\uFF9F]", RegexOptions.Compiled)]
    private static partial Regex JapaneseTextRegex();
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(IEnumerable<string> args)
    {
        var options = new Options();
        var parts = args.ToArray();

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (!part.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected positional argument: {part}");
            }

            var nameValue = part[2..].Split('=', 2);
            var name = nameValue[0];
            if (nameValue.Length == 2)
            {
                options._values[name] = nameValue[1];
                continue;
            }

            if (i + 1 < parts.Length && !parts[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options._values[name] = parts[++i];
            }
            else
            {
                options._flags.Add(name);
            }
        }

        return options;
    }

    public bool Has(string name)
    {
        return _flags.Contains(name) || _values.ContainsKey(name);
    }

    public string Get(string name, string defaultValue)
    {
        return _values.TryGetValue(name, out var value) ? value : defaultValue;
    }
}

internal static class Tsv
{
    public static string GetCell(IReadOnlyList<string> cells, int index)
    {
        return index < cells.Count ? cells[index] : "";
    }

    public static string Escape(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    public static string Unescape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                sb.Append(text[i]);
                continue;
            }

            var next = text[++i];
            sb.Append(next switch
            {
                't' => '\t',
                'r' => '\r',
                'n' => '\n',
                '\\' => '\\',
                _ => next
            });
        }

        return sb.ToString();
    }

    public static string[] Split(string line)
    {
        return line.Split('\t');
    }
}

internal sealed class TsvTable
{
    public List<string> Preamble { get; } = [];
    public List<string> Headers { get; } = [];
    public List<TsvRow> Rows { get; } = [];

    public static TsvTable Read(string path, Encoding encoding)
    {
        var table = new TsvTable();
        using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                table.Preamble.Add(line);
                continue;
            }

            table.Headers.AddRange(Tsv.Split(line));
            break;
        }

        if (table.Headers.Count == 0)
        {
            throw new InvalidOperationException($"TSV has no header row: {path}");
        }

        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                table.Preamble.Add(line);
                continue;
            }

            table.Rows.Add(new TsvRow(Tsv.Split(line).ToList()));
        }

        return table;
    }

    public void Write(string path, Encoding encoding)
    {
        using var writer = new StreamWriter(path, false, encoding);
        foreach (var line in Preamble)
        {
            writer.WriteLine(line);
        }

        writer.WriteLine(string.Join('\t', Headers));

        foreach (var row in Rows)
        {
            writer.WriteLine(string.Join('\t', row.Cells));
        }
    }

    public void WriteAtomic(string path, Encoding encoding)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, Path.GetFileName(fullPath) + ".tmp");

        Write(tempPath, encoding);

        if (File.Exists(fullPath))
        {
            File.Replace(tempPath, fullPath, null);
        }
        else
        {
            File.Move(tempPath, fullPath);
        }
    }

    public int GetColumnIndex(string name)
    {
        var index = TryGetColumnIndex(name);
        if (index < 0)
        {
            throw new InvalidOperationException($"TSV missing required column: {name}");
        }

        return index;
    }

    public int TryGetColumnIndex(string name)
    {
        for (var i = 0; i < Headers.Count; i++)
        {
            if (string.Equals(Headers[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    public void MergeTranslationsFrom(TsvTable existingOutput)
    {
        var thisIdIndex = TryGetColumnIndex("Id");
        var thisHashIndex = TryGetColumnIndex("Hash");
        var thisTranslationIndex = TryGetColumnIndex("Translation");
        var otherIdIndex = existingOutput.TryGetColumnIndex("Id");
        var otherHashIndex = existingOutput.TryGetColumnIndex("Hash");
        var otherTranslationIndex = existingOutput.TryGetColumnIndex("Translation");

        if (thisIdIndex < 0 || thisHashIndex < 0 || thisTranslationIndex < 0 || otherIdIndex < 0 || otherHashIndex < 0 || otherTranslationIndex < 0)
        {
            return;
        }

        var translations = existingOutput.Rows
            .Select(row => new
            {
                Id = Tsv.GetCell(row.Cells, otherIdIndex),
                Hash = Tsv.GetCell(row.Cells, otherHashIndex),
                Translation = Tsv.GetCell(row.Cells, otherTranslationIndex)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Hash) && !string.IsNullOrWhiteSpace(x.Translation))
            .ToDictionary(x => x.Id + "\u0000" + x.Hash, x => x.Translation, StringComparer.Ordinal);

        foreach (var row in Rows)
        {
            var id = Tsv.GetCell(row.Cells, thisIdIndex);
            var hash = Tsv.GetCell(row.Cells, thisHashIndex);
            if (translations.TryGetValue(id + "\u0000" + hash, out var translation))
            {
                row.SetCell(thisTranslationIndex, translation);
            }
        }
    }
}

internal sealed class TsvRow(List<string> cells)
{
    public List<string> Cells { get; } = cells;

    public void SetCell(int index, string value)
    {
        while (Cells.Count <= index)
        {
            Cells.Add("");
        }

        Cells[index] = value;
    }
}

internal sealed record TextEntry(
    string Id,
    int LineNumber,
    int Start,
    int Length,
    string Kind,
    string Speaker,
    string Original,
    string Hash);

internal sealed record TranslationRow(
    string Id,
    int Line,
    int Start,
    int Length,
    string Original,
    string Translation,
    string Hash);

internal sealed record LineRecord(string Text, string Ending);

internal static class TextHash
{
    public static string Compute(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static bool Equals(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
