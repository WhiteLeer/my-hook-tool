using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyHookTool;

internal static class CaptureSessionCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        var options = ParseOptions(args);
        var renderDoc = RequiredFile(options, "renderdoc");
        var target = RequiredFile(options, "target");
        var module = RequiredFile(options, "module");
        var outputRoot = Path.GetFullPath(RequiredValue(options, "output"));
        var profilePath = OptionalFile(options, "profile");
        var profile = profilePath is null
            ? new JsonObject
            {
                ["id"] = Environment.GetEnvironmentVariable("MY_HOOK_PROFILE") ?? "runtime"
            }
            : ReadObject(profilePath, "profile");
        var profileId = profile["id"]?.GetValue<string>() ?? "runtime";
        var name = Sanitize(options.TryGetValue("name", out var names) ? names[^1] :
            $"{profileId}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}");
        var sessionDir = Path.Combine(outputRoot, name);
        if (Directory.Exists(sessionDir))
            throw new IOException($"会话目录已存在，不覆盖历史会话: {sessionDir}");

        Directory.CreateDirectory(sessionDir);
        var runtimeDir = Path.Combine(sessionDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        var eventsPath = Path.Combine(runtimeDir, "events.ndjson");
        File.WriteAllText(eventsPath, string.Empty);
        var hookPath = Path.Combine(sessionDir, name + ".hook");
        var captureSessionId = $"{name}-{Guid.NewGuid():N}";
        var captureFile = options.TryGetValue("capture-file", out var captureFiles)
            ? Path.GetFullPath(captureFiles[^1])
            : Path.Combine(sessionDir, "capture.srrdc");
        Directory.CreateDirectory(Path.GetDirectoryName(captureFile)!);

        var hook = CreateHook(profile, profileId, name, captureSessionId, target, module,
            eventsPath, captureFile, renderDoc);
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));

        var renderDocArgs = new List<string> { "capture", "--capture-file", captureFile };
        if (options.TryGetValue("working-directory", out var workingDirectories))
            renderDocArgs.AddRange(new[] { "--working-dir", Path.GetFullPath(workingDirectories[^1]) });
        AddFlag(options, "opt-api-validation", renderDocArgs);
        AddFlag(options, "opt-ref-all-resources", renderDocArgs);
        AddFlag(options, "opt-capture-all-cmd-lists", renderDocArgs);
        renderDocArgs.Add(target);
        if (options.TryGetValue("target-arg", out var targetArgs))
            renderDocArgs.AddRange(targetArgs);

        var start = new ProcessStartInfo(renderDoc)
        {
            WorkingDirectory = Path.GetDirectoryName(renderDoc)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment["MY_HOOK_PROFILE"] = profileId;
        start.Environment["MY_HOOK_OUTPUT"] = runtimeDir;
        start.Environment["MY_HOOK_EVENTS"] = eventsPath;
        start.Environment["MY_HOOK_FILE"] = hookPath;
        start.Environment["MY_HOOK_CAPTURE_SESSION_ID"] = captureSessionId;
        foreach (var argument in renderDocArgs)
            start.ArgumentList.Add(argument);

        Console.WriteLine($"启动 RenderDoc capture，会话: {captureSessionId}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 RenderDoc capture");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(stdout.Result)) Console.Write(stdout.Result);
        if (!string.IsNullOrWhiteSpace(stderr.Result)) Console.Error.WriteLine(stderr.Result.Trim());
        if (process.ExitCode <= 0)
            throw new InvalidOperationException(
                $"RenderDoc 没有返回有效 TargetControl ident，退出码: {process.ExitCode}");

        var targetControlIdent = process.ExitCode;
        var triggerTool = Program.ResolveRenderDocTriggerTool(options, profileId);
        var targetPid = Program.RunRenderDocTargetControl(
            triggerTool, renderDoc, targetControlIdent, queryOnly: true, frames: 1);
        hook["session"]!["targetPid"] = targetPid;
        hook["session"]!["status"] = "renderdoc_launched";
        hook["session"]!["targetControlIdent"] = targetControlIdent;
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));
        Console.WriteLine($"RenderDoc 目标 PID: {targetPid}，加载 Hook 模块...");
        Program.InjectIntoProcess(targetPid, module);
        hook["session"]!["status"] = "injected";
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));
        Console.WriteLine($"已创建 Hook 会话: {hookPath}");
        Console.WriteLine("现在可在目标进程中触发 RenderDoc capture；Hook 事件会追加到 runtime/events.ndjson。捕获后执行 align。");
        return 0;
    }

    private static JsonObject CreateHook(JsonObject profile, string profileId, string name,
        string captureSessionId, string target, string module, string eventsPath,
        string captureFile, string renderDoc)
    {
        var hookDirectory = Directory.GetParent(Path.GetDirectoryName(eventsPath)!)!.FullName;
        return new JsonObject
        {
            ["format"] = "my-hook.runtime.v1",
            ["tool"] = new JsonObject
            {
                ["name"] = "my-hook-tool",
                ["version"] = "0.1.0",
                ["mode"] = "renderdoc-coordinated-runtime"
            },
            ["profile"] = profile.DeepClone(),
            ["session"] = new JsonObject
            {
                ["id"] = name,
                ["captureSessionId"] = captureSessionId,
                ["status"] = "preparing",
                ["startedUtc"] = DateTime.UtcNow.ToString("O"),
                ["target"] = target,
                ["injectedModule"] = module,
                ["renderDoc"] = renderDoc,
                ["captureFile"] = captureFile
            },
            ["protocol"] = new JsonObject
            {
                ["eventFile"] = "runtime/events.ndjson",
                ["eventFormat"] = "my-hook.runtime-event.v1",
                ["environment"] = new JsonObject
                {
                    ["MY_HOOK_PROFILE"] = profileId,
                    ["MY_HOOK_OUTPUT"] = "runtime",
                    ["MY_HOOK_CAPTURE_SESSION_ID"] = captureSessionId
                }
            },
            ["records"] = new JsonArray(),
            ["diagnostics"] = new JsonArray()
        };
    }

    private static void AddFlag(Dictionary<string, List<string>> options, string key, List<string> args)
    {
        if (options.ContainsKey(key)) args.Add("--" + key);
    }

    private static JsonObject ReadObject(string path, string label) =>
        JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidDataException($"{label} 不是 JSON 对象: {path}");

    private static string RequiredValue(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values) && values.Count > 0
            ? values[^1]
            : throw new ArgumentException($"capture-session 缺少 --{key}");

    private static string RequiredFile(Dictionary<string, List<string>> options, string key)
    {
        var path = Path.GetFullPath(RequiredValue(options, key));
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到 --{key}", path);
        return path;
    }

    private static string? OptionalFile(Dictionary<string, List<string>> options, string key)
    {
        if (!options.TryGetValue(key, out var values)) return null;
        var path = Path.GetFullPath(values[^1]);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到 --{key}", path);
        return path;
    }

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"capture-session 不接受位置参数: {args[i]}");
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "1";
            if (!result.TryGetValue(key, out var values)) result[key] = values = [];
            values.Add(value);
        }
        return result;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }
}
