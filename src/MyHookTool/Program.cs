using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyHookTool;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                PrintUsage();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "export" => Export(args[1..]),
                "attach" => Attach(args[1..]),
                "mumu" => AttachMumu(args[1..]),
                "finalize" => FinalizeSession(args[1..]),
                "inspect" => Inspect(args[1..]),
                _ => Fail($"未知命令: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    private static int Export(string[] args)
    {
        var options = ParseOptions(args);
        if (options.Positionals().Count != 1)
            throw new ArgumentException("export 需要一个 capture 路径");

        var capturePath = FullPath(options.Positionals()[0]);
        var renderDoc = RequiredPath(options, "renderdoc");
        var output = FullPath(RequiredValue(options, "output"));
        var profile = LoadProfile(options.GetValueOrDefault("profile"));
        ValidateCapture(capturePath, profile);

        Directory.CreateDirectory(output);
        var renderDocOutput = Path.Combine(output, "renderdoc");
        RecreateDirectory(renderDocOutput);

        var renderDocArgs = new List<string> { "shader-export", capturePath, "--output", renderDocOutput };
        AddRepeated(options, "event", renderDocArgs, "--event");
        AddFlag(options, "reconstruct", renderDocArgs, "--reconstruct");
        AddValue(options, "spirv-cross", renderDocArgs, "--spirv-cross");
        AddFlag(options, "export-resources", renderDocArgs, "--export-resources");
        AddFlag(options, "emit-placeholder-templates", renderDocArgs, "--emit-placeholder-templates");

        Console.WriteLine("执行 RenderDoc shader-export...");
        RunProcess(renderDoc, renderDocArgs);

        var captureManifestPath = Path.Combine(renderDocOutput, "capture.json");
        if (!File.Exists(captureManifestPath))
            throw new InvalidDataException($"RenderDoc 没有生成 capture.json: {captureManifestPath}");

        var capture = JsonNode.Parse(File.ReadAllText(captureManifestPath))?.AsObject()
            ?? throw new InvalidDataException("capture.json 不是 JSON 对象");

        var asInfo = CopyAsReports(options, output);
        var links = ReadAndValidateLinks(options.GetValueOrDefault("link-map"), capture, asInfo);
        var hook = BuildHook(profile, capturePath, capture, output, asInfo, links);
        var hookPath = Path.Combine(output, Path.GetFileNameWithoutExtension(capturePath) + ".hook");
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));

        Console.WriteLine($"已生成: {hookPath}");
        PrintHookSummary(hook);
        return 0;
    }

    private static int Inspect(string[] args)
    {
        var options = ParseOptions(args);
        if (options.Positionals().Count != 1)
            throw new ArgumentException("inspect 需要一个 .hook 路径");
        var path = FullPath(options.Positionals()[0]);
        var hook = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException(".hook 不是 JSON 对象");
        PrintHookSummary(hook);
        return 0;
    }

    private static int Attach(string[] args)
    {
        var options = ParseOptions(args);
        var profile = LoadProfile(options.GetValueOrDefault("profile"));
        var module = RequiredPath(options, "module");
        var outputRoot = FullPath(RequiredValue(options, "output"));
        Directory.CreateDirectory(outputRoot);

        var profileId = StringValue(profile, "id") ?? "runtime";
        var sessionName = options.GetValueOrDefault("name") ??
            $"{profileId}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}";
        sessionName = SanitizeFileName(sessionName);
        var sessionDir = Path.Combine(outputRoot, sessionName);
        Directory.CreateDirectory(sessionDir);
        var runtimeDir = Path.Combine(sessionDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        var eventsPath = Path.Combine(runtimeDir, "events.ndjson");
        File.WriteAllText(eventsPath, string.Empty);

        var hookPath = Path.Combine(sessionDir, sessionName + ".hook");
        var targetPid = 0;
        string targetPath;
        var existingPid = options.GetValueOrDefault("target-pid");
        if (existingPid is not null)
        {
            if (!int.TryParse(existingPid, out targetPid) || targetPid <= 0)
                throw new ArgumentException("--target-pid 必须是正整数");
            targetPath = TryGetProcessPath(targetPid) ?? $"pid:{targetPid}";
            Console.WriteLine($"向现有进程注入: PID {targetPid}");
            InjectIntoProcess(targetPid, module);
        }
        else
        {
            targetPath = RequiredPath(options, "target");
            var workingDirectory = options.GetValueOrDefault("working-directory") is { } wd
                ? FullPath(wd)
                : Path.GetDirectoryName(targetPath)!;
            var arguments = options.GetValueOrDefault("arguments") ?? string.Empty;
            Console.WriteLine($"以挂起方式启动并注入: {targetPath}");
            targetPid = LaunchSuspendedAndInject(
                targetPath,
                arguments,
                workingDirectory,
                module,
                profileId,
                eventsPath,
                hookPath);
        }

        WriteSessionHook(hookPath, profile, sessionName, targetPid, targetPath, module, eventsPath, "injected");

        Console.WriteLine($"已生成会话: {hookPath}");
        Console.WriteLine("运行时桥接模块应向 runtime/events.ndjson 追加 JSON Lines；完成后执行 finalize。");
        return 0;
    }

    private static int FinalizeSession(string[] args)
    {
        var options = ParseOptions(args);
        if (options.Positionals().Count != 1)
            throw new ArgumentException("finalize 需要一个 .hook 路径");
        var hookPath = FullPath(options.Positionals()[0]);
        var hook = JsonNode.Parse(File.ReadAllText(hookPath))?.AsObject()
            ?? throw new InvalidDataException(".hook 不是 JSON 对象");
        var hookDir = Path.GetDirectoryName(hookPath)!;
        var eventRelative = hook["protocol"]?["eventFile"]?.GetValue<string>()
            ?? "runtime/events.ndjson";
        var eventsPath = Path.GetFullPath(Path.Combine(hookDir, eventRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(eventsPath))
            throw new FileNotFoundException("找不到运行时事件文件", eventsPath);

        var records = new JsonArray();
        foreach (var line in File.ReadLines(eventsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = JsonNode.Parse(line)?.AsObject()
                ?? throw new InvalidDataException("运行时事件不是 JSON 对象");
            records.Add(record);
        }
        hook["records"] = records;
        var session = hook["session"]?.AsObject() ?? new JsonObject();
        session["status"] = records.Count == 0 ? "no_records" : "completed";
        session["finishedUtc"] = DateTime.UtcNow.ToString("O");
        hook["session"] = session;
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));
        Console.WriteLine($"已合并运行时记录: {records.Count}");
        PrintHookSummary(hook);
        return 0;
    }

    private static int AttachMumu(string[] args)
    {
        var options = ParseOptions(args);
        var profile = LoadProfile(options.GetValueOrDefault("profile"));
        var module = RequiredPath(options, "module");
        var mumuRoot = ResolveMumuRoot(RequiredValue(options, "mumu-root"));
        var vmIndex = options.GetValueOrDefault("vmindex") ?? "0";
        if (!int.TryParse(vmIndex, out var parsedVmIndex) || parsedVmIndex < 0)
            throw new ArgumentException("--vmindex 必须是非负整数");
        var mumuCli = Path.Combine(mumuRoot, "nx_main", "mumu-cli.exe");
        var mumuHost = Path.Combine(mumuRoot, "nx_main", "MuMuNxMain.exe");
        if (!File.Exists(mumuCli) || !File.Exists(mumuHost))
            throw new FileNotFoundException("MUMU 安装不完整，需要 nx_main\\mumu-cli.exe 和 MuMuNxMain.exe", mumuRoot);

        var outputRoot = FullPath(RequiredValue(options, "output"));
        Directory.CreateDirectory(outputRoot);
        var profileId = StringValue(profile, "id") ?? "runtime";
        var sessionName = SanitizeFileName(options.GetValueOrDefault("name") ??
            $"{profileId}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}");
        var sessionDir = Path.Combine(outputRoot, sessionName);
        Directory.CreateDirectory(sessionDir);
        var runtimeDir = Path.Combine(sessionDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        var eventsPath = Path.Combine(runtimeDir, "events.ndjson");
        File.WriteAllText(eventsPath, string.Empty);
        var hookPath = Path.Combine(sessionDir, sessionName + ".hook");

        Console.WriteLine("停止指定 MUMU 实例，避免桥接模块加载过晚...");
        RunProcess(mumuCli, new[] { "control", "--vmindex", parsedVmIndex.ToString(), "shutdown" });
        RunProcess(mumuCli, new[] { "main", "close" });
        Thread.Sleep(6000);

        var targetPid = LaunchSuspendedAndInject(
            mumuHost,
            string.Empty,
            Path.Combine(mumuRoot, "nx_main"),
            module,
            profileId,
            eventsPath,
            hookPath);
        WriteSessionHook(hookPath, profile, sessionName, targetPid, mumuHost, module, eventsPath, "injected");

        Console.WriteLine("启动指定 MUMU 实例...");
        RunProcess(mumuCli, new[] { "control", "--vmindex", parsedVmIndex.ToString(), "--version", "15", "launch" });
        Console.WriteLine($"宿主已注入，PID {targetPid}；会话: {hookPath}");
        Console.WriteLine("请由客体桥接模块向 runtime/events.ndjson 追加记录，再执行 finalize。");
        return 0;
    }

    private static string ResolveMumuRoot(string value)
    {
        var path = FullPath(value);
        if (File.Exists(path) && string.Equals(Path.GetFileName(path), "MuMuNxMain.exe", StringComparison.OrdinalIgnoreCase))
            path = Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName;
        if (Directory.Exists(Path.Combine(path, "nx_main"))) return path;
        throw new DirectoryNotFoundException($"找不到 MUMU 根目录或 MuMuNxMain.exe: {value}");
    }

    private static void WriteSessionHook(
        string hookPath,
        JsonObject profile,
        string sessionName,
        int targetPid,
        string targetPath,
        string module,
        string eventsPath,
        string status)
    {
        var hookDir = Path.GetDirectoryName(hookPath)!;
        var hook = new JsonObject
        {
            ["format"] = "my-hook.runtime.v1",
            ["tool"] = new JsonObject
            {
                ["name"] = "my-hook-tool",
                ["version"] = "0.1.0",
                ["mode"] = "runtime-identification"
            },
            ["profile"] = profile.DeepClone(),
            ["session"] = new JsonObject
            {
                ["id"] = sessionName,
                ["status"] = status,
                ["startedUtc"] = DateTime.UtcNow.ToString("O"),
                ["targetPid"] = targetPid,
                ["target"] = targetPath,
                ["injectedModule"] = module
            },
            ["protocol"] = new JsonObject
            {
                ["eventFile"] = RelativeTo(hookDir, eventsPath),
                ["eventFormat"] = "my-hook.runtime-event.v1",
                ["environment"] = new JsonObject
                {
                    ["MY_HOOK_PROFILE"] = StringValue(profile, "id"),
                    ["MY_HOOK_OUTPUT"] = RelativeTo(hookDir, Path.GetDirectoryName(eventsPath)!)
                }
            },
            ["records"] = new JsonArray(),
            ["diagnostics"] = new JsonArray()
        };
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));
    }

    private static int LaunchSuspendedAndInject(
        string executable,
        string arguments,
        string workingDirectory,
        string module,
        string profileId,
        string eventsPath,
        string hookPath)
    {
        var previous = new Dictionary<string, string?>
        {
            ["MY_HOOK_PROFILE"] = Environment.GetEnvironmentVariable("MY_HOOK_PROFILE"),
            ["MY_HOOK_OUTPUT"] = Environment.GetEnvironmentVariable("MY_HOOK_OUTPUT"),
            ["MY_HOOK_EVENTS"] = Environment.GetEnvironmentVariable("MY_HOOK_EVENTS"),
            ["MY_HOOK_FILE"] = Environment.GetEnvironmentVariable("MY_HOOK_FILE")
        };
        Environment.SetEnvironmentVariable("MY_HOOK_PROFILE", profileId);
        Environment.SetEnvironmentVariable("MY_HOOK_OUTPUT", Path.GetDirectoryName(eventsPath));
        Environment.SetEnvironmentVariable("MY_HOOK_EVENTS", eventsPath);
        Environment.SetEnvironmentVariable("MY_HOOK_FILE", hookPath);

        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var commandLine = new System.Text.StringBuilder('"' + executable + '"' + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments));
        try
        {
            if (!CreateProcess(
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateSuspended,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startup,
                    out var processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess");
            }

            try
            {
                InjectIntoHandle(processInfo.Process, module);
                if (ResumeThread(processInfo.Thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread");
                return processInfo.ProcessId;
            }
            catch
            {
                TerminateProcess(processInfo.Process, 1);
                throw;
            }
            finally
            {
                CloseHandle(processInfo.Thread);
                CloseHandle(processInfo.Process);
            }
        }
        finally
        {
            foreach (var pair in previous)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static void InjectIntoProcess(int processId, string module)
    {
        var process = OpenProcess(ProcessAccess, false, processId);
        if (process == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess({processId})");
        try { InjectIntoHandle(process, module); }
        finally { CloseHandle(process); }
    }

    private static void InjectIntoHandle(IntPtr process, string module)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(module + "\0");
        var remotePath = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)bytes.Length, AllocationType, PageReadWrite);
        if (remotePath == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx");
        try
        {
            if (!WriteProcessMemory(process, remotePath, bytes, (UIntPtr)bytes.Length, out var written) || written.ToUInt64() != (ulong)bytes.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory");
            var loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LoadLibraryW");
            var remoteThread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remotePath, 0, out _);
            if (remoteThread == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateRemoteThread");
            try
            {
                if (WaitForSingleObject(remoteThread, 10000) != WaitObject0)
                    throw new TimeoutException("等待远程 LoadLibraryW 超时");
                if (!GetExitCodeThread(remoteThread, out var moduleHandle) || moduleHandle == 0)
                    throw new InvalidOperationException("目标进程未能加载运行时桥接模块");
            }
            finally { CloseHandle(remoteThread); }
        }
        finally { VirtualFreeEx(process, remotePath, UIntPtr.Zero, Release); }
    }

    private static string? TryGetProcessPath(int processId)
    {
        try { return Process.GetProcessById(processId).MainModule?.FileName; }
        catch { return null; }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

    private static JsonObject BuildHook(
        JsonObject profile,
        string capturePath,
        JsonObject capture,
        string output,
        AsReports asInfo,
        JsonArray links)
    {
        var summary = capture["summary"]?.AsObject();
        var captureRelative = RelativeTo(output, capturePath);
        var renderDoc = new JsonObject
        {
            ["format"] = capture["format"]?.GetValue<string>(),
            ["manifest"] = "renderdoc/capture.json",
            ["sourceCapture"] = captureRelative,
            ["summary"] = summary?.DeepClone()
        };

        var asObject = new JsonObject
        {
            ["shaderReport"] = asInfo.ShaderReportRelative,
            ["materialBindings"] = asInfo.BindingsRelative,
            ["schema"] = asInfo.Schema,
            ["shaderCount"] = asInfo.ShaderCount,
            ["materialCount"] = asInfo.MaterialCount,
            ["shaderNames"] = new JsonArray(asInfo.ShaderNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray())
        };

        return new JsonObject
        {
            ["format"] = "my-hook.hsr-4.4-mumu.v1",
            ["tool"] = new JsonObject
            {
                ["name"] = "my-hook-tool",
                ["version"] = "0.1.0",
                ["alignmentPolicy"] = "explicit-only"
            },
            ["profile"] = profile.DeepClone(),
            ["source"] = new JsonObject
            {
                ["capture"] = Path.GetFileName(capturePath),
                ["extension"] = Path.GetExtension(capturePath),
                ["sha256"] = Sha256(capturePath)
            },
            ["renderDoc"] = renderDoc,
            ["as"] = asObject,
            ["links"] = links,
            ["unresolved"] = BuildUnresolved(capture, asInfo, links),
            ["artifacts"] = BuildArtifacts(capture)
        };
    }

    private static JsonArray BuildArtifacts(JsonObject capture)
    {
        var shaders = new JsonArray();
        if (capture["shaders"] is JsonArray shaderArray)
        {
            foreach (var node in shaderArray.OfType<JsonObject>())
            {
                shaders.Add(new JsonObject
                {
                    ["stage"] = StringValue(node, "stage"),
                    ["shaderHash"] = StringValue(node, "shaderHash"),
                    ["interfaceHash"] = StringValue(node, "interfaceHash"),
                    ["status"] = StringValue(node, "status"),
                    ["source"] = StringValue(node, "source"),
                    ["reflection"] = StringValue(node, "reflection"),
                    ["reconstructedHlsl"] = StringValue(node, "reconstructedHlsl")
                });
            }
        }
        return shaders;
    }

    private static JsonArray BuildUnresolved(JsonObject capture, AsReports asInfo, JsonArray links)
    {
        var result = new JsonArray();
        if (asInfo.ShaderCount > 0 && links.Count == 0)
        {
            result.Add(new JsonObject
            {
                ["kind"] = "shader-material-alignment",
                ["status"] = "needs_explicit_link",
                ["reason"] = "AS Shader/Material names are not present in the GPU capture; supply --link-map."
            });
        }

        if (capture["variants"] is JsonArray variants && variants.Count > 0)
        {
            result.Add(new JsonObject
            {
                ["kind"] = "variant-material-values",
                ["status"] = "not_inferred",
                ["reason"] = "A GPU variant does not by itself prove the original Unity keyword or material assignment."
            });
        }
        return result;
    }

    private static AsReports CopyAsReports(Dictionary<string, List<string>> options, string output)
    {
        var report = CopyOptional(options, "as-report", output, "as/shader-report.json");
        var bindings = CopyOptional(options, "as-bindings", output, "as/unity-material-bindings.json");
        var schema = "none";
        var shaderCount = 0;
        var materialCount = 0;
        var shaderNames = new List<string>();

        foreach (var path in new[] { report, bindings }.Where(x => x is not null))
        {
            var root = JsonNode.Parse(File.ReadAllText(path!))?.AsObject();
            if (root is null) continue;
            schema = StringValue(root, "schema") ?? schema;
            shaderCount = Math.Max(shaderCount, ArrayCount(root, "shaders"));
            materialCount = Math.Max(materialCount, ArrayCount(root, "materials"));
            if (root["shaders"] is JsonArray shaders)
            {
                shaderNames.AddRange(shaders.OfType<JsonObject>()
                    .Select(x => StringValue(x, "name"))
                    .Where(x => !string.IsNullOrWhiteSpace(x))!);
            }
        }

        return new AsReports(
            report is null ? null : RelativeTo(output, report),
            bindings is null ? null : RelativeTo(output, bindings),
            schema,
            shaderCount,
            materialCount,
            shaderNames.Distinct(StringComparer.Ordinal).OrderBy(x => x).ToList());
    }

    private static JsonArray ReadAndValidateLinks(string? path, JsonObject capture, AsReports asInfo)
    {
        if (path is null) return new JsonArray();
        var root = JsonNode.Parse(File.ReadAllText(FullPath(path)));
        var input = root as JsonArray ?? throw new InvalidDataException("link-map 必须是 JSON 数组");
        var validHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in capture["shaders"]?.AsArray().OfType<JsonObject>() ?? [])
            AddIfPresent(validHashes, node, "shaderHash");
        foreach (var node in capture["variants"]?.AsArray().OfType<JsonObject>() ?? [])
            AddIfPresent(validHashes, node, "variantHash");

        var result = new JsonArray();
        foreach (var node in input.OfType<JsonObject>())
        {
            var hash = StringValue(node, "shaderHash") ?? StringValue(node, "variantHash");
            var name = StringValue(node, "asShaderName");
            if (hash is null || !validHashes.Contains(hash))
                throw new InvalidDataException($"link-map 中的 capture hash 不存在: {hash}");
            if (name is null || !asInfo.ShaderNames.Contains(name, StringComparer.Ordinal))
                throw new InvalidDataException($"link-map 中的 AS Shader 名称不存在: {name}");
            result.Add(node.DeepClone());
        }
        return result;
    }

    private static string? CopyOptional(Dictionary<string, List<string>> options, string key, string output, string destination)
    {
        if (!options.TryGetValue(key, out var values)) return null;
        var source = FullPath(OneValue(values, key));
        if (!File.Exists(source)) throw new FileNotFoundException($"找不到 {key}", source);
        var target = Path.Combine(output, destination.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, true);
        return target;
    }

    private static void RunProcess(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 RenderDoc");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Console.Write(stdout.Result);
        var error = stderr.Result;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"RenderDoc 导出失败，退出码 {process.ExitCode}: {error.Trim()}");
        if (!string.IsNullOrWhiteSpace(error)) Console.Error.WriteLine(error.Trim());
    }

    private static JsonObject LoadProfile(string? path)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "profiles", "HSR-4.4-MUMU.json");
        path = FullPath(path);
        if (!File.Exists(path))
            return new JsonObject { ["id"] = "HSR-4.4-MUMU", ["captureExtensions"] = new JsonArray(".srrdc") };
        return JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("profile 不是 JSON 对象");
    }

    private static void ValidateCapture(string path, JsonObject profile)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 capture", path);
        var extension = Path.GetExtension(path);
        var allowed = profile["captureExtensions"]?.AsArray().Select(x => x?.GetValue<string>()) ?? [];
        if (!allowed.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"当前 Profile 不接受 {extension}，允许: {string.Join(", ", allowed)}");
    }

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(args[i]);
                continue;
            }
            var key = args[i][2..];
            var value = "true";
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];
            if (!result.TryGetValue(key, out var values)) result[key] = values = [];
            values.Add(value);
        }
        result["__positionals"] = positionals;
        return result;
    }

    private static void AddRepeated(Dictionary<string, List<string>> options, string key, List<string> args, string command)
    {
        if (!options.TryGetValue(key, out var values)) return;
        foreach (var value in values) { args.Add(command); args.Add(value); }
    }

    private static void AddValue(Dictionary<string, List<string>> options, string key, List<string> args, string command)
    {
        if (options.TryGetValue(key, out var values)) { args.Add(command); args.Add(OneValue(values, key)); }
    }

    private static void AddFlag(Dictionary<string, List<string>> options, string key, List<string> args, string command)
    {
        if (options.ContainsKey(key)) args.Add(command);
    }

    private static string RequiredValue(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values) ? OneValue(values, key) : throw new ArgumentException($"缺少 --{key}");

    private static string RequiredPath(Dictionary<string, List<string>> options, string key)
    {
        var path = FullPath(RequiredValue(options, key));
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到 --{key}", path);
        return path;
    }

    private static string OneValue(List<string> values, string key) => values.Count == 0 ? throw new ArgumentException($"--{key} 缺少值") : values[^1];
    private static string? GetValueOrDefault(this Dictionary<string, List<string>> options, string key) => options.TryGetValue(key, out var values) ? OneValue(values, key) : null;
    private static List<string> Positionals(this Dictionary<string, List<string>> options) => options.TryGetValue("__positionals", out var values) ? values : [];
    private static string FullPath(string value) => Path.GetFullPath(value);
    private static string RelativeTo(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static string? StringValue(JsonObject node, string key) => node[key]?.GetValue<string>();
    private static int ArrayCount(JsonObject node, string key) => node[key] is JsonArray array ? array.Count : 0;
    private static void AddIfPresent(HashSet<string> values, JsonObject node, string key) { var value = StringValue(node, key); if (value is not null) values.Add(value); }
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static void RecreateDirectory(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); Directory.CreateDirectory(path); }
    private static int Fail(string message) { Console.Error.WriteLine(message); PrintUsage(); return 2; }

    private static void PrintHookSummary(JsonObject hook)
    {
        var summary = hook["renderDoc"]?["summary"]?.AsObject();
        Console.WriteLine($"格式: {StringValue(hook, "format")}");
        if (summary is not null)
            Console.WriteLine($"捕获: {hook["source"]?["capture"]?.GetValue<string>()}, Shader: {summary["uniqueShaders"] ?? "?"}, Variant: {summary["uniqueVariants"] ?? "?"}, EID: {summary["events"] ?? "?"}");
        if (hook["session"] is JsonObject session)
            Console.WriteLine($"会话: {session["id"] ?? "?"}, 状态: {session["status"] ?? "?"}, PID: {session["targetPid"] ?? "?"}");
        Console.WriteLine($"AS Shader: {hook["as"]?["shaderCount"] ?? 0}, Material: {hook["as"]?["materialCount"] ?? 0}");
        Console.WriteLine($"运行时记录: {hook["records"]?.AsArray().Count ?? 0}, 显式关联: {hook["links"]?.AsArray().Count ?? 0}, 未解决: {hook["unresolved"]?.AsArray().Count ?? 0}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("my-hook-tool export <capture.srrdc> --renderdoc <renderdoccmd.exe> --output <dir> [options]");
        Console.WriteLine("  --event <eid>       限定事件，可重复");
        Console.WriteLine("  --reconstruct       请求 RenderDoc 重建 HLSL");
        Console.WriteLine("  --spirv-cross <exe> 指定 SPIRV-Cross");
        Console.WriteLine("  --export-resources  导出事件绑定的纹理和 buffer 快照");
        Console.WriteLine("  --as-report <json>  附带 AnimeStudio shader-report.json");
        Console.WriteLine("  --as-bindings <json>附带 AnimeStudio unity-material-bindings.json");
        Console.WriteLine("  --link-map <json>   仅接受显式 Shader 对齐");
        Console.WriteLine("my-hook-tool inspect <file.hook>");
        Console.WriteLine("my-hook-tool attach --target <exe> --module <bridge.dll> --output <dir> [options]");
        Console.WriteLine("  --target-pid <pid>  向已运行进程注入，而不是启动新进程");
        Console.WriteLine("  --arguments <text>  目标进程参数");
        Console.WriteLine("  --name <name>       会话目录和 .hook 文件名");
        Console.WriteLine("my-hook-tool mumu --mumu-root <dir> --module <bridge.dll> --output <dir> [options]");
        Console.WriteLine("  --vmindex <index>   MUMU 实例编号，默认 0");
        Console.WriteLine("my-hook-tool finalize <session.hook>");
    }

    private const uint CreateSuspended = 0x00000004;
    private const uint ProcessAccess = 0x0000142A;
    private const uint AllocationType = 0x00003000;
    private const uint Release = 0x00008000;
    private const uint PageReadWrite = 0x04;
    private const uint WaitObject0 = 0x00000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XChars;
        public int YChars;
        public int Fill;
        public int Flags;
        public short ShowWindow;
        public short ReservedSize;
        public IntPtr ReservedPointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        System.Text.StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed record AsReports(string? ShaderReportRelative, string? BindingsRelative, string Schema, int ShaderCount, int MaterialCount, List<string> ShaderNames);
}
