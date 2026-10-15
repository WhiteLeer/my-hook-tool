using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace MyHookTool;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
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
                "capture-session" => CaptureSessionCommand.Run(args[1..]),
                "trigger" => TriggerTarget(args[1..]),
                "enrich" => EnrichHook(args[1..]),
                "finalize" => FinalizeSession(args[1..]),
                "inspect" => Inspect(args[1..]),
                "profile-check" => ProfileCheck(args[1..]),
                "analyze" => Analyze(args[1..]),
                "typetree" => InspectTypeTree(args[1..]),
                "typetree-static" => StaticTypeTreeCommand.Run(args[1..]),
                "as-index" => AsIndexCommand.Run(args[1..]),
                "align" => AlignmentCommand.Run(args[1..]),
                "verify" => VerifyCommand.Run(args[1..]),
                _ => Fail($"未知命令: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex}");
            return 1;
        }
    }

    private static int ProfileCheck(string[] args)
    {
        var options = ParseOptions(args);
        if (options.Positionals().Count != 1)
            throw new ArgumentException("profile-check 需要一个 Profile 路径");

        var profilePath = FullPath(options.Positionals()[0]);
        var profile = LoadProfile(profilePath);
        var id = StringValue(profile, "id") ?? "<missing>";
        var version = StringValue(profile, "version") ?? "<missing>";
        var unity = StringValue(profile, "unity") ?? "<missing>";
        var nativeOffset = StringValue(profile, "nativeVtableOffset") ?? "<unset>";
        if (!string.Equals(StringValue(profile, "game"), "HSR", StringComparison.Ordinal)
            || !Version.TryParse(version, out _))
            throw new InvalidDataException(
                $"Profile 缺少有效的 HSR 版本: {id} / {version}");

        if (StringValue(profile, "runtimeSupport") == "discovery-only")
        {
            Console.WriteLine($"Profile: {id}; version: {version}; Unity: {unity}");
            Console.WriteLine("版本发现完成；当前版本的 native ABI / TypeTree 尚未验证，注入保持关闭。");
            return 2;
        }

        var baseline = profile["typeTreeBaseline"]?.AsObject()
            ?? throw new InvalidDataException("Profile 缺少 typeTreeBaseline");
        var baselinePath = ResolveProfilePath(baseline, "file")
            ?? throw new FileNotFoundException("找不到 Profile 的 TypeTree baseline");
        var expectedHash = StringValue(baseline, "sha256");
        var actualHash = Sha256(baselinePath);
        if (!string.IsNullOrWhiteSpace(expectedHash)
            && !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TypeTree baseline SHA-256 不匹配");

        Console.WriteLine($"Profile: {id}");
        Console.WriteLine($"Version: {version}; Unity: {unity}");
        Console.WriteLine($"Native vtable offset: {nativeOffset} (explicit native experiment only)");
        Console.WriteLine($"Baseline: {baselinePath}");
        Console.WriteLine($"Baseline SHA-256: {actualHash}");
        Console.WriteLine("安全检查通过；未启动 MuMu，未加载或调用 native ABI。");
        return 0;
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

    private static int Analyze(string[] args)
    {
        var options = ParseOptions(args);
        if (options.Positionals().Count != 1)
            throw new ArgumentException("analyze 需要一个 .hook 路径");

        var hookPath = FullPath(options.Positionals()[0]);
        var hook = JsonNode.Parse(File.ReadAllText(hookPath))?.AsObject()
            ?? throw new InvalidDataException(".hook 不是 JSON 对象");
        var records = hook["records"]?.AsArray();
        var recordSource = "hook.records";
        if (records is null || records.Count == 0)
        {
            // A live guest session writes a probe beside events.ndjson before
            // finalize commits the records into the .hook. Analyze that
            // evidence read-only, and expose the source so it cannot be
            // mistaken for a finalized hook payload.
            var fallbackDirectory = Path.GetDirectoryName(hookPath)!;
            var eventCandidates = new List<string>();
            if (hook["protocol"]?.AsObject()?["eventFile"]?.GetValue<string>() is { } eventFile)
                eventCandidates.Add(Path.Combine(
                    fallbackDirectory, eventFile.Replace('/', Path.DirectorySeparatorChar)));
            eventCandidates.Add(Path.Combine(fallbackDirectory, "runtime", ".runtime-events.probe"));

            records = new JsonArray();
            foreach (var candidate in eventCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(candidate) || new FileInfo(candidate).Length == 0)
                    continue;
                var candidateRecords = ReadEventRecords(candidate, ignoreIncompleteTail: true);
                if (candidateRecords.Count == 0)
                    continue;
                records = candidateRecords;
                recordSource = Path.GetFileName(candidate).Equals(
                    ".runtime-events.probe", StringComparison.Ordinal)
                    ? "runtime/.runtime-events.probe (live probe; not finalized)"
                    : Path.GetRelativePath(fallbackDirectory, candidate);
                break;
            }
        }
        if (records is null || records.Count == 0)
            throw new InvalidDataException(".hook 不包含 records，且同会话没有可读取的运行时事件；请先 finalize 或重新 attach");

        var objects = new HashSet<ulong>();
        var transfers = new HashSet<ulong>();
        var vtables = new HashSet<ulong>();
        var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var processCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var snapshotCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var snapshotSizes = new Dictionary<int, int>();
        var recordTypeIds = new HashSet<int>();
        var snapshotReferences = new JsonArray();
        var typeTreeObservations = new JsonArray();
        var serializedIdentityRecords = new JsonArray();
        var serializedIdentityProducerRecords = new JsonArray();
        var producerCallerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var serializedIdentityResolveRecords = new JsonArray();
        var serializedObjectResolveCandidateRecords = new JsonArray();
        var serializedObjectLoadBoundaryRecords = new JsonArray();
        var serializedObjectLoadBoundaryPPtrRecords = new JsonArray();
        var serializedObjectLoadToTransferSameObjectRecords = new JsonArray();
        var directPPtrToResolveChainRecords = new JsonArray();
        var activeTransferCallChainRecords = new JsonArray();
        var resolveCallerCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var serializedIdentityRawPatchRecords = new JsonArray();
        var serializedIdentityPostStoreRecords = new JsonArray();
        var serializedIdentityCallChainRecords = new JsonArray();
        var transferContextRecords = new JsonArray();
        var transferRecords = new JsonArray();
        var sameObjectTransferContextRecords = new JsonArray();
        var verifiedMaterialContextRelationCount = 0;
        var typeTreeConstructorRecords = new JsonArray();
        var keyWord0Values = new HashSet<uint>();
        var legacySerializedFileIndexes = new HashSet<uint>();
        var identityKeys = new HashSet<ulong>();
        var runtimeIdentityValues = new HashSet<uint>();
        var identityCallers = new HashSet<ulong>();
        var identityAtHookReturnReadableCount = 0;
        var identityAtHookReturnMatchCount = 0;
        var identityDelayedWritebackCount = 0;
        var identityDelayedMatchOneMsCount = 0;
        var identityDelayedMatchFiveMsCount = 0;
        var runtimeFieldEvidence = new JsonArray();
        var runtimeFieldEvidenceKeys = new HashSet<string>(StringComparer.Ordinal);
        var invalidSnapshots = 0;
        var drawContextCount = 0;
        var verifiedDrawContextCount = 0;
        var completeShaderIdentityCount = 0;
        var completeMaterialIdentityCount = 0;
        var sharedEventIdentityCount = 0;
        var completeSharedIdentityCount = 0;
        var resolveCandidateReturnVtableReadableCount = 0;
        var resolveCandidateReturnVtableObservedTypeCount = 0;

        foreach (var node in records)
        {
            if (node is not JsonObject record)
                continue;

            var kind = StringValue(record, "kind") ?? "<unknown>";
            kindCounts[kind] = kindCounts.GetValueOrDefault(kind) + 1;
            var processId = StringValue(record, "processId") ?? "<missing>";
            processCounts[processId] = processCounts.GetValueOrDefault(processId) + 1;
            AddPointer(record, "object", objects);
            AddPointer(record, "transfer", transfers);
            AddPointer(record, "vtable", vtables);

            if (string.Equals(kind, "unity.serialized-identity-map", StringComparison.Ordinal))
            {
                var identity = new JsonObject
                {
                    ["keyWord0"] = record["keyWord0"]?.DeepClone()
                        ?? record["serializedFileIndex"]?.DeepClone(),
                    ["key64"] = record["key64"]?.DeepClone(),
                    ["runtimeValue32"] = record["runtimeValue32"]?.DeepClone(),
                    ["recordValue32AtHookReturn"] = record["recordValue32AtHookReturn"]?.DeepClone(),
                    ["recordValueReadableAtHookReturn"] = record["recordValueReadableAtHookReturn"]?.DeepClone(),
                    ["returnMatchesRecordValueAtHookReturn"] = record["returnMatchesRecordValueAtHookReturn"]?.DeepClone(),
                    ["writebackTiming"] = record["writebackTiming"]?.DeepClone(),
                    ["caller"] = record["caller"]?.DeepClone(),
                    ["keyReadable"] = record["keyReadable"]?.DeepClone()
                };
                serializedIdentityRecords.Add(identity);
                if (TryGetUInt32(record, "keyWord0", out var keyWord0))
                    keyWord0Values.Add(keyWord0);
                else if (TryGetUInt32(record, "serializedFileIndex", out var legacyIndex))
                    legacySerializedFileIndexes.Add(legacyIndex);
                if (TryGetUInt64(record, "key64", out var key64))
                    identityKeys.Add(key64);
                if (TryGetUInt32(record, "runtimeValue32", out var runtimeValue32))
                    runtimeIdentityValues.Add(runtimeValue32);
                if (TryGetUInt64(record, "caller", out var caller))
                    identityCallers.Add(caller);
                if (record["recordValueReadableAtHookReturn"]?.GetValue<bool>() == true)
                {
                    identityAtHookReturnReadableCount++;
                    if (record["returnMatchesRecordValueAtHookReturn"]?.GetValue<bool>() == true)
                        identityAtHookReturnMatchCount++;
                }
            }

            if (string.Equals(kind, "unity.serialized-identity-writeback",
                StringComparison.Ordinal))
            {
                identityDelayedWritebackCount++;
                if (record["matchesAfterOneMs"]?.GetValue<bool>() == true)
                    identityDelayedMatchOneMsCount++;
                if (record["matchesAfterFiveMs"]?.GetValue<bool>() == true)
                    identityDelayedMatchFiveMsCount++;
            }

            if (string.Equals(kind, "unity.serialized-identity-producer-call",
                StringComparison.Ordinal))
            {
                var callerPc = StringValue(record, "callerPc") ?? "<missing>";
                producerCallerCounts[callerPc] =
                    producerCallerCounts.GetValueOrDefault(callerPc) + 1;
                serializedIdentityProducerRecords.Add(new JsonObject
                {
                    ["callerPc"] = record["callerPc"]?.DeepClone(),
                    ["manager"] = record["manager"]?.DeepClone(),
                    ["input"] = record["input"]?.DeepClone(),
                    ["outputA"] = record["outputA"]?.DeepClone(),
                    ["outputB"] = record["outputB"]?.DeepClone(),
                    ["inputSnapshotHex"] = record["inputSnapshotHex"]?.DeepClone(),
                    ["outputASnapshotHex"] = record["outputASnapshotHex"]?.DeepClone(),
                    ["outputBSnapshotHex"] = record["outputBSnapshotHex"]?.DeepClone()
                });
            }

            if (string.Equals(kind, "unity.serialized-identity-resolve-call",
                StringComparison.Ordinal))
            {
                var callerPc = StringValue(record, "callerPc") ?? "<missing>";
                resolveCallerCounts[callerPc] =
                    resolveCallerCounts.GetValueOrDefault(callerPc) + 1;
                serializedIdentityResolveRecords.Add(new JsonObject
                {
                    ["callerPc"] = record["callerPc"]?.DeepClone(),
                    ["manager"] = record["manager"]?.DeepClone(),
                    ["key32"] = record["key32"]?.DeepClone(),
                    ["input"] = record["input"]?.DeepClone(),
                    ["alternateInput"] = record["alternateInput"]?.DeepClone(),
                    ["flagsA"] = record["flagsA"]?.DeepClone(),
                    ["flagsB"] = record["flagsB"]?.DeepClone(),
                    ["returnValue"] = record["returnValue"]?.DeepClone(),
                    ["inputSnapshotHex"] = record["inputSnapshotHex"]?.DeepClone(),
                    ["alternateInputSnapshotHex"] = record["alternateInputSnapshotHex"]?.DeepClone(),
                    ["returnSnapshotHex"] = record["returnSnapshotHex"]?.DeepClone(),
                    ["semantics"] = record["semantics"]?.DeepClone()
                });
            }

            if (string.Equals(kind, "unity.serialized-object-resolve-candidate",
                StringComparison.Ordinal))
            {
                serializedObjectResolveCandidateRecords.Add(record.DeepClone());
                if (record["returnVtableReadable"]?.GetValue<bool>() == true)
                {
                    resolveCandidateReturnVtableReadableCount++;
                    if (record["returnVtableObservedType"] is JsonObject)
                        resolveCandidateReturnVtableObservedTypeCount++;
                }
                if (record["directPPtrToResolveCallChain"] is JsonObject chain)
                    directPPtrToResolveChainRecords.Add(chain.DeepClone());
                if (record["activeTransferCallChain"] is JsonObject transferChain)
                    activeTransferCallChainRecords.Add(transferChain.DeepClone());
            }

            if (string.Equals(kind, "unity.serialized-object-load-boundary",
                StringComparison.Ordinal))
            {
                serializedObjectLoadBoundaryRecords.Add(record.DeepClone());
            }
            if (record["activeSerializedObjectLoadBoundary"] is JsonObject loadBoundary)
                serializedObjectLoadBoundaryPPtrRecords.Add(new JsonObject
                {
                    ["eventSequence"] = record["eventSequence"]?.DeepClone(),
                    ["threadId"] = record["threadId"]?.DeepClone(),
                    ["pptr"] = record.DeepClone(),
                    ["loadBoundary"] = loadBoundary.DeepClone()
                });
            if (string.Equals(kind, "unity.serialized-object-load-to-transfer-same-object",
                StringComparison.Ordinal))
                serializedObjectLoadToTransferSameObjectRecords.Add(record.DeepClone());

            if (string.Equals(kind, "unity.serialized-identity-raw-patch",
                StringComparison.Ordinal))
            {
                // Preserve the guest-side observation verbatim. The mailbox
                // fields are deliberately not renamed to PathID/InstanceID.
                serializedIdentityRawPatchRecords.Add(new JsonObject
                {
                    ["status"] = record["status"]?.DeepClone(),
                    ["manager"] = record["manager"]?.DeepClone(),
                    ["key"] = record["key"]?.DeepClone(),
                    ["returnValue32"] = record["returnValue32"]?.DeepClone(),
                    ["caller"] = record["caller"]?.DeepClone(),
                    ["keyReadable"] = record["keyReadable"]?.DeepClone(),
                    ["keyBytesHex"] = record["keyBytesHex"]?.DeepClone()
                });
            }

            if (string.Equals(kind, "unity.serialized-identity-post-store",
                StringComparison.Ordinal))
            {
                // The caller patch records the exact x28 record used as x1 at
                // the identity callsite. Keep this evidence separate from
                // asset identity; the record fields remain opaque.
                serializedIdentityPostStoreRecords.Add(new JsonObject
                {
                    ["callPc"] = record["callPc"]?.DeepClone(),
                    ["recordObject"] = record["recordObject"]?.DeepClone(),
                    ["postStoreValue"] = record["postStoreValue"]?.DeepClone(),
                    ["destinationAddress"] = record["destinationAddress"]?.DeepClone(),
                    ["recordBytesHex"] = record["recordBytesHex"]?.DeepClone(),
                    ["destinationSnapshotHex"] = record["destinationSnapshotHex"]?.DeepClone()
                });
            }

            if (string.Equals(kind, "unity.transfer-context-call",
                StringComparison.Ordinal))
            {
                // Keep this as a same-call-chain evidence record. The fields
                // are deliberately not renamed to Unity business identifiers.
                transferContextRecords.Add(new JsonObject
                {
                    ["context"] = record["context"]?.DeepClone(),
                    ["contextVtable"] = record["contextVtable"]?.DeepClone(),
                    ["typeName"] = record["typeName"]?.DeepClone(),
                    ["typeId"] = record["typeId"]?.DeepClone(),
                    ["caller"] = record["caller"]?.DeepClone(),
                    ["arguments"] = record["arguments"]?.DeepClone(),
                    ["result"] = record["result"]?.DeepClone(),
                    ["activeTransfer"] = record["activeTransfer"]?.DeepClone(),
                    ["callRelation"] = record["callRelation"]?.DeepClone(),
                    ["verifiedRelations"] = record["verifiedRelations"]?.DeepClone(),
                    ["contextDataProbe"] = record["contextDataProbe"]?.DeepClone(),
                    ["contextDataSnapshotHex"] = record["contextDataSnapshotHex"]?.DeepClone()
                });
                if (string.Equals(StringValue(record, "typeName"), "Material",
                        StringComparison.Ordinal)
                    && record["activeTransfer"] is JsonObject
                    && record["verifiedRelations"] is JsonObject relations
                    && relations["argument0MatchesObjectWord28Low32"]?.GetValue<bool>() == true
                    && relations["argument1MatchesTransferWord58Low32"]?.GetValue<bool>() == true)
                {
                    verifiedMaterialContextRelationCount++;
                }
            }

            if (string.Equals(kind, "unity.transfer", StringComparison.Ordinal))
            {
                // Preserve the exact runtime-object relation. This is not an
                // asset identity: object addresses are never renamed to
                // PathID, InstanceID, sourceFile, or RenderDoc ResourceId.
                transferRecords.Add(new JsonObject
                {
                    ["object"] = record["object"]?.DeepClone(),
                    ["transfer"] = record["transfer"]?.DeepClone(),
                    ["vtable"] = record["vtable"]?.DeepClone(),
                    ["typeId"] = record["typeId"]?.DeepClone(),
                    ["typeName"] = record["typeName"]?.DeepClone(),
                    ["objectSize"] = record["objectSize"]?.DeepClone(),
                    ["objectSnapshotHex"] = record["objectSnapshotHex"]?.DeepClone(),
                    ["captureSessionId"] = record["captureSessionId"]?.DeepClone()
                });
            }

            if (string.Equals(kind, "unity.transfer-layout", StringComparison.Ordinal))
                CollectRuntimeFieldEvidence(record, runtimeFieldEvidence, runtimeFieldEvidenceKeys);

            if (string.Equals(kind, "unity.typetree-constructor-call", StringComparison.Ordinal))
            {
                typeTreeConstructorRecords.Add(new JsonObject
                {
                    ["status"] = record["status"]?.DeepClone(),
                    ["object"] = record["object"]?.DeepClone(),
                    ["size"] = record["size"]?.DeepClone(),
                    ["flags"] = record["flags"]?.DeepClone(),
                    ["argument3"] = record["argument3"]?.DeepClone(),
                    ["argument4"] = record["argument4"]?.DeepClone(),
                    ["typeNamePointer"] = record["typeName"]?.DeepClone(),
                    ["typeNameString"] = record["typeNameString"]?.DeepClone(),
                    ["initialize"] = record["initialize"]?.DeepClone(),
                    ["returnValue"] = record["returnValue"]?.DeepClone()
                });
            }

            if (string.Equals(kind, "unity.draw-context", StringComparison.Ordinal))
            {
                drawContextCount++;
                if (string.Equals(StringValue(record, "status"), "verified", StringComparison.Ordinal))
                    verifiedDrawContextCount++;
                if (!string.IsNullOrWhiteSpace(StringValue(record, "shaderSourceFile"))
                    && !string.IsNullOrWhiteSpace(StringValue(record, "shaderPathId")))
                    completeShaderIdentityCount++;
                if (!string.IsNullOrWhiteSpace(StringValue(record, "materialSourceFile"))
                    && !string.IsNullOrWhiteSpace(StringValue(record, "materialPathId")))
                    completeMaterialIdentityCount++;
                if (!string.IsNullOrWhiteSpace(StringValue(record, "renderDocEventId")))
                    sharedEventIdentityCount++;
                if (string.Equals(StringValue(record, "status"), "verified", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(StringValue(record, "shaderSourceFile"))
                    && !string.IsNullOrWhiteSpace(StringValue(record, "shaderPathId"))
                    && !string.IsNullOrWhiteSpace(StringValue(record, "materialSourceFile"))
                    && !string.IsNullOrWhiteSpace(StringValue(record, "materialPathId"))
                    && !string.IsNullOrWhiteSpace(StringValue(record, "renderDocEventId")))
                    completeSharedIdentityCount++;
            }

            if (string.Equals(kind, "unity.typetree", StringComparison.Ordinal))
            {
                typeTreeObservations.Add(new JsonObject
                {
                    ["typeName"] = StringValue(record, "typeName") ?? "<unknown>",
                    ["typeId"] = record["typeId"]?.DeepClone(),
                    ["nodeCount"] = record["nodeCount"]?.DeepClone(),
                    ["stringBytes"] = record["stringBytes"]?.DeepClone(),
                    ["layout"] = record["layout"]?.DeepClone(),
                    ["transferFieldOffset"] = record["transferFieldOffset"]?.DeepClone()
                });
            }

            var typeName = StringValue(record, "typeName") ?? "<unknown>";
            typeCounts[typeName] = typeCounts.GetValueOrDefault(typeName) + 1;
            if (TryGetInt(record, "typeId", out var typeId) && typeId >= 0)
                recordTypeIds.Add(typeId);

            var snapshot = StringValue(record, "objectSnapshotHex");
            if (string.IsNullOrWhiteSpace(snapshot))
                continue;

            if (!TryDecodeHex(snapshot, out var bytes))
            {
                invalidSnapshots++;
                continue;
            }

            snapshotCounts[typeName] = snapshotCounts.GetValueOrDefault(typeName) + 1;
            snapshotSizes[bytes.Length] = snapshotSizes.GetValueOrDefault(bytes.Length) + 1;
            var references = FindSnapshotReferences(bytes, objects, transfers, vtables);
            if (references.Count == 0)
                continue;

            var referenceRecord = new JsonObject
            {
                ["typeName"] = typeName,
                ["typeId"] = record["typeId"]?.DeepClone(),
                ["object"] = StringValue(record, "object"),
                ["references"] = references
            };
            snapshotReferences.Add(referenceRecord);
        }

        foreach (var postNode in serializedIdentityPostStoreRecords.OfType<JsonObject>())
        {
            var recordObject = StringValue(postNode, "recordObject");
            if (string.IsNullOrWhiteSpace(recordObject)
                || !TryGetUInt32(postNode, "postStoreValue", out var postValue))
                continue;

            foreach (var rawNode in serializedIdentityRawPatchRecords.OfType<JsonObject>())
            {
                var rawKey = StringValue(rawNode, "key");
                if (string.IsNullOrWhiteSpace(rawKey)
                    || !string.Equals(recordObject, rawKey, StringComparison.Ordinal)
                    || !TryGetUInt32(rawNode, "returnValue32", out var rawValue)
                    || rawValue != postValue)
                    continue;

                serializedIdentityCallChainRecords.Add(new JsonObject
                {
                    ["status"] = "verified_same_runtime_record",
                    ["recordObject"] = recordObject,
                    ["rawKey"] = rawKey,
                    ["callPc"] = postNode["callPc"]?.DeepClone(),
                    ["postStoreValue"] = postNode["postStoreValue"]?.DeepClone(),
                    ["rawReturnValue32"] = rawNode["returnValue32"]?.DeepClone(),
                    ["recordBytesHex"] = postNode["recordBytesHex"]?.DeepClone(),
                    ["rawKeyBytesHex"] = rawNode["keyBytesHex"]?.DeepClone(),
                    ["proof"] = "exact x28 record pointer equals raw identity key pointer and the 32-bit lookup result equals the caller writeback value; no asset identity is inferred"
                });
            }
        }

        var transferByObject = transferRecords
            .OfType<JsonObject>()
            .Where(record => !string.IsNullOrWhiteSpace(StringValue(record, "object")))
            .GroupBy(record => StringValue(record, "object")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var context in transferContextRecords.OfType<JsonObject>())
        {
            if (context["activeTransfer"] is not JsonObject active
                || string.IsNullOrWhiteSpace(StringValue(active, "object")))
                continue;

            var objectKey = StringValue(active, "object")!;
            if (!transferByObject.TryGetValue(objectKey, out var matches))
                continue;

            foreach (var transfer in matches)
            {
                sameObjectTransferContextRecords.Add(new JsonObject
                {
                    ["status"] = "observed_same_runtime_object",
                    ["identityStatus"] = "unresolved",
                    ["object"] = objectKey,
                    ["typeId"] = transfer["typeId"]?.DeepClone(),
                    ["typeName"] = transfer["typeName"]?.DeepClone(),
                    ["transfer"] = transfer["transfer"]?.DeepClone(),
                    ["context"] = context["context"]?.DeepClone(),
                    ["verifiedRelations"] = context["verifiedRelations"]?.DeepClone(),
                    ["transferRecord"] = transfer.DeepClone(),
                    ["contextRecord"] = context.DeepClone(),
                    ["proof"] = "the exact observed runtime object pointer is present in both unity.transfer and its active transfer context; no asset or GPU identity is inferred"
                });
            }
        }

        // Audit Material/Shader cross-type evidence separately from the
        // per-object Transfer/context relation above. A Material and a
        // Shader sharing a thread, context shape, or timing is not an
        // identity bridge; only an exact shared runtime value is counted.
        var materialTransferRecords = transferRecords
            .OfType<JsonObject>()
            .Where(record => string.Equals(
                StringValue(record, "typeName"), "Material", StringComparison.Ordinal))
            .ToArray();
        var shaderTransferRecords = transferRecords
            .OfType<JsonObject>()
            .Where(record => string.Equals(
                StringValue(record, "typeName"), "Shader", StringComparison.Ordinal))
            .ToArray();
        var materialObjectKeys = PointerKeys(materialTransferRecords, "object");
        var shaderObjectKeys = PointerKeys(shaderTransferRecords, "object");
        var materialTransferKeys = PointerKeys(materialTransferRecords, "transfer");
        var shaderTransferKeys = PointerKeys(shaderTransferRecords, "transfer");
        var materialContextObjectKeys = ContextActiveObjectKeys(
            transferContextRecords.OfType<JsonObject>(), "Material");
        var shaderContextObjectKeys = ContextActiveObjectKeys(
            transferContextRecords.OfType<JsonObject>(), "Shader");
        var materialShaderObjectIntersectionCount = materialObjectKeys.Intersect(shaderObjectKeys).Count();
        var materialShaderTransferIntersectionCount = materialTransferKeys.Intersect(shaderTransferKeys).Count();
        var materialShaderContextIntersectionCount = materialContextObjectKeys.Intersect(shaderContextObjectKeys).Count();
        var materialShaderSameCallChainCount = 0;
        var materialShaderBindingStatus = materialShaderObjectIntersectionCount > 0
            || materialShaderTransferIntersectionCount > 0
            || materialShaderContextIntersectionCount > 0
            || materialShaderSameCallChainCount > 0
            ? "observed_unresolved"
            : "not_observed";
        var materialShaderBindingAudit = new JsonObject
        {
            ["status"] = materialShaderBindingStatus,
            ["materialObjectCount"] = materialObjectKeys.Count,
            ["shaderObjectCount"] = shaderObjectKeys.Count,
            ["materialTransferCount"] = materialTransferKeys.Count,
            ["shaderTransferCount"] = shaderTransferKeys.Count,
            ["materialContextObjectCount"] = materialContextObjectKeys.Count,
            ["shaderContextObjectCount"] = shaderContextObjectKeys.Count,
            ["materialShaderPointerIntersectionCount"] = materialShaderObjectIntersectionCount,
            ["materialShaderTransferPointerIntersectionCount"] = materialShaderTransferIntersectionCount,
            ["materialShaderContextPointerIntersectionCount"] = materialShaderContextIntersectionCount,
            ["materialShaderSameCallChainCount"] = materialShaderSameCallChainCount,
            ["proof"] = "Only exact cross-type runtime values are counted; thread, order, timing, address proximity, ResourceId and opaque fields are not identity evidence",
            ["identityBridgeStatus"] = "not_observed"
        };

        var analysis = new JsonObject
        {
            ["schema"] = "my-hook.analysis.v1",
            ["source"] = new JsonObject
            {
                ["file"] = Path.GetFileName(hookPath),
                ["format"] = StringValue(hook, "format"),
                ["profile"] = StringValue(hook["profile"]?.AsObject() ?? [], "id"),
                ["recordSource"] = recordSource
            },
            ["summary"] = new JsonObject
            {
                ["records"] = records.Count,
                ["kindCounts"] = ToCountArray(kindCounts, "kind"),
                ["processCounts"] = ToCountArray(processCounts, "processId"),
                ["distinctObjects"] = objects.Count,
                ["distinctTransfers"] = transfers.Count,
                ["distinctVtables"] = vtables.Count,
                ["recordsWithSnapshots"] = snapshotCounts.Values.Sum(),
                ["invalidSnapshots"] = invalidSnapshots,
                ["snapshotsWithKnownReferences"] = snapshotReferences.Count
                , ["serializedIdentityProducerCallCount"] = serializedIdentityProducerRecords.Count
                , ["serializedIdentityProducerCallerCounts"] = ToCountArray(
                    producerCallerCounts, "callerPc")
                , ["serializedIdentityResolveCallCount"] = serializedIdentityResolveRecords.Count
                , ["serializedIdentityResolveCallerCounts"] = ToCountArray(
                    resolveCallerCounts, "callerPc")
                , ["serializedObjectResolveCandidateCallCount"] =
                    serializedObjectResolveCandidateRecords.Count
                , ["serializedObjectLoadBoundaryCallCount"] =
                    serializedObjectLoadBoundaryRecords.Count
                , ["serializedObjectLoadBoundaryPPtrChainCount"] =
                    serializedObjectLoadBoundaryPPtrRecords.Count
                , ["serializedObjectLoadToTransferSameObjectCount"] =
                    serializedObjectLoadToTransferSameObjectRecords.Count
                , ["directPPtrToResolveChainCount"] =
                    directPPtrToResolveChainRecords.Count
                , ["activeTransferCallChainCount"] =
                    activeTransferCallChainRecords.Count
                , ["serializedIdentityRawPatchCount"] = serializedIdentityRawPatchRecords.Count
                , ["serializedIdentityPostStoreCount"] = serializedIdentityPostStoreRecords.Count
                , ["serializedIdentityCallChainCount"] = serializedIdentityCallChainRecords.Count
                , ["transferContextCallCount"] = transferContextRecords.Count
                , ["verifiedMaterialContextRelationCount"] = verifiedMaterialContextRelationCount
                , ["sameObjectTransferContextRelationCount"] = sameObjectTransferContextRecords.Count
                , ["materialShaderBindingStatus"] = materialShaderBindingStatus
                , ["materialShaderPointerIntersectionCount"] = materialShaderObjectIntersectionCount
                , ["materialShaderTransferPointerIntersectionCount"] = materialShaderTransferIntersectionCount
                , ["materialShaderContextPointerIntersectionCount"] = materialShaderContextIntersectionCount
                , ["materialShaderSameCallChainCount"] = materialShaderSameCallChainCount
            },
            ["typeCounts"] = ToCountArray(typeCounts, "typeName"),
            ["typeTreeObservations"] = typeTreeObservations,
            ["snapshotCounts"] = ToCountArray(snapshotCounts, "typeName"),
            ["snapshotSizes"] = ToCountArray(snapshotSizes, "bytes"),
            ["snapshotReferences"] = snapshotReferences,
            ["transferEvidence"] = new JsonObject
            {
                ["status"] = transferRecords.Count == 0 ? "not_observed" : "observed",
                ["recordCount"] = transferRecords.Count,
                ["semantics"] = "Exact unity.transfer records; object pointers and snapshots remain runtime evidence, not serialized asset identities",
                ["records"] = transferRecords
            },
            ["serializedIdentity"] = new JsonObject
            {
                ["status"] = serializedIdentityRecords.Count == 0
                    ? "not_observed"
                    : identityDelayedWritebackCount > 0
                        && (identityDelayedMatchOneMsCount > 0
                            || identityDelayedMatchFiveMsCount > 0)
                        ? "writeback_verified_identity_unresolved"
                        : "observed_unresolved",
                ["recordCount"] = serializedIdentityRecords.Count,
                ["distinctKeyWord0"] = keyWord0Values.Count,
                ["legacySerializedFileIndexCount"] = legacySerializedFileIndexes.Count,
                ["distinctKey64"] = identityKeys.Count,
                ["distinctRuntimeValue32"] = runtimeIdentityValues.Count,
                ["distinctCallers"] = identityCallers.Count,
                ["recordReadableAtHookReturnCount"] = identityAtHookReturnReadableCount,
                ["recordMatchesAtHookReturnCount"] = identityAtHookReturnMatchCount,
                ["delayedWritebackProbeCount"] = identityDelayedWritebackCount,
                ["delayedMatchAfterOneMsCount"] = identityDelayedMatchOneMsCount,
                ["delayedMatchAfterFiveMsCount"] = identityDelayedMatchFiveMsCount,
                ["writebackEvidence"] = new JsonObject
                {
                    ["status"] = identityDelayedMatchOneMsCount > 0
                        || identityDelayedMatchFiveMsCount > 0
                        ? "verified_same_record_delayed_read"
                        : identityDelayedWritebackCount > 0
                            ? "observed_no_match"
                            : "not_observed",
                    ["proof"] = "the exact identity record address was read after the callee returned; no address, order, name or ResourceId inference",
                    ["identityStillUnresolved"] = true
                },
                ["records"] = serializedIdentityRecords,
                ["identityKeyFormat"] = "keyWord0 + key64 (unresolved)",
                ["notPathIdEvidence"] = "keyWord0 and key64 are raw key fields; neither is renamed to PathID without a verified same-version serialized object table"
            },
            ["serializedIdentityProducer"] = new JsonObject
            {
                ["status"] = serializedIdentityProducerRecords.Count == 0
                    ? "not_observed"
                    : "observed_opaque_parameters",
                ["recordCount"] = serializedIdentityProducerRecords.Count,
                ["callerCounts"] = ToCountArray(producerCallerCounts, "callerPc"),
                ["semantics"] = "Bounded post-call parameter snapshots; pointer/count-like values remain opaque and are not interpreted as PathID or object identity",
                ["records"] = serializedIdentityProducerRecords
            },
            ["serializedIdentityResolve"] = new JsonObject
            {
                ["status"] = serializedIdentityResolveRecords.Count == 0
                    ? "not_observed"
                    : "observed_opaque_parameters",
                ["recordCount"] = serializedIdentityResolveRecords.Count,
                ["callerCounts"] = ToCountArray(resolveCallerCounts, "callerPc"),
                ["semantics"] = "ABI-checked bounded arguments and return snapshot; no field is named PathID, InstanceID or asset identity",
                ["records"] = serializedIdentityResolveRecords
            },
            ["serializedObjectResolveCandidate"] = new JsonObject
            {
                ["status"] = serializedObjectResolveCandidateRecords.Count == 0
                    ? "not_observed"
                    : directPPtrToResolveChainRecords.Count > 0
                        ? "observed_with_direct_PPtr_value_handoff"
                        : "observed_opaque_parameters",
                ["recordCount"] = serializedObjectResolveCandidateRecords.Count,
                ["directPPtrToResolveChainCount"] = directPPtrToResolveChainRecords.Count,
                ["returnVtableReadableCount"] = resolveCandidateReturnVtableReadableCount,
                ["returnVtableObservedTypeCount"] = resolveCandidateReturnVtableObservedTypeCount,
                ["semantics"] = "Opaque resolver arguments and return; direct chain records prove only a same-thread value handoff under version-matched static callsite conditions",
                ["identityStatus"] = "unresolved",
                ["records"] = serializedObjectResolveCandidateRecords,
                ["directChainRecords"] = directPPtrToResolveChainRecords
                , ["activeTransferCallChainCount"] = activeTransferCallChainRecords.Count
                , ["activeTransferCallChainRecords"] = activeTransferCallChainRecords
            },
            ["serializedObjectLoadBoundary"] = new JsonObject
            {
                ["status"] = serializedObjectLoadBoundaryRecords.Count == 0
                    ? "not_observed"
                    : "observed_opaque_arguments_and_return",
                ["recordCount"] = serializedObjectLoadBoundaryRecords.Count,
                ["nestedPPtrChainCount"] = serializedObjectLoadBoundaryPPtrRecords.Count,
                ["identityStatus"] = "unresolved",
                ["semantics"] = "Version-checked entry observer; nested PPtr records prove call-chain nesting only. keyOpaque64 and returned pointers are not renamed to PathID, sourceFile, Material, Shader or EID.",
                ["records"] = serializedObjectLoadBoundaryRecords,
                ["nestedPPtrRecords"] = serializedObjectLoadBoundaryPPtrRecords
                , ["sameObjectTransferCount"] = serializedObjectLoadToTransferSameObjectRecords.Count
                , ["sameObjectTransferRecords"] = serializedObjectLoadToTransferSameObjectRecords
            },
            ["serializedIdentityRawPatch"] = new JsonObject
            {
                ["status"] = serializedIdentityRawPatchRecords.Count == 0
                    ? "not_observed"
                    : "observed_opaque_guest_mailbox",
                ["recordCount"] = serializedIdentityRawPatchRecords.Count,
                ["semantics"] = "ARM64 guest raw-patch mailbox observation; key and return fields remain opaque and are not interpreted as PathID, InstanceID or asset identity",
                ["records"] = serializedIdentityRawPatchRecords
            },
            ["serializedIdentityCallChainEvidence"] = new JsonObject
            {
                ["status"] = serializedIdentityCallChainRecords.Count == 0
                    ? "not_observed"
                    : "verified_same_runtime_record_identity_unresolved",
                ["recordCount"] = serializedIdentityCallChainRecords.Count,
                ["semantics"] = "Exact same runtime record evidence across the caller post-store and guest identity lookup; this does not establish sourceFile, PathID, InstanceID, Material/Shader asset identity, or RenderDoc EID",
                ["records"] = serializedIdentityCallChainRecords
            },
            ["transferContextEvidence"] = new JsonObject
            {
                ["status"] = transferContextRecords.Count == 0
                    ? "not_observed"
                    : verifiedMaterialContextRelationCount > 0
                        ? "verified_same_transfer_relations"
                        : "observed_without_verified_relation",
                ["recordCount"] = transferContextRecords.Count,
                ["verifiedMaterialContextRelationCount"] = verifiedMaterialContextRelationCount,
                ["semantics"] = "Same-call-chain Transfer context evidence; opaque fields are not renamed to PathID or InstanceID",
                ["records"] = transferContextRecords
            },
            ["sameObjectTransferContextEvidence"] = new JsonObject
            {
                ["status"] = sameObjectTransferContextRecords.Count == 0
                    ? "not_observed"
                    : "observed_same_runtime_object_identity_unresolved",
                ["recordCount"] = sameObjectTransferContextRecords.Count,
                ["semantics"] = "The exact observed runtime object pointer appears in both unity.transfer and active transfer context; this does not establish sourceFile, PathID, Material/Shader asset identity, or RenderDoc EID",
                ["records"] = sameObjectTransferContextRecords
            },
            ["materialShaderBindingAudit"] = materialShaderBindingAudit,
            ["runtimeFieldEvidence"] = new JsonObject
            {
                ["status"] = runtimeFieldEvidence.Count == 0
                    ? "not_observed"
                    : "partial_observed",
                ["recordCount"] = runtimeFieldEvidence.Count,
                ["source"] = "unity.transfer-layout typeTreeObjectReferencedBuffers",
                ["semantics"] = "Observed printable strings from bounded guest buffers; not a complete TypeTree and not assigned field offsets",
                ["schemaEvidence"] = "runtime TypeTree node and string metadata only",
                ["liveValueEvidence"] = "not_observed",
                ["liveValueBlocker"] = "No same-call evidence currently connects a decoded field value to the active serialized object, sourceFile#PathID, or Material/Shader identity",
                ["fields"] = runtimeFieldEvidence
            },
            ["typeTreeConstructorEvidence"] = new JsonObject
            {
                ["status"] = typeTreeConstructorRecords.Count == 0
                    ? "not_observed"
                    : "observed_opaque_arguments",
                ["recordCount"] = typeTreeConstructorRecords.Count,
                ["semantics"] = "Observed constructor arguments after an ABI-checked hook; pointers remain opaque until the same-version parameter layout is proven",
                ["records"] = typeTreeConstructorRecords
            },
            ["identityBridge"] = new JsonObject
            {
                ["status"] = drawContextCount == 0
                    ? "not_observed"
                    : verifiedDrawContextCount == drawContextCount
                        && completeSharedIdentityCount > 0
                        ? "verified"
                        : "partial",
                ["drawContextCount"] = drawContextCount,
                ["verifiedDrawContextCount"] = verifiedDrawContextCount,
                ["completeShaderIdentityCount"] = completeShaderIdentityCount,
                ["completeMaterialIdentityCount"] = completeMaterialIdentityCount,
                ["sharedRenderDocEventIdCount"] = sharedEventIdentityCount,
                ["completeSharedIdentityCount"] = completeSharedIdentityCount,
                ["identityKeyFormat"] = "sourceFile#pathId",
                ["requiredEvidence"] = "verified unity.draw-context records containing sourceFile+PathID for Shader and Material, plus a shared RenderDoc event identity"
            },
            ["limitations"] = new JsonArray(
                "objectSnapshotHex is a bounded native-memory sample, not a decoded Unity object",
                "references are reported only when an 8-byte value exactly matches an observed object, transfer, or vtable address",
                "unknown values are intentionally not interpreted as paths, strings, hashes, or material fields")
        };

        var outputPath = options.GetValueOrDefault("output") is { } outputValue
            ? FullPath(outputValue)
            : Path.Combine(Path.GetDirectoryName(hookPath)!,
                Path.GetFileNameWithoutExtension(hookPath) + ".analysis.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var hookDirectory = Path.GetDirectoryName(hookPath)!;
        var catalogIds = ReadCatalogTypeIds(hookDirectory, hook["typetree"]?.AsObject());
        var missingTypeIds = catalogIds is null
            ? recordTypeIds.OrderBy(value => value).ToArray()
            : recordTypeIds.Where(value => !catalogIds.Contains(value)).OrderBy(value => value).ToArray();
        analysis["integrity"] = BuildIntegrityReport(hookDirectory, hook, records.Count, missingTypeIds);
        var decodedTypeTree = DecodeAttachedTypeTree(
            hookDirectory, hook, options.GetValueOrDefault("typetree"));
        var decodedTypeTreeStatus = StringValue(decodedTypeTree, "status");
        var runtimeCatalogStatus = catalogIds is not null
            && decodedTypeTreeStatus == "decoded"
            && Int64Value(decodedTypeTree, "totalNodes") > 0
            ? "verified_fields"
            : catalogIds is null ? "not_available" : "verified_ids_only";
        var runtimeProvenance = hook["typetree"]?.AsObject()?["provenance"] as JsonObject;
        analysis["runtimeCatalog"] = new JsonObject
        {
            ["status"] = runtimeCatalogStatus,
            ["entryCount"] = catalogIds?.Count ?? 0,
            ["recordTypeIds"] = ToIntArray(recordTypeIds.OrderBy(value => value)),
            ["recordTypeIdsMissingFromCatalog"] = ToIntArray(missingTypeIds)
        };
        if (StringValue(runtimeProvenance ?? [], "packageVersion") is { } packageVersion)
            ((JsonObject)analysis["runtimeCatalog"]!)["packageVersion"] = packageVersion;
        if (hook["typetree"]?.AsObject()["nativeTransfer"] is JsonNode nativeTransfer)
            analysis["nativeTransfer"] = nativeTransfer.DeepClone();
        analysis["decodedTypeTree"] = decodedTypeTree;
        if (hook["typetree"] is JsonObject typeTree
            && typeTree["baseline"] is JsonObject baseline
            && StringValue(baseline, "file") is { } baselineFile)
        {
            var baselinePath = Path.GetFullPath(Path.Combine(
                hookDirectory, baselineFile.Replace('/', Path.DirectorySeparatorChar)));
            var baselineAnalysis = DecodeAttachedTypeTree(hookDirectory, hook, baselinePath);
            baselineAnalysis["provenance"] = baseline["provenance"]?.DeepClone();
            analysis["decodedTypeTreeBaseline"] = baselineAnalysis;
        }
        File.WriteAllText(outputPath, analysis.ToJsonString(JsonOptions));

        Console.WriteLine($"记录: {records.Count}, 对象: {objects.Count}, Transfer: {transfers.Count}, vtable: {vtables.Count}");
        Console.WriteLine($"快照: {snapshotCounts.Values.Sum()}, 命中已观测引用: {snapshotReferences.Count}");
        Console.WriteLine($"已生成: {outputPath}");
        return 0;
    }

    private static int InspectTypeTree(string[] args)
    {
        var options = ParseOptions(args);
        var positionals = options.Positionals();
        if (positionals.Count > 0 && string.Equals(positionals[0], "merge", StringComparison.OrdinalIgnoreCase))
            return MergeTypeTrees(args[1..]);
        if (positionals.Count > 0 && string.Equals(positionals[0], "import-struct", StringComparison.OrdinalIgnoreCase))
            return ImportStructDump(args[1..]);
        if (positionals.Count != 1)
            throw new ArgumentException("typetree 需要一个 .ttbin 路径，或使用 typetree merge/import-struct");

        var sourcePath = FullPath(options.Positionals()[0]);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到 TypeTree 文件", sourcePath);

        JsonObject decoded;
        try
        {
            decoded = ReadTypeTreeBinary(sourcePath);
        }
        catch (InvalidDataException ex)
        {
            decoded = new JsonObject
            {
                ["status"] = "invalid",
                ["file"] = sourcePath,
                ["reason"] = ex.Message
            };
        }

        var treeStates = new JsonArray();
        var decodedTreesWithPaths = new JsonArray();
        var fieldIntegrity = new JsonArray();
        var decodedTypes = new HashSet<string>(StringComparer.Ordinal);
        var seenSerializedTrees = new List<JsonObject>();
        foreach (var tree in decoded["trees"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            // A guest dump can contain the same serialized tree more than
            // once when the same runtime vtable is observed through multiple
            // paths. Deduplicate only byte-for-byte equivalent JSON trees;
            // same type names with different nodes remain separate evidence.
            if (seenSerializedTrees.Any(existing => JsonNode.DeepEquals(existing, tree)))
                continue;
            seenSerializedTrees.Add(tree);
            var rtti = tree["rtti"] as JsonObject;
            var className = StringValue(rtti ?? [], "className") ?? "<unknown>";
            var nodeCount = Int64Value(tree, "nodeCount");
            var treeStatus = nodeCount > 0 ? "decoded" : "decoded_empty";
            if (nodeCount > 0) decodedTypes.Add(className);
            var annotatedTree = (JsonObject)tree.DeepClone();
            var treeIntegrity = AnalyzeTypeTreeFields(annotatedTree);
            annotatedTree["fieldIntegrity"] = treeIntegrity;
            decodedTreesWithPaths.Add(annotatedTree);
            fieldIntegrity.Add(new JsonObject
            {
                ["typeName"] = className,
                ["persistentTypeId"] = rtti?["persistentTypeId"]?.DeepClone(),
                ["result"] = treeIntegrity.DeepClone()
            });
            treeStates.Add(new JsonObject
            {
                ["typeName"] = className,
                ["persistentTypeId"] = rtti?["persistentTypeId"]?.DeepClone(),
                ["objectSize"] = rtti?["size"]?.DeepClone(),
                ["nodeCount"] = nodeCount,
                ["status"] = treeStatus,
                ["fieldIntegrity"] = treeIntegrity.DeepClone()
            });
        }

        var requiredTypes = new List<string>();
        if (options.TryGetValue("required-type", out var requiredValues))
        {
            foreach (var value in requiredValues)
            {
                requiredTypes.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));
            }
        }
        requiredTypes = requiredTypes
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        var catalogTypes = ReadCatalogTypeNames(options.GetValueOrDefault("catalog"));
        var coverageTypes = requiredTypes.Count > 0
            ? requiredTypes
            : catalogTypes is not null
                ? catalogTypes.OrderBy(value => value, StringComparer.Ordinal).ToList()
                : [];
        var coverage = new JsonArray();
        var decodedCoverageCount = 0;
        foreach (var typeName in coverageTypes)
        {
            var decodedTree = treeStates.OfType<JsonObject>()
                .FirstOrDefault(tree => string.Equals(
                    StringValue(tree, "typeName"), typeName, StringComparison.Ordinal));
            var coverageStatus = decodedTypes.Contains(typeName)
                ? "decoded"
                : decodedTree is not null ? "decoded_empty" : "missing";
            if (coverageStatus == "decoded") decodedCoverageCount++;
            coverage.Add(new JsonObject
            {
                ["typeName"] = typeName,
                ["status"] = coverageStatus,
                ["nodeCount"] = decodedTree?["nodeCount"]?.DeepClone()
            });
        }

        var decodedStatus = StringValue(decoded, "status") ??
            (Int64Value(decoded, "totalNodes") == 0 ? "decoded_empty" : "decoded");
        var compatibility = ValidateTypeTreeHeader(decoded, options);
        var fieldIntegritySummary = SummarizeTypeTreeFields(fieldIntegrity);
        var fieldIntegrityStatus = StringValue(fieldIntegritySummary, "status") ?? "invalid";
        var uniqueTreeCount = treeStates.Count;
        var uniqueNodeCount = treeStates.OfType<JsonObject>()
            .Sum(tree => Int64Value(tree, "nodeCount"));
        var status = decodedStatus == "invalid"
            ? "invalid"
            : compatibility is not null && StringValue(compatibility, "status") != "compatible"
                ? "incompatible"
            : fieldIntegrityStatus != "valid"
                ? "field_integrity_invalid"
            : decodedStatus == "decoded_empty"
                ? "decoded_empty"
                : coverageTypes.Count == 0
                    ? "decoded"
                    : decodedCoverageCount == coverageTypes.Count
                        ? "complete_for_requested_types"
                        : "partial_for_requested_types";

        var report = new JsonObject
        {
            ["schema"] = "my-hook.typetree-report.v1",
            ["status"] = status,
            ["source"] = new JsonObject
            {
                ["file"] = sourcePath,
                ["size"] = new FileInfo(sourcePath).Length,
                ["sha256"] = Sha256(sourcePath)
            },
            ["header"] = decoded["header"]?.DeepClone(),
            ["treeCount"] = uniqueTreeCount,
            ["totalNodes"] = uniqueNodeCount,
            ["compatibility"] = compatibility ?? new JsonObject { ["status"] = "not_checked" },
            ["trees"] = treeStates,
            ["fieldIntegrity"] = fieldIntegritySummary,
            ["coverage"] = new JsonObject
            {
                ["catalogFile"] = options.GetValueOrDefault("catalog"),
                ["requestedTypeCount"] = coverageTypes.Count,
                ["decodedTypeCount"] = decodedCoverageCount,
                ["types"] = coverage
            },
            ["policy"] = "only serialized TypeTree nodes are reported; missing fields are not inferred"
        };
        if (options.ContainsKey("full"))
            report["decodedTrees"] = decodedTreesWithPaths;

        var outputPath = options.GetValueOrDefault("output") is { } outputValue
            ? FullPath(outputValue)
            : Path.ChangeExtension(sourcePath, ".typetree-report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, report.ToJsonString(JsonOptions));
        Console.WriteLine($"TypeTree: {status}, trees={report["treeCount"]}, nodes={report["totalNodes"]}");
        if (coverageTypes.Count > 0)
            Console.WriteLine($"字段覆盖: {decodedCoverageCount}/{coverageTypes.Count}");
        Console.WriteLine($"已生成: {outputPath}");
        return options.ContainsKey("strict") && status is not "complete_for_requested_types" and not "decoded"
            ? 2
            : status == "invalid" ? 1 : 0;
    }

    private static JsonObject AnalyzeTypeTreeFields(JsonObject tree)
    {
        var nodes = tree["nodes"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var errors = new JsonArray();
        var stack = new List<int>();
        var paths = new string[nodes.Length];
        var seenIndexes = new HashSet<int>();
        var nonEmptyTypes = 0;
        var nonEmptyNames = 0;
        var parentResolved = 0;
        var nonZeroRefTypeHashes = 0;
        var invalidByteSizes = 0;
        var invalidLevels = 0;
        var missingIndexes = 0;
        var sequentialIndexes = true;

        for (var nodePosition = 0; nodePosition < nodes.Length; nodePosition++)
        {
            var node = nodes[nodePosition];
            var type = StringValue(node, "type");
            var name = StringValue(node, "name");
            if (!string.IsNullOrWhiteSpace(type)) nonEmptyTypes++;
            else errors.Add($"node[{nodePosition}] has an empty type");
            if (!string.IsNullOrWhiteSpace(name)) nonEmptyNames++;
            else errors.Add($"node[{nodePosition}] has an empty name");

            if (node["index"] is not JsonValue)
            {
                missingIndexes++;
                sequentialIndexes = false;
            }
            else
            {
                var index = (int)Int64Value(node, "index");
                if (index != nodePosition || !seenIndexes.Add(index))
                    sequentialIndexes = false;
            }

            var byteSize = Int64Value(node, "byteSize");
            if (byteSize < -1)
                invalidByteSizes++;

            var level = (int)Int64Value(node, "level");
            if (level < 0 || level > nodePosition + 1
                || (nodePosition == 0 && level != 0)
                || (nodePosition > 0 && level > (int)Int64Value(nodes[nodePosition - 1], "level") + 1))
            {
                invalidLevels++;
                errors.Add($"node[{nodePosition}] has an invalid level {level}");
                level = Math.Max(0, Math.Min(level, stack.Count));
            }

            while (stack.Count > level)
                stack.RemoveAt(stack.Count - 1);
            var parentIndex = level == 0 ? -1 : stack.Count >= level ? stack[level - 1] : -1;
            if (level > 0 && parentIndex >= 0)
                parentResolved++;
            else if (level > 0)
                errors.Add($"node[{nodePosition}] has no resolvable parent at level {level}");

            var parentPath = level > 0 && parentIndex >= 0 ? paths[parentIndex] : null;
            paths[nodePosition] = string.IsNullOrWhiteSpace(parentPath)
                ? name ?? "<unnamed>"
                : $"{parentPath}.{name ?? "<unnamed>"}";
            node["parentIndex"] = parentIndex;
            node["fieldPath"] = paths[nodePosition];
            while (stack.Count < level)
                stack.Add(-1);
            if (stack.Count == level)
                stack.Add(nodePosition);
            else
                stack[level] = nodePosition;

            if (UInt64Value(node, "refTypeHash") != 0)
                nonZeroRefTypeHashes++;
        }

        if (!sequentialIndexes)
            errors.Add("node index values are not a unique zero-based sequence");
        if (invalidByteSizes > 0)
            errors.Add($"{invalidByteSizes} node byteSize values are less than -1");

        return new JsonObject
        {
            ["status"] = errors.Count == 0 ? "valid" : "invalid",
            ["serializedNodeCount"] = nodes.Length,
            ["nonEmptyTypeCount"] = nonEmptyTypes,
            ["nonEmptyNameCount"] = nonEmptyNames,
            ["parentResolvedCount"] = parentResolved,
            ["missingIndexCount"] = missingIndexes,
            ["invalidLevelCount"] = invalidLevels,
            ["invalidByteSizeCount"] = invalidByteSizes,
            ["nonZeroRefTypeHashCount"] = nonZeroRefTypeHashes,
            ["refTypeHashCoverage"] = nodes.Length == 0 ? 0 : (double)nonZeroRefTypeHashes / nodes.Length,
            ["errors"] = errors
        };
    }

    private static JsonObject SummarizeTypeTreeFields(JsonArray reports)
    {
        var objects = reports.OfType<JsonObject>().ToArray();
        var invalidReports = objects
            .Where(report => StringValue(report["result"] as JsonObject ?? [], "status") != "valid")
            .ToArray();
        var invalid = invalidReports.Length;
        var totalNodes = objects.Sum(report =>
            (int)Int64Value(report["result"] as JsonObject, "serializedNodeCount"));
        var refHashNodes = objects.Sum(report =>
            (int)Int64Value(report["result"] as JsonObject, "nonZeroRefTypeHashCount"));
        var errors = new JsonArray();
        foreach (var report in invalidReports)
            errors.Add(report.DeepClone());
        return new JsonObject
        {
            ["status"] = invalid == 0 ? "valid" : "invalid",
            ["treeCount"] = objects.Length,
            ["invalidTreeCount"] = invalid,
            ["serializedNodeCount"] = totalNodes,
            ["nonZeroRefTypeHashCount"] = refHashNodes,
            ["refTypeHashCoverage"] = totalNodes == 0 ? 0 : (double)refHashNodes / totalNodes,
            ["fieldPaths"] = "computed from serialized node level/name; they are not additional source fields",
            ["errors"] = errors
        };
    }

    private static bool IsStructurallyValidTypeTree(JsonObject tree)
    {
        var probe = (JsonObject)tree.DeepClone();
        return StringValue(AnalyzeTypeTreeFields(probe), "status") == "valid";
    }

    private static int MergeTypeTrees(string[] args)
    {
        var options = ParseOptions(args);
        var positionals = options.Positionals();
        if (positionals.Count < 2)
            throw new ArgumentException("typetree merge 需要输出路径和至少一个输入 .ttbin");

        var outputPath = FullPath(positionals[0]);
        if (File.Exists(outputPath) && !options.ContainsKey("force"))
            throw new IOException($"输出文件已存在；如需覆盖请显式指定 --force: {outputPath}");

        JsonObject? first = null;
        var trees = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var treeSources = new Dictionary<string, string>(StringComparer.Ordinal);
        var replacements = new JsonArray();
        var rejectedCandidates = new JsonArray();
        foreach (var inputValue in positionals.Skip(1))
        {
            var inputPath = FullPath(inputValue);
            if (!File.Exists(inputPath))
                throw new FileNotFoundException("找不到 TypeTree 输入文件", inputPath);
            var decoded = ReadTypeTreeBinary(inputPath);
            first ??= decoded;
            EnsureSameTypeTreeHeader(first, decoded, inputPath);
            foreach (var tree in decoded["trees"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var rtti = tree["rtti"]?.AsObject()
                    ?? throw new InvalidDataException($"TypeTree 缺少 RTTI: {inputPath}");
                var key = string.Join("|", new[]
                {
                    StringValue(rtti, "persistentTypeId") ?? "",
                    StringValue(rtti, "className") ?? "",
                    StringValue(rtti, "classNamespace") ?? ""
                });
                if (trees.TryGetValue(key, out var existing))
                {
                    if (!JsonNode.DeepEquals(existing, tree))
                    {
                        var existingNodeCount = Int64Value(existing, "nodeCount");
                        var candidateNodeCount = Int64Value(tree, "nodeCount");
                        var existingValid = IsStructurallyValidTypeTree(existing);
                        var candidateValid = IsStructurallyValidTypeTree(tree);
                        if (options.ContainsKey("prefer-more-nodes")
                            && candidateValid
                            && (!existingValid || candidateNodeCount > existingNodeCount))
                        {
                            var previousInput = treeSources[key];
                            trees[key] = (JsonObject)tree.DeepClone();
                            treeSources[key] = inputPath;
                            replacements.Add(new JsonObject
                            {
                                ["identity"] = key,
                                ["previousInput"] = previousInput,
                                ["selectedInput"] = inputPath,
                                ["previousNodeCount"] = existingNodeCount,
                                ["selectedNodeCount"] = candidateNodeCount,
                                ["reason"] = existingValid
                                    ? "both candidates structurally valid; explicit prefer-more-nodes policy"
                                    : "candidate structurally valid; existing candidate invalid"
                            });
                        }
                        else if (options.ContainsKey("prefer-more-nodes")
                            && existingValid && !candidateValid)
                        {
                            rejectedCandidates.Add(new JsonObject
                            {
                                ["identity"] = key,
                                ["input"] = inputPath,
                                ["nodeCount"] = candidateNodeCount,
                                ["reason"] = "candidate failed TypeTree field integrity; existing valid tree retained"
                            });
                        }
                        else if (!options.ContainsKey("prefer-more-nodes")
                            || (!existingValid && !candidateValid)
                            || (candidateValid && existingValid && candidateNodeCount == existingNodeCount))
                        {
                            throw new InvalidDataException(
                                $"TypeTree 输入发生字段冲突，拒绝静默覆盖: {key} ({inputPath})");
                        }
                    }
                }
                else
                {
                    trees[key] = (JsonObject)tree.DeepClone();
                    treeSources[key] = inputPath;
                }
            }
        }

        if (first is null)
            throw new InvalidDataException("没有有效 TypeTree 输入");
        var merged = (JsonObject)first.DeepClone();
        var mergedTrees = new JsonArray();
        foreach (var tree in trees.Values.OrderBy(
            tree => StringValue(tree["rtti"]?.AsObject() ?? [], "className"), StringComparer.Ordinal))
            mergedTrees.Add(tree);
        merged["treeCount"] = mergedTrees.Count;
        merged["totalNodes"] = mergedTrees
            .OfType<JsonObject>()
            .Sum(tree => Int64Value(tree, "nodeCount"));
        merged["trees"] = mergedTrees;
        WriteTypeTreeBinary(outputPath, merged);
        WriteTypeTreeManifest(
            Path.ChangeExtension(outputPath, ".manifest.json"),
            outputPath,
            "merge",
            positionals.Skip(1).Select(FullPath).ToArray(),
            merged,
            new JsonObject
            {
                ["conflictPolicy"] = options.ContainsKey("prefer-more-nodes")
                    ? "prefer-more-nodes"
                    : "reject-conflicts",
                ["replacements"] = replacements,
                ["rejectedCandidates"] = rejectedCandidates
            });
        Console.WriteLine($"已合并 TypeTree: trees={merged["treeCount"]}, nodes={merged["totalNodes"]}");
        Console.WriteLine($"已生成: {outputPath}");
        return 0;
    }

    private static int ImportStructDump(string[] args)
    {
        var options = ParseOptions(args);
        var positionals = options.Positionals();
        if (positionals.Count != 2)
            throw new ArgumentException("typetree import-struct 需要输入 .structs.dump 和输出 .ttbin");

        var inputPath = FullPath(positionals[0]);
        var outputPath = FullPath(positionals[1]);
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("找不到 StructDump", inputPath);
        if (File.Exists(outputPath) && !options.ContainsKey("force"))
            throw new IOException($"输出文件已存在；如需覆盖请显式指定 --force: {outputPath}");

        var catalogPath = options.GetValueOrDefault("catalog");
        var catalog = ReadTypeTreeCatalog(catalogPath);
        var parsed = ParseStructDump(inputPath, catalog);
        var revision = options.GetValueOrDefault("revision")
            ?? parsed.Revision
            ?? throw new InvalidDataException("StructDump 未包含 Unity 版本；请指定 --revision 2019.4.34");
        var variant = options.GetValueOrDefault("variant") ?? "Runtime";
        var trees = new JsonArray();
        foreach (var type in parsed.Types.OrderBy(type => type.PersistentTypeId))
        {
            var rtti = new JsonObject
            {
                ["className"] = type.ClassName,
                ["classNamespace"] = "",
                ["module"] = "Core",
                ["persistentTypeId"] = type.PersistentTypeId,
                ["size"] = type.Size,
                ["flags"] = type.IsAbstract ? 1 : 0,
                ["basePersistentTypeId"] = type.BasePersistentTypeId < 0
                    ? uint.MaxValue
                    : type.BasePersistentTypeId,
                ["derivedFromTypeIndex"] = 0,
                ["derivedFromDescendantCount"] = 0
            };
            var nodes = new JsonArray();
            foreach (var node in type.Nodes)
            {
                nodes.Add(new JsonObject
                {
                    ["type"] = node.Type,
                    ["name"] = node.Name,
                    ["flags"] = node.Flags,
                    ["byteSize"] = node.ByteSize,
                    ["index"] = node.Index,
                    ["version"] = node.Version,
                    ["level"] = node.Level,
                    ["metaFlags"] = node.MetaFlags,
                    ["refTypeHash"] = "0x0000000000000000"
                });
            }
            trees.Add(new JsonObject
            {
                ["rtti"] = rtti,
                ["transferFlags"] = 0,
                ["nodeCount"] = nodes.Count,
                ["nodes"] = nodes
            });
        }

        var decoded = new JsonObject
        {
            ["format"] = "TypeTreeRipper.ttbin",
            ["header"] = new JsonObject
            {
                ["version"] = 1,
                ["revision"] = revision,
                ["variant"] = variant
            },
            ["treeCount"] = trees.Count,
            ["totalNodes"] = trees.OfType<JsonObject>().Sum(tree => Int64Value(tree, "nodeCount")),
            ["trees"] = trees
        };
        WriteTypeTreeBinary(outputPath, decoded);
        WriteTypeTreeManifest(
            Path.ChangeExtension(outputPath, ".manifest.json"),
            outputPath,
            "import-struct",
            new[] { inputPath }.Concat(catalogPath is null ? [] : new[] { FullPath(catalogPath) }).ToArray(),
            decoded,
            new JsonObject
            {
                ["sourceFormat"] = "TypeTreeRipper legacy StructDump text",
                ["catalogFile"] = catalogPath,
                ["classCount"] = parsed.Types.Count,
                ["fieldNodeCount"] = parsed.Types.Sum(type => type.Nodes.Count),
                ["catalogMissingClassCount"] = parsed.CatalogMissingClassCount,
                ["refTypeHashAvailable"] = false,
                ["policy"] = "legacy text preserves field names and layout metadata; refTypeHash is unavailable and remains zero"
            });
        Console.WriteLine($"已导入 StructDump: trees={decoded["treeCount"]}, nodes={decoded["totalNodes"]}");
        Console.WriteLine($"已生成: {outputPath}");
        return 0;
    }

    private static Dictionary<int, StructCatalogEntry> ReadTypeTreeCatalog(string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
            return new Dictionary<int, StructCatalogEntry>();

        var path = FullPath(pathValue);
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到 --catalog", path);
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || !lines[0].StartsWith("persistentTypeID|", StringComparison.Ordinal))
            throw new InvalidDataException("TypeTree RTTI catalog 格式无效");

        var result = new Dictionary<int, StructCatalogEntry>();
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split('|');
            if (fields.Length < 5
                || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                continue;
            var isAbstract = string.Equals(fields[2], "1", StringComparison.Ordinal);
            result[id] = new StructCatalogEntry(id, size, isAbstract, fields[4].Trim());
        }
        return result;
    }

    private static StructDumpData ParseStructDump(
        string inputPath,
        IReadOnlyDictionary<int, StructCatalogEntry> catalog)
    {
        var types = new List<StructDumpType>();
        StructDumpType? current = null;
        string? revision = null;
        var versionMatch = Regex.Match(
            File.ReadLines(inputPath).FirstOrDefault() ?? "",
            @"Unity\s+(?<version>\d+\.\d+\.\d+)",
            RegexOptions.CultureInvariant);
        if (versionMatch.Success)
            revision = versionMatch.Groups["version"].Value;

        var classPattern = new Regex(
            @"^//\s+classID\{(?<id>-?\d+)\}:\s+(?<chain>.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var abstractPattern = new Regex(
            @"^//\s+(?<name>.+?)\s+is abstract\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var nodePattern = new Regex(
            @"^(?<byte>[0-9a-fA-F]+)\},\s+Index\{(?<index>[0-9a-fA-F]+)\},\s+Version\{(?<version>[0-9a-fA-F]+)\},\s+IsArray\{(?<flags>[0-9a-fA-F]+)\},\s+MetaFlag\{(?<meta>[0-9a-fA-F]+)\}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        foreach (var rawLine in File.ReadLines(inputPath).Skip(1))
        {
            var line = rawLine.TrimEnd('\r');
            var classMatch = classPattern.Match(line);
            if (classMatch.Success)
            {
                var chain = classMatch.Groups["chain"].Value
                    .Split(" <- ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var id = int.Parse(classMatch.Groups["id"].Value, CultureInfo.InvariantCulture);
                var className = chain[0];
                var baseName = chain.Length > 1 ? chain[1] : null;
                catalog.TryGetValue(id, out var catalogEntry);
                current = new StructDumpType(
                    id,
                    className,
                    baseName,
                    catalogEntry?.Size ?? -1,
                    catalogEntry?.IsAbstract ?? false);
                types.Add(current);
                continue;
            }

            if (current is null)
                continue;

            var abstractMatch = abstractPattern.Match(line);
            if (abstractMatch.Success
                && string.Equals(abstractMatch.Groups["name"].Value, current.ClassName, StringComparison.Ordinal))
            {
                current.IsAbstract = true;
                continue;
            }

            var marker = " // ByteSize{";
            var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
                continue;

            var declaration = line[..markerIndex].Trim();
            var fields = line[(markerIndex + marker.Length)..];
            var nodeMatch = nodePattern.Match(fields);
            if (!nodeMatch.Success || !TrySplitStructDeclaration(declaration, out var type, out var name))
                continue;

            current.Nodes.Add(new StructDumpNode(
                type,
                name,
                CountLeadingTabs(line),
                unchecked((int)ParseHexUInt(nodeMatch.Groups["byte"].Value)),
                unchecked((int)ParseHexUInt(nodeMatch.Groups["index"].Value)),
                unchecked((short)ParseHexUInt(nodeMatch.Groups["version"].Value)),
                ParseHexUInt(nodeMatch.Groups["flags"].Value),
                ParseHexUInt(nodeMatch.Groups["meta"].Value)));
        }

        var typesByName = types
            .GroupBy(type => type.ClassName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().PersistentTypeId, StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (type.BaseName is not null && typesByName.TryGetValue(type.BaseName, out var baseId))
                type.BasePersistentTypeId = baseId;
        }

        return new StructDumpData(
            revision,
            types,
            types.Count(type => !catalog.ContainsKey(type.PersistentTypeId)));
    }

    private static bool TrySplitStructDeclaration(string declaration, out string type, out string name)
    {
        var multiWordTypes = new[]
        {
            "unsigned long long",
            "unsigned short",
            "unsigned int",
            "unsigned long",
            "signed short",
            "signed int",
            "signed long",
            "long long"
        };
        foreach (var candidate in multiWordTypes)
        {
            if (!declaration.StartsWith(candidate + " ", StringComparison.Ordinal))
                continue;
            type = candidate;
            name = declaration[(candidate.Length + 1)..].Trim();
            return name.Length > 0;
        }

        var separator = declaration.IndexOf(' ');
        if (separator <= 0 || separator == declaration.Length - 1)
        {
            type = "";
            name = "";
            return false;
        }
        type = declaration[..separator];
        name = declaration[(separator + 1)..].Trim();
        return name.Length > 0;
    }

    private static uint ParseHexUInt(string value) =>
        uint.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    private static byte CountLeadingTabs(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] == '\t') count++;
        return checked((byte)count);
    }

    private static void WriteTypeTreeManifest(
        string manifestPath,
        string outputPath,
        string operation,
        IReadOnlyList<string> inputPaths,
        JsonObject output,
        JsonObject? details)
    {
        var inputs = new JsonArray();
        foreach (var inputPath in inputPaths)
        {
            var file = new FileInfo(inputPath);
            inputs.Add(new JsonObject
            {
                ["path"] = inputPath,
                ["size"] = file.Exists ? file.Length : 0,
                ["sha256"] = file.Exists ? Sha256(inputPath) : null
            });
        }

        var manifest = new JsonObject
        {
            ["schema"] = "my-hook.typetree-manifest.v1",
            ["operation"] = operation,
            ["output"] = new JsonObject
            {
                ["path"] = outputPath,
                ["size"] = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0,
                ["sha256"] = File.Exists(outputPath) ? Sha256(outputPath) : null,
                ["header"] = output["header"]?.DeepClone(),
                ["treeCount"] = output["treeCount"]?.DeepClone(),
                ["totalNodes"] = output["totalNodes"]?.DeepClone()
            },
            ["inputs"] = inputs,
            ["details"] = details?.DeepClone(),
            ["policy"] = "Inputs are retained by path and SHA-256; incompatible headers or conflicting fields must not be silently merged."
        };
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, manifest.ToJsonString(JsonOptions));
    }

    private sealed class StructDumpType(
        int persistentTypeId,
        string className,
        string? baseName,
        int size,
        bool isAbstract)
    {
        public int PersistentTypeId { get; } = persistentTypeId;
        public string ClassName { get; } = className;
        public string? BaseName { get; } = baseName;
        public int Size { get; } = size;
        public bool IsAbstract { get; set; } = isAbstract;
        public int BasePersistentTypeId { get; set; } = -1;
        public List<StructDumpNode> Nodes { get; } = [];
    }

    private sealed record StructDumpNode(
        string Type,
        string Name,
        byte Level,
        int ByteSize,
        int Index,
        short Version,
        uint Flags,
        uint MetaFlags);

    private sealed record StructCatalogEntry(
        int PersistentTypeId,
        int Size,
        bool IsAbstract,
        string ClassName);

    private sealed record StructDumpData(
        string? Revision,
        List<StructDumpType> Types,
        int CatalogMissingClassCount);

    private static JsonObject? ValidateTypeTreeHeader(JsonObject decoded, Dictionary<string, List<string>> options)
    {
        var expectedRevision = options.TryGetValue("expected-revision", out var revisionValues)
            && revisionValues.Count > 0 ? revisionValues[^1] : null;
        var expectedVariant = options.TryGetValue("expected-variant", out var variantValues)
            && variantValues.Count > 0 ? variantValues[^1] : null;
        if (string.IsNullOrWhiteSpace(expectedRevision) && string.IsNullOrWhiteSpace(expectedVariant))
            return null;

        var header = decoded["header"]?.AsObject();
        var actualRevision = StringValue(header ?? [], "revision") ?? "";
        var actualVariant = StringValue(header ?? [], "variant") ?? "";
        var revisionMatches = string.IsNullOrWhiteSpace(expectedRevision)
            || string.Equals(actualRevision, expectedRevision, StringComparison.OrdinalIgnoreCase);
        var variantMatches = string.IsNullOrWhiteSpace(expectedVariant)
            || string.Equals(actualVariant, expectedVariant, StringComparison.OrdinalIgnoreCase);
        return new JsonObject
        {
            ["status"] = revisionMatches && variantMatches ? "compatible" : "mismatch",
            ["expectedRevision"] = expectedRevision,
            ["actualRevision"] = actualRevision,
            ["expectedVariant"] = expectedVariant,
            ["actualVariant"] = actualVariant
        };
    }

    private static void EnsureSameTypeTreeHeader(JsonObject first, JsonObject current, string inputPath)
    {
        var firstHeader = first["header"]?.AsObject();
        var currentHeader = current["header"]?.AsObject();
        var firstRevision = StringValue(firstHeader ?? [], "revision") ?? "";
        var currentRevision = StringValue(currentHeader ?? [], "revision") ?? "";
        var firstVariant = StringValue(firstHeader ?? [], "variant") ?? "";
        var currentVariant = StringValue(currentHeader ?? [], "variant") ?? "";
        if (!string.Equals(firstRevision, currentRevision, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(firstVariant, currentVariant, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"TypeTree 版本不一致，拒绝合并: first={firstRevision}/{firstVariant}, input={currentRevision}/{currentVariant}, file={inputPath}");
    }

    private static void WriteTypeTreeBinary(string path, JsonObject decoded)
    {
        var header = decoded["header"]?.AsObject()
            ?? throw new InvalidDataException("TypeTree 缺少 header");
        var trees = decoded["trees"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(0x4545525445505954UL);
        writer.Write(1U);
        var revision = (StringValue(header, "revision") ?? "0.0.0").Split('.');
        writer.Write(ushort.Parse(revision.ElementAtOrDefault(0) ?? "0", CultureInfo.InvariantCulture));
        writer.Write(byte.Parse(revision.ElementAtOrDefault(1) ?? "0", CultureInfo.InvariantCulture));
        writer.Write(byte.Parse(revision.ElementAtOrDefault(2) ?? "0", CultureInfo.InvariantCulture));
        WriteLengthPrefixedString(writer, StringValue(header, "variant") ?? "Runtime");
        writer.Write((uint)trees.Length);
        foreach (var tree in trees)
        {
            var rtti = tree["rtti"]?.AsObject()
                ?? throw new InvalidDataException("TypeTree 缺少 RTTI");
            WriteLengthPrefixedString(writer, StringValue(rtti, "className") ?? "");
            WriteLengthPrefixedString(writer, StringValue(rtti, "classNamespace") ?? "");
            WriteLengthPrefixedString(writer, StringValue(rtti, "module") ?? "");
            writer.Write(Int32Value(rtti, "persistentTypeId"));
            writer.Write(Int32Value(rtti, "size"));
            writer.Write(UInt32Value(rtti, "flags"));
            writer.Write(UInt32Value(rtti, "basePersistentTypeId"));
            writer.Write(UInt32Value(rtti, "derivedFromTypeIndex"));
            writer.Write(UInt32Value(rtti, "derivedFromDescendantCount"));
            writer.Write(UInt64Value(tree, "transferFlags"));
            var nodes = tree["nodes"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
            writer.Write((uint)nodes.Length);
            foreach (var node in nodes)
            {
                WriteLengthPrefixedString(writer, StringValue(node, "type") ?? "");
                WriteLengthPrefixedString(writer, StringValue(node, "name") ?? "");
                writer.Write(UInt32Value(node, "flags"));
                writer.Write(Int32Value(node, "byteSize"));
                writer.Write(Int32Value(node, "index"));
                writer.Write((short)Int32Value(node, "version"));
                writer.Write((byte)Int32Value(node, "level"));
                writer.Write(UInt32Value(node, "metaFlags"));
                writer.Write(UInt64Value(node, "refTypeHash"));
            }
        }
    }

    private static void WriteLengthPrefixedString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static int Int32Value(JsonObject? node, string key) => checked((int)Int64Value(node, key));

    private static uint UInt32Value(JsonObject? node, string key) => checked((uint)Int64Value(node, key));

    private static ulong UInt64Value(JsonObject? node, string key)
    {
        var text = StringValue(node ?? [], key) ?? "0";
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static HashSet<string>? ReadCatalogTypeNames(string? pathValue)
    {
        if (string.IsNullOrWhiteSpace(pathValue)) return null;
        var path = FullPath(pathValue);
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到 --catalog", path);
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || !lines[0].StartsWith("persistentTypeID|", StringComparison.Ordinal))
            throw new InvalidDataException("TypeTree RTTI catalog 格式无效");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split('|');
            if (fields.Length >= 5 && !string.IsNullOrWhiteSpace(fields[4]))
                names.Add(fields[4]);
        }
        return names;
    }

    private static long Int64Value(JsonObject? node, string key)
    {
        if (node?[key] is not JsonValue value) return 0;
        if (value.TryGetValue<long>(out var integer)) return integer;
        if (value.TryGetValue<short>(out var signed16)) return signed16;
        if (value.TryGetValue<sbyte>(out var signed8)) return signed8;
        if (value.TryGetValue<int>(out var signedInteger)) return signedInteger;
        if (value.TryGetValue<uint>(out var unsigned32)) return unsigned32;
        if (value.TryGetValue<ushort>(out var unsigned16)) return unsigned16;
        if (value.TryGetValue<byte>(out var unsigned8)) return unsigned8;
        if (value.TryGetValue<ulong>(out var unsignedInteger) && unsignedInteger <= long.MaxValue)
            return (long)unsignedInteger;
        return 0;
    }

    private static void AddPointer(JsonObject record, string key, HashSet<ulong> target)
    {
        if (TryParsePointer(StringValue(record, key), out var value) && value != 0)
            target.Add(value);
    }

    private static HashSet<string> PointerKeys(
        IEnumerable<JsonObject> records,
        string key)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var value = StringValue(record, key);
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }
        return result;
    }

    private static HashSet<string> ContextActiveObjectKeys(
        IEnumerable<JsonObject> records,
        string typeName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!string.Equals(StringValue(record, "typeName"), typeName,
                    StringComparison.Ordinal)
                || record["activeTransfer"] is not JsonObject active)
                continue;

            var value = StringValue(active, "object");
            if (!string.IsNullOrWhiteSpace(value))
                result.Add(value);
        }
        return result;
    }

    private static JsonArray FindSnapshotReferences(
        byte[] bytes,
        HashSet<ulong> objects,
        HashSet<ulong> transfers,
        HashSet<ulong> vtables)
    {
        var result = new JsonArray();
        for (var offset = 0; offset + sizeof(ulong) <= bytes.Length; offset += sizeof(ulong))
        {
            var value = BitConverter.ToUInt64(bytes, offset);
            var role = objects.Contains(value) ? "object" :
                transfers.Contains(value) ? "transfer" :
                vtables.Contains(value) ? "vtable" : null;
            if (role is null)
                continue;

            result.Add(new JsonObject
            {
                ["offset"] = offset,
                ["value"] = $"0x{value:x}",
                ["role"] = role
            });
        }
        return result;
    }

    private static bool TryDecodeHex(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static void CollectRuntimeFieldEvidence(
        JsonObject record,
        JsonArray output,
        HashSet<string> seen)
    {
        if (record["typeTreeObjectReferencedBuffers"] is not JsonArray buffers)
            return;

        for (var bufferIndex = 0; bufferIndex < buffers.Count; bufferIndex++)
        {
            if (buffers[bufferIndex] is not JsonObject bufferRecord
                || bufferRecord["buffer"] is not JsonObject buffer
                || StringValue(buffer, "hex") is not { } hex
                || !TryDecodeHex(hex, out var bytes))
                continue;

            var printable = ExtractPrintableAscii(bytes, 4);
            foreach (var value in printable)
            {
                var key = $"{StringValue(record, "object")}|{bufferIndex}|{value}";
                if (!seen.Add(key))
                    continue;

                output.Add(new JsonObject
                {
                    ["typeName"] = StringValue(record, "typeName"),
                    ["typeId"] = record["typeId"]?.DeepClone(),
                    ["object"] = StringValue(record, "object"),
                    ["bufferIndex"] = bufferIndex,
                    ["value"] = value,
                    ["evidence"] = "bounded_guest_transfer_buffer"
                });
            }
        }
    }

    private static IEnumerable<string> ExtractPrintableAscii(byte[] bytes, int minimumLength)
    {
        var start = -1;
        for (var index = 0; index <= bytes.Length; index++)
        {
            var printable = index < bytes.Length && bytes[index] >= 0x20 && bytes[index] <= 0x7e;
            if (printable)
            {
                if (start < 0)
                    start = index;
                continue;
            }

            if (start >= 0 && index - start >= minimumLength)
                yield return Encoding.ASCII.GetString(bytes, start, index - start);
            start = -1;
        }
    }

    private static bool TryParsePointer(string? value, out ulong pointer)
    {
        pointer = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return ulong.TryParse(text, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out pointer);
    }

    private static JsonArray ToCountArray<T>(IReadOnlyDictionary<T, int> counts, string key)
        where T : notnull
    {
        var result = new JsonArray();
        foreach (var pair in counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key?.ToString(), StringComparer.Ordinal))
        {
            result.Add(new JsonObject
            {
                [key] = pair.Key.ToString(),
                ["count"] = pair.Value
            });
        }
        return result;
    }

    private static bool TryGetInt(JsonObject node, string key, out int value)
    {
        value = 0;
        try
        {
            return node[key] is JsonValue jsonValue && jsonValue.TryGetValue<int>(out value);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetUInt32(JsonObject node, string key, out uint value)
    {
        value = 0;
        var text = StringValue(node, key);
        if (text is not null)
        {
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(text[2..], NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out value);
            return uint.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }

        try
        {
            return node[key] is JsonValue jsonValue && jsonValue.TryGetValue<uint>(out value);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetUInt64(JsonObject node, string key, out ulong value)
    {
        value = 0;
        var text = StringValue(node, key);
        if (text is not null)
        {
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(text[2..], NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out value);
            return ulong.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }

        try
        {
            return node[key] is JsonValue jsonValue && jsonValue.TryGetValue<ulong>(out value);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static JsonArray ToIntArray(IEnumerable<int> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    private static HashSet<int>? ReadCatalogTypeIds(string hookDirectory, JsonObject? catalogInfo)
    {
        var file = StringValue(catalogInfo ?? [], "file");
        if (file is null)
            return null;
        var path = Path.GetFullPath(Path.Combine(
            hookDirectory, file.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path) || !Path.GetFileName(path).Contains("catalog", StringComparison.OrdinalIgnoreCase))
            return null;

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || !lines[0].StartsWith("persistentTypeID|", StringComparison.Ordinal))
            return null;

        var ids = new HashSet<int>();
        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split('|');
            if (fields.Length > 0 && int.TryParse(fields[0], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var id))
                ids.Add(id);
        }
        return ids;
    }

    private static JsonObject DecodeAttachedTypeTree(
        string hookDirectory,
        JsonObject hook,
        string? explicitPath)
    {
        var path = explicitPath is not null
            ? FullPath(explicitPath)
            : FindAttachedTypeTreeFile(hookDirectory, hook);
        if (path is null)
        {
            return new JsonObject
            {
                ["status"] = "not_available",
                ["reason"] = "no TypeTreeRipper .ttbin is attached"
            };
        }

        if (!File.Exists(path))
        {
            return new JsonObject
            {
                ["status"] = "missing",
                ["file"] = path
            };
        }

        try
        {
            var decoded = ReadTypeTreeBinary(path);
            AddDecodedTypeTreeFieldIntegrity(decoded);
            decoded["status"] = decoded["totalNodes"]?.GetValue<long>() == 0
                ? "decoded_empty"
                : "decoded";
            decoded["file"] = RelativeTo(hookDirectory, path);
            decoded["size"] = new FileInfo(path).Length;
            decoded["sha256"] = Sha256(path);
            if (decoded["status"]?.GetValue<string>() == "decoded_empty")
                decoded["reason"] = "The binary is structurally valid but contains no field nodes; it is not a complete TypeTree.";
            return decoded;
        }
        catch (InvalidDataException ex)
        {
            return new JsonObject
            {
                ["status"] = "invalid",
                ["file"] = RelativeTo(hookDirectory, path),
                ["reason"] = ex.Message
            };
        }
    }

    private static string? FindAttachedTypeTreeFile(string hookDirectory, JsonObject hook)
    {
        var candidates = new List<string>();
        AddManifestFileCandidate(candidates, hookDirectory, hook["typetree"]?.AsObject());
        if (hook["typetree"] is JsonObject typeTree && typeTree["artifacts"] is JsonArray artifacts)
        {
            foreach (var artifact in artifacts.OfType<JsonObject>())
                AddManifestFileCandidate(candidates, hookDirectory, artifact);
        }

        return candidates.FirstOrDefault(path =>
            string.Equals(Path.GetExtension(path), ".ttbin", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddManifestFileCandidate(
        ICollection<string> candidates,
        string hookDirectory,
        JsonObject? manifest)
    {
        var relativeFile = StringValue(manifest ?? [], "file");
        if (relativeFile is null)
            return;
        candidates.Add(Path.GetFullPath(Path.Combine(
            hookDirectory, relativeFile.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static JsonObject ReadTypeTreeBinary(string path)
    {
        const ulong expectedMagic = 0x4545525445505954;
        const uint expectedVersion = 1;
        const int maxStringBytes = 1024 * 1024;
        const uint maxTrees = 100_000;
        const uint maxNodesPerTree = 1_000_000;

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var magic = reader.ReadUInt64();
        if (magic != expectedMagic)
            throw new InvalidDataException($"Invalid TypeTree magic: 0x{magic:x16}");
        var version = reader.ReadUInt32();
        if (version != expectedVersion)
            throw new InvalidDataException($"Unsupported TypeTree version: {version}");

        var major = reader.ReadUInt16();
        var minor = reader.ReadByte();
        var patch = reader.ReadByte();
        var variant = ReadLengthPrefixedString(reader, maxStringBytes);
        var treeCount = reader.ReadUInt32();
        if (treeCount > maxTrees)
            throw new InvalidDataException($"TypeTree count is too large: {treeCount}");

        var trees = new JsonArray();
        var totalNodes = 0L;
        for (var treeIndex = 0u; treeIndex < treeCount; treeIndex++)
        {
            var className = ReadLengthPrefixedString(reader, maxStringBytes);
            var classNamespace = ReadLengthPrefixedString(reader, maxStringBytes);
            var module = ReadLengthPrefixedString(reader, maxStringBytes);
            var persistentTypeId = reader.ReadInt32();
            var objectSize = reader.ReadInt32();
            var rttiFlags = reader.ReadUInt32();
            var basePersistentTypeId = reader.ReadUInt32();
            var derivedFromTypeIndex = reader.ReadUInt32();
            var derivedFromDescendantCount = reader.ReadUInt32();
            var transferFlags = reader.ReadUInt64();
            var nodeCount = reader.ReadUInt32();
            if (nodeCount > maxNodesPerTree)
                throw new InvalidDataException($"TypeTree node count is too large for {className}: {nodeCount}");
            totalNodes += nodeCount;

            var nodes = new JsonArray();
            for (var nodeIndex = 0u; nodeIndex < nodeCount; nodeIndex++)
            {
                nodes.Add(new JsonObject
                {
                    ["type"] = ReadLengthPrefixedString(reader, maxStringBytes),
                    ["name"] = ReadLengthPrefixedString(reader, maxStringBytes),
                    ["flags"] = reader.ReadUInt32(),
                    ["byteSize"] = reader.ReadInt32(),
                    ["index"] = reader.ReadInt32(),
                    ["version"] = reader.ReadInt16(),
                    ["level"] = reader.ReadByte(),
                    ["metaFlags"] = reader.ReadUInt32(),
                    ["refTypeHash"] = $"0x{reader.ReadUInt64():x16}"
                });
            }

            trees.Add(new JsonObject
            {
                ["rtti"] = new JsonObject
                {
                    ["className"] = className,
                    ["classNamespace"] = classNamespace,
                    ["module"] = module,
                    ["persistentTypeId"] = persistentTypeId,
                    ["size"] = objectSize,
                    ["flags"] = rttiFlags,
                    ["basePersistentTypeId"] = basePersistentTypeId,
                    ["derivedFromTypeIndex"] = derivedFromTypeIndex,
                    ["derivedFromDescendantCount"] = derivedFromDescendantCount
                },
                ["transferFlags"] = transferFlags,
                ["nodeCount"] = nodeCount,
                ["nodes"] = nodes
            });
        }

        return new JsonObject
        {
            ["format"] = "TypeTreeRipper.ttbin",
            ["header"] = new JsonObject
            {
                ["version"] = version,
                ["revision"] = $"{major}.{minor}.{patch}",
                ["variant"] = variant
            },
            ["treeCount"] = treeCount,
            ["totalNodes"] = totalNodes,
            ["trees"] = trees
        };
    }

    private static string ReadLengthPrefixedString(BinaryReader reader, int maxBytes)
    {
        var size = reader.ReadUInt32();
        if (size > maxBytes || size > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException($"Invalid TypeTree string length: {size}");
        var bytes = reader.ReadBytes(checked((int)size));
        if (bytes.Length != size)
            throw new InvalidDataException("Unexpected end of TypeTree string");
        return Encoding.UTF8.GetString(bytes);
    }

    private static JsonObject BuildIntegrityReport(
        string hookDirectory,
        JsonObject hook,
        int recordCount,
        IReadOnlyCollection<int> missingTypeIds)
    {
        var report = new JsonObject
        {
            ["status"] = missingTypeIds.Count == 0 ? "consistent" : "type_coverage_gap",
            ["recordCount"] = recordCount,
            ["missingTypeIdCount"] = missingTypeIds.Count
        };

        var eventInfo = hook["events"]?.AsObject()
            ?? hook["typetree"]?["events"]?.AsObject();
        if (eventInfo is not null)
        {
            report["events"] = VerifyArtifact(hookDirectory, eventInfo, recordCount);
        }
        else
        {
            report["events"] = new JsonObject { ["status"] = "not_attached" };
        }

        var treeInfo = hook["typetree"]?.AsObject();
        report["typetree"] = treeInfo is null
            ? new JsonObject { ["status"] = "not_attached" }
            : VerifyArtifact(hookDirectory, treeInfo, expectedRecordCount: null);
        return report;
    }

    private static JsonObject VerifyArtifact(
        string hookDirectory,
        JsonObject info,
        int? expectedRecordCount)
    {
        var relativeFile = StringValue(info, "file");
        if (relativeFile is null)
            return new JsonObject { ["status"] = "invalid_manifest", ["reason"] = "missing file" };

        var path = Path.GetFullPath(Path.Combine(
            hookDirectory, relativeFile.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path))
            return new JsonObject { ["status"] = "missing", ["file"] = relativeFile };

        var actualSize = new FileInfo(path).Length;
        var expectedSize = info["size"]?.GetValue<long>();
        var actualHash = Sha256(path);
        var expectedHash = StringValue(info, "sha256");
        var status = (expectedSize is not null && expectedSize.Value != actualSize)
            || (expectedHash is not null && !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            ? "changed"
            : "verified";
        var result = new JsonObject
        {
            ["status"] = status,
            ["file"] = relativeFile,
            ["actualSize"] = actualSize,
            ["sha256"] = actualHash
        };
        if (expectedRecordCount is not null && info["count"] is JsonValue countValue &&
            countValue.TryGetValue<int>(out var manifestCount))
        {
            result["manifestCount"] = manifestCount;
            result["countMatchesAnalysis"] = manifestCount == expectedRecordCount;
        }
        return result;
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
        var captureSessionId = $"{sessionName}-{Guid.NewGuid():N}";
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
                hookPath,
                captureSessionId);
        }

        WriteSessionHook(hookPath, profile, sessionName, captureSessionId, targetPid, targetPath,
            module, eventsPath, "injected");

        Console.WriteLine($"已生成会话: {hookPath}");
        if (ShouldWatch(options))
            WatchSession(hookPath, eventsPath, targetPid, options);
        else
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

        FinalizeSessionFile(
            hookPath,
            eventsPath,
            ignoreIncompleteTail: false,
            options.GetValueOrDefault("typetree"));

        // Keep finalization backward-compatible, but make the evidence loop
        // reproducible when requested: the finalized hook is analyzed only
        // after its records and TypeTree references have been committed.
        if (options.ContainsKey("analyze"))
        {
            var analysisPath = options.GetValueOrDefault("analysis-output")
                ?? Path.Combine(hookDir, Path.GetFileNameWithoutExtension(hookPath) + ".analysis.json");
            var analyzeArgs = new List<string> { hookPath, "--output", analysisPath };
            if (options.GetValueOrDefault("typetree") is { } typeTreePath)
            {
                analyzeArgs.Add("--typetree");
                analyzeArgs.Add(typeTreePath);
            }
            Analyze(analyzeArgs.ToArray());
        }
        return 0;
    }

    private static int TriggerTarget(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("my-hook-tool trigger --renderdoc <renderdoccmd.exe> --ident <port> [--frames N | --query-only]");
            Console.WriteLine("  通过 RenderDoc TargetControl 查询目标 PID，或向指定端口请求一次捕获");
            return 0;
        }
        var options = ParseOptions(args);
        var renderDoc = RequiredFile(options, "renderdoc");
        var profile = LoadProfile(options.GetValueOrDefault("profile"));
        var profileId = StringValue(profile, "id") ?? "runtime";
        var triggerTool = ResolveRenderDocTriggerTool(options, profileId);
        var targetName = options.GetValueOrDefault("target");
        var targetPid = options.GetValueOrDefault("target-pid") is { } targetPidValue
            ? ParsePositiveInt(targetPidValue, "--target-pid", 0)
            : 0;
        var ident = targetName is null && targetPid == 0
            ? ParsePositiveInt(options.GetValueOrDefault("ident"), "--ident", 0)
            : 0;
        var frames = ParsePositiveInt(options.GetValueOrDefault("frames"), "--frames", 1);
        var pid = RunRenderDocTargetControl(triggerTool, renderDoc, ident,
            options.ContainsKey("query-only"), frames,
            options.GetValueOrDefault("capture-file") is { } captureValue
                ? FullPath(captureValue)
                : null,
            ParsePositiveInt(options.GetValueOrDefault("wait-seconds"), "--wait-seconds", 120),
            targetName,
            targetPid == 0 ? null : targetPid);
        if (!options.ContainsKey("query-only") &&
            options.GetValueOrDefault("hook") is { } hookValue &&
            options.GetValueOrDefault("capture-file") is { } capturedValue)
        {
            var hookPath = FullPath(hookValue);
            var capturePath = FullPath(capturedValue);
            if (!File.Exists(capturePath))
                throw new FileNotFoundException("TargetControl 已返回成功，但找不到捕获文件", capturePath);
            UpdateCaptureSessionHook(hookPath, capturePath, capturePath,
                targetName ?? (ident > 0 ? $"ident:{ident}" : null));
            Console.WriteLine($"已更新会话捕获状态: {hookPath}");
        }
        if (options.ContainsKey("query-only"))
            Console.WriteLine($"目标 PID: {pid}");
        return 0;
    }

    private static int EnrichHook(string[] args)
    {
        var options = ParseOptions(args);
        if (options.Positionals().Count != 1)
            throw new ArgumentException("enrich 需要一个 .hook 路径");

        var hookPath = FullPath(options.Positionals()[0]);
        var hook = JsonNode.Parse(File.ReadAllText(hookPath))?.AsObject()
            ?? throw new InvalidDataException(".hook 不是 JSON 对象");
        var profile = hook["profile"]?.AsObject() ?? LoadProfile(options.GetValueOrDefault("profile"));
        var hookDir = Path.GetDirectoryName(hookPath)!;
        var capturePath = ResolveHookCapturePath(hookDir, hook, options.GetValueOrDefault("capture"));
        ValidateCapture(capturePath, profile);

        if (!options.TryGetValue("event", out var eventValues) || eventValues.Count == 0)
            throw new ArgumentException("enrich 必须显式指定至少一个 --event；禁止默认整帧扫描");
        foreach (var value in eventValues)
            ParsePositiveInt(value, "--event", 0);

        var renderDoc = RequiredFile(options, "renderdoc");
        var exportDirectory = options.GetValueOrDefault("output") is { } output
            ? FullPath(output)
            : Path.Combine(hookDir, "renderdoc", "events-" + string.Join("-", eventValues));
        Directory.CreateDirectory(exportDirectory);

        var renderDocArgs = new List<string> { "shader-export", capturePath, "--output", exportDirectory };
        AddRepeated(options, "event", renderDocArgs, "--event");
        AddFlag(options, "reconstruct", renderDocArgs, "--reconstruct");
        AddValue(options, "spirv-cross", renderDocArgs, "--spirv-cross");
        AddFlag(options, "export-resources", renderDocArgs, "--export-resources");
        AddFlag(options, "emit-placeholder-templates", renderDocArgs, "--emit-placeholder-templates");

        Console.WriteLine($"按 EID 定点导出 RenderDoc 数据: {string.Join(", ", eventValues)}");
        RunProcess(renderDoc, renderDocArgs);

        var manifestPath = Path.Combine(exportDirectory, "capture.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"RenderDoc 没有生成 capture.json: {manifestPath}");
        var capture = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException("RenderDoc capture.json 不是 JSON 对象");

        var relativeExport = RelativeTo(hookDir, exportDirectory);
        var renderDocInfo = hook["renderDoc"]?.AsObject() ?? new JsonObject();
        renderDocInfo["format"] = capture["format"]?.DeepClone();
        renderDocInfo["manifest"] = RelativeTo(hookDir, manifestPath);
        renderDocInfo["sourceCapture"] = RelativeTo(hookDir, capturePath);
        renderDocInfo["summary"] = capture["summary"]?.DeepClone();
        if (capture["drawReplayEvidence"] is JsonValue evidenceName &&
            !string.IsNullOrWhiteSpace(evidenceName.GetValue<string>()))
        {
            var evidencePath = Path.Combine(exportDirectory, evidenceName.GetValue<string>());
            if (File.Exists(evidencePath))
                renderDocInfo["drawReplayEvidence"] = RelativeTo(hookDir, evidencePath);
        }
        renderDocInfo["mode"] = "selected-events";
        renderDocInfo["selectedEvents"] = new JsonArray(eventValues.Select(value => JsonValue.Create(value)).ToArray());
        renderDocInfo["exportDirectory"] = relativeExport;
        renderDocInfo["resourceSnapshots"] = capture["reconstruction"]?["resourceSnapshots"]?.DeepClone() ?? false;
        hook["renderDoc"] = renderDocInfo;
        hook["artifacts"] = BuildArtifacts(capture);

        var unresolved = hook["unresolved"]?.AsArray() ?? new JsonArray();
        RemoveUnresolved(unresolved, "capture-shader-export");
        if (capture["shaders"] is JsonArray shaders && shaders.Count > 0)
        {
            unresolved.Add(new JsonObject
            {
                ["kind"] = "capture-shader-export",
                ["status"] = "attached",
                ["reason"] = "Shader/variant identity is capture-derived; Unity material and original asset path still require explicit AS/runtime alignment."
            });
        }
        hook["unresolved"] = unresolved;

        var diagnostics = hook["diagnostics"]?.AsArray() ?? new JsonArray();
        diagnostics.Add(new JsonObject
        {
            ["level"] = "info",
            ["code"] = "capture_shader_export_attached",
            ["events"] = new JsonArray(eventValues.Select(value => JsonValue.Create(value)).ToArray()),
            ["uniqueShaders"] = capture["summary"]?["uniqueShaders"]?.DeepClone(),
            ["uniqueVariants"] = capture["summary"]?["uniqueVariants"]?.DeepClone(),
            ["note"] = "导出仅处理显式 EID；未执行整帧资源扫描。"
        });
        hook["diagnostics"] = diagnostics;
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));

        Console.WriteLine($"已将定点 Shader 数据挂入: {hookPath}");
        PrintHookSummary(hook);
        return 0;
    }

    private static string ResolveHookCapturePath(
        string hookDirectory,
        JsonObject hook,
        string? explicitCapture)
    {
        if (!string.IsNullOrWhiteSpace(explicitCapture))
            return FullPath(explicitCapture);

        var relative = StringValue(hook["session"]?.AsObject() ?? [], "captureFile")
            ?? StringValue(hook["renderDoc"]?.AsObject() ?? [], "sourceCapture")
            ?? StringValue(hook["source"]?.AsObject() ?? [], "capture");
        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException(".hook 没有 captureFile/sourceCapture；请使用 --capture 指定捕获文件");

        var candidate = Path.GetFullPath(Path.Combine(
            hookDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(candidate))
            throw new FileNotFoundException(".hook 指向的捕获文件不存在，请使用 --capture 指定实际文件", candidate);
        return candidate;
    }

    private static void RemoveUnresolved(JsonArray unresolved, string kind)
    {
        for (var index = unresolved.Count - 1; index >= 0; index--)
        {
            if (unresolved[index] is JsonObject item &&
                string.Equals(StringValue(item, "kind"), kind, StringComparison.Ordinal))
                unresolved.RemoveAt(index);
        }
    }

    private static void FinalizeSessionFile(
        string hookPath,
        string eventsPath,
        bool ignoreIncompleteTail,
        string? typeTreePath)
    {
        var hook = JsonNode.Parse(File.ReadAllText(hookPath))?.AsObject()
            ?? throw new InvalidDataException(".hook 不是 JSON 对象");
        var hookDir = Path.GetDirectoryName(hookPath)!;
        var records = ReadEventRecords(eventsPath, ignoreIncompleteTail);
        hook["records"] = records;
        RefreshEventManifest(hook, hookDir, eventsPath, records.Count);
        if (typeTreePath is not null)
            hook["typetree"] = CopyTypeTree(hookDir, typeTreePath);
        var session = hook["session"]?.AsObject() ?? new JsonObject();
        session["status"] = records.Count == 0 ? "no_records" : "completed";
        session["finishedUtc"] = DateTime.UtcNow.ToString("O");
        hook["session"] = session;
        RefreshRuntimeIdentityEvidence(hook);
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));
        Console.WriteLine($"已合并运行时记录: {records.Count}");
        PrintHookSummary(hook);
    }

    private static void RefreshRuntimeIdentityEvidence(JsonObject hook)
    {
        var records = hook["records"] as JsonArray ?? new JsonArray();
        var runtimeRecords = records.OfType<JsonObject>().ToArray();
        var configuredPPtrRecords = runtimeRecords
            .Where(record => record["configuredPPtrToLookupCallChain"] is JsonObject chain
                && string.Equals(StringValue(chain, "status"), "matched", StringComparison.Ordinal))
            .ToArray();
        var typedCorrelationRecords = runtimeRecords
            .Where(record => record["pptrToTypedObjectCorrelation"] is JsonObject)
            .ToArray();
        var resolverRecords = runtimeRecords
            .Where(record => StringValue(record, "kind") == "unity.serialized-object-resolve-candidate")
            .ToArray();
        var resolverTypedRecords = resolverRecords
            .Where(record => record["returnTypeObserved"] is JsonValue observed
                && observed.TryGetValue<bool>(out var value)
                && value
                && record["returnVtableObservedType"] is JsonObject)
            .ToArray();
        var resolverTypeCounts = new JsonObject();
        var resolverEventSequences = new JsonArray();
        foreach (var record in resolverTypedRecords)
        {
            var observedType = record["returnVtableObservedType"] as JsonObject;
            var className = StringValue(observedType ?? [], "className") ?? "<unknown>";
            resolverTypeCounts[className] = Int64Value(resolverTypeCounts, className) + 1;
            if (record["eventSequence"] is JsonNode eventSequence)
                resolverEventSequences.Add(eventSequence.DeepClone());
        }
        var returnTypeObservedCount = typedCorrelationRecords.Count(record =>
            record["pptrToTypedObjectCorrelation"] is JsonObject correlation
            && correlation["returnTypeObserved"] is JsonValue observed
            && observed.TryGetValue<bool>(out var value)
            && value);
        var exactMaterialShaderBindingCount = typedCorrelationRecords.Count(record =>
            record["pptrToTypedObjectCorrelation"] is JsonObject correlation
            && correlation["exactMaterialShaderFieldBinding"] is JsonValue binding
            && binding.TryGetValue<bool>(out var value)
            && value);
        var transferInvocationTokenCount = runtimeRecords.Count(record =>
            record["transferInvocationId"] is JsonValue);
        var fieldInvocationTokenCount = runtimeRecords.Count(record =>
            record["activeTransferCallChain"] is JsonObject chain
            && chain["invocationId"] is JsonValue);
        var resolverInvocationTokenCount = resolverRecords.Count(record =>
            record["activeTransferCallChain"] is JsonObject chain
            && chain["invocationId"] is JsonValue);
        var configuredPPtrEventSequences = new JsonArray();
        foreach (var record in configuredPPtrRecords)
        {
            if (record["eventSequence"] is JsonNode eventSequence)
                configuredPPtrEventSequences.Add(eventSequence.DeepClone());
        }
        hook["runtimeEvidence"] = new JsonObject
        {
            ["schema"] = "my-hook.runtime-identity-evidence.v1",
            ["runtimeCallChain"] = new JsonObject
            {
                ["status"] = configuredPPtrRecords.Length > 0 ? "observed" : "not_observed",
                ["matchedCount"] = configuredPPtrRecords.Length,
                ["eventSequences"] = configuredPPtrEventSequences,
                ["meaning"] = "same-thread PPtr output to lookup-key dataflow with statically verified lookup branch; not a Material/Shader or RenderDoc EID identity"
            },
            ["typedObjectCorrelation"] = new JsonObject
            {
                ["status"] = returnTypeObservedCount > 0 ? "observed" : "not_observed",
                ["recordCount"] = typedCorrelationRecords.Length,
                ["returnTypeObservedCount"] = returnTypeObservedCount,
                ["exactMaterialShaderFieldBindingCount"] = exactMaterialShaderBindingCount
            },
            ["resolverTypeObservation"] = new JsonObject
            {
                ["status"] = resolverTypedRecords.Length > 0 ? "observed" : "not_observed",
                ["resolverCandidateCount"] = resolverRecords.Length,
                ["typedReturnCount"] = resolverTypedRecords.Length,
                ["classNameCounts"] = resolverTypeCounts,
                ["eventSequences"] = resolverEventSequences,
                ["meaning"] = "the resolver returned an object whose runtime vtable was mapped to a TypeTree type in the same event; this is not a Material field or sourceFile#PathID identity"
            },
            ["invocationTokenEvidence"] = new JsonObject
            {
                ["status"] = fieldInvocationTokenCount > 0 ? "observed" : "not_observed",
                ["transferRecordCount"] = transferInvocationTokenCount,
                ["fieldRecordCount"] = fieldInvocationTokenCount,
                ["resolverRecordCount"] = resolverInvocationTokenCount,
                ["meaning"] = "a token is accepted only when emitted by the same real Transfer call chain; thread, order, address and opaque values are not substituted"
            },
            ["crossSourceIdentity"] = new JsonObject
            {
                ["status"] = "not_observed",
                ["materialToShader"] = false,
                ["sourceFilePathId"] = false,
                ["renderDocEid"] = false
            }
        };
    }

    private static JsonArray ReadEventRecords(string eventsPath, bool ignoreIncompleteTail)
    {
        var records = new JsonArray();
        var lines = File.ReadAllLines(eventsPath);
        var duplicatePropertyLines = 0;
        var salvagedRecords = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using (var document = JsonDocument.Parse(line))
                {
                    if (HasDuplicateJsonProperties(document.RootElement))
                    {
                        duplicatePropertyLines++;
                        continue;
                    }
                }
                var record = JsonNode.Parse(line)?.AsObject()
                    ?? throw new InvalidDataException("运行时事件不是 JSON 对象");
                records.Add(record);
            }
            catch (JsonException)
            {
                var recovered = AddConcatenatedRuntimeEvents(
                    line, records, ref duplicatePropertyLines);
                if (recovered > 0)
                {
                    salvagedRecords += recovered;
                    continue;
                }
                if (ignoreIncompleteTail && index == lines.Length - 1)
                {
                    Console.WriteLine("忽略事件文件末尾未完成的 JSON 行。");
                    continue;
                }
                throw;
            }
        }
        if (duplicatePropertyLines > 0)
            Console.WriteLine($"隔离含重复 JSON 属性的运行时事件：{duplicatePropertyLines} 行；原始文件未修改。");
        if (salvagedRecords > 0)
            Console.WriteLine($"从并发追加造成的拼接 JSON 行中恢复运行时事件：{salvagedRecords} 条；原始文件未修改。");
        return records;
    }

    private static int AddConcatenatedRuntimeEvents(
        string line, JsonArray records, ref int duplicatePropertyLines)
    {
        const string marker = "{\"format\":\"my-hook.runtime-event.v1\"";
        var starts = new List<int>();
        var offset = 0;
        while ((offset = line.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            starts.Add(offset);
            offset += marker.Length;
        }
        if (starts.Count < 2)
            return 0;

        var recovered = 0;
        foreach (var start in starts)
        {
            var end = line.Length;
            foreach (var next in starts)
            {
                if (next > start)
                {
                    end = next;
                    break;
                }
            }
            var candidate = line[start..end].Trim();
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (HasDuplicateJsonProperties(document.RootElement))
                {
                    duplicatePropertyLines++;
                    continue;
                }
                var record = JsonNode.Parse(candidate)?.AsObject();
                if (record is null)
                    continue;
                records.Add(record);
                recovered++;
            }
            catch (JsonException)
            {
                // The prefix object may be torn by concurrent writers. Keep
                // the original line intact and salvage only complete objects.
            }
        }
        return recovered;
    }

    private static bool HasDuplicateJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateJsonProperties(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateJsonProperties(item))
                    return true;
            }
        }
        return false;
    }

    private static void RefreshEventManifest(
        JsonObject hook,
        string hookDirectory,
        string eventsPath,
        int recordCount)
    {
        var eventInfo = hook["events"]?.AsObject();
        if (eventInfo is null && hook["typetree"] is JsonObject typeTree)
            eventInfo = typeTree["events"]?.AsObject();
        if (eventInfo is null)
            return;

        eventInfo["status"] = recordCount == 0 ? "empty" : "attached";
        eventInfo["file"] = RelativeTo(hookDirectory, eventsPath);
        eventInfo["size"] = new FileInfo(eventsPath).Length;
        eventInfo["sha256"] = Sha256(eventsPath);
        eventInfo["count"] = recordCount;

        // AttachGuestArtifact stores a second copy below typetree. Keep both
        // manifests synchronized when a session is finalized offline.
        if (hook["typetree"] is JsonObject nestedTypeTree &&
            nestedTypeTree["events"] is JsonObject nestedEvents &&
            !ReferenceEquals(nestedEvents, eventInfo))
        {
            nestedEvents["status"] = eventInfo["status"]?.DeepClone();
            nestedEvents["file"] = eventInfo["file"]?.DeepClone();
            nestedEvents["size"] = eventInfo["size"]?.DeepClone();
            nestedEvents["sha256"] = eventInfo["sha256"]?.DeepClone();
            nestedEvents["count"] = eventInfo["count"]?.DeepClone();
        }
    }

    private static void WatchSession(
        string hookPath,
        string eventsPath,
        int targetPid,
        Dictionary<string, List<string>> options)
    {
        var stopRequested = false;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopRequested = true;
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var duration = ParseDuration(options.GetValueOrDefault("duration-seconds"));
            DateTime? deadline = duration is null ? null : DateTime.UtcNow.Add(duration.Value);
            Console.WriteLine("运行时采集已启动：只追加新事件，不重复扫描；按 Ctrl+C 完成会话。 ");
            while (!stopRequested && IsProcessRunning(targetPid) &&
                   (deadline is null || DateTime.UtcNow < deadline.Value))
            {
                Thread.Sleep(250);
            }
            if (deadline is not null && DateTime.UtcNow >= deadline.Value)
                Console.WriteLine($"已达到采集时长 {duration?.TotalSeconds:0} 秒，自动完成会话。");
            else if (!stopRequested && !IsProcessRunning(targetPid))
                Console.WriteLine("目标进程已退出，自动完成会话。");
            FinalizeSessionFile(hookPath, eventsPath, ignoreIncompleteTail: true, typeTreePath: null);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static TimeSpan? ParseDuration(string? value)
    {
        if (value is null) return null;
        if (!int.TryParse(value, out var seconds) || seconds <= 0)
            throw new ArgumentException("--duration-seconds 必须是正整数");
        return TimeSpan.FromSeconds(seconds);
    }

    private static int ParsePositiveInt(string? value, string option, int defaultValue)
    {
        if (value is null)
            return defaultValue;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ||
            result <= 0)
            throw new ArgumentException($"{option} 必须是正整数");
        return result;
    }

    private static int ParseNonNegativeInt(string? value, string option, int defaultValue)
    {
        if (value is null)
            return defaultValue;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ||
            result < 0)
            throw new ArgumentException($"{option} 必须是非负整数");
        return result;
    }

    private static bool ShouldWatch(Dictionary<string, List<string>> options) =>
        options.ContainsKey("watch") || options.ContainsKey("duration-seconds");

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static JsonObject CopyTypeTree(string hookDir, string sourceValue)
    {
        var source = FullPath(sourceValue);
        if (!File.Exists(source))
            throw new FileNotFoundException("找不到 TypeTree 文件", source);
        var extension = Path.GetExtension(source);
        var accepted = new[] { ".ttbin", ".tpk", ".txt", ".json" };
        if (!accepted.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"不支持的 TypeTree 文件扩展名: {extension}");

        var targetDirectory = Path.Combine(hookDir, "runtime", "typetree");
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, Path.GetFileName(source));
        if (!string.Equals(source, target, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal))
        {
            File.Copy(source, target, true);
        }
        if (extension.Equals(".ttbin", StringComparison.OrdinalIgnoreCase))
            return BuildTypeTreeArtifactInfo(hookDir, target);

        return new JsonObject
        {
            ["status"] = "attached",
            ["format"] = extension.Equals(".ttbin", StringComparison.OrdinalIgnoreCase)
                ? "TypeTreeRipper.ttbin"
                : extension.TrimStart('.'),
            ["file"] = RelativeTo(hookDir, target),
            ["sourceFile"] = Path.GetFileName(source),
            ["size"] = new FileInfo(target).Length,
            ["sha256"] = Sha256(target)
        };
    }

    private static int AttachMumu(string[] args)
    {
        var options = ParseOptions(args);
        if (options.ContainsKey("help") || options.ContainsKey("?"))
        {
            PrintUsage();
            return 0;
        }
        var profilePath = options.GetValueOrDefault("profile");
        var profile = LoadProfile(profilePath);
        ValidateMumuProfile(profile, profilePath);
        var guestRipper = options.ContainsKey("no-guest-ripper")
            ? null
            : options.GetValueOrDefault("guest-ripper") is { } guestRipperValue
            ? RequiredFile(guestRipperValue, "guest-ripper")
            : ResolveBundledGuestRipper(profile, profilePath);
        var guestArm64Ripper = ResolveOptionalBundledGuestRipper(
            profile, profilePath, "guestArm64RipperPath");
        var module = options.GetValueOrDefault("module") is { } moduleValue
            ? RequiredFile(moduleValue, "module")
            : null;
        var renderDoc = options.GetValueOrDefault("renderdoc") is { } renderDocValue
            ? RequiredFile(renderDocValue, "renderdoc")
            : null;
        var reuseMumu = options.ContainsKey("reuse-mumu");
        if (reuseMumu && renderDoc is not null)
            throw new ArgumentException("--reuse-mumu 不能与 --renderdoc 同时使用；RenderDoc 必须在宿主启动前注入。请使用默认启动流程。 ");
        var mumuRoot = ResolveMumuRoot(RequiredValue(options, "mumu-root"));
        var vmIndex = options.GetValueOrDefault("vmindex") ?? "0";
        if (!int.TryParse(vmIndex, out var parsedVmIndex) || parsedVmIndex < 0)
            throw new ArgumentException("--vmindex 必须是非负整数");
        var androidVersion = ResolveMumuAndroidVersion(profile, options);
        var mumuCli = Path.Combine(mumuRoot, "nx_main", "mumu-cli.exe");
        var mumuHost = Path.Combine(mumuRoot, "nx_main", "MuMuNxMain.exe");
        if (!File.Exists(mumuCli) || !File.Exists(mumuHost))
            throw new FileNotFoundException("MUMU 安装不完整，需要 nx_main\\mumu-cli.exe 和 MuMuNxMain.exe", mumuRoot);

        if (guestRipper is not null)
        {
            ValidateGuestTimingOptions(
                options.GetValueOrDefault("guest-timeout-seconds"),
                options.GetValueOrDefault("guest-event-seconds"),
                options.GetValueOrDefault("guest-event-max-seconds")
                    ?? StringValue(profile, "guestEventMaxSeconds"),
                options.GetValueOrDefault("guest-event-idle-seconds")
                    ?? StringValue(profile, "guestEventIdleSeconds"),
                options.GetValueOrDefault("guest-dump-delay-seconds"));
            var preflightClassIds = options.GetValueOrDefault("guest-class-ids");
            if (preflightClassIds is null)
            {
                preflightClassIds = options.GetValueOrDefault("vtable-probe") == "1"
                    ? StringValue(profile, "vtableProbeClassIds") ?? "21"
                    : StringValue(profile, "observeClassIds") ?? "21";
            }
            ValidateNativeExperimentOptions(
                options.GetValueOrDefault("enable-native-transfer"),
                options.GetValueOrDefault("unsafe-experiment"),
                preflightClassIds,
                options.GetValueOrDefault("vtable-offset")
                    ?? StringValue(profile, "nativeVtableOffset"),
                options.GetValueOrDefault("vtable-probe") ?? StringValue(profile, "vtableProbe"));
        }

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
        var captureSessionId = $"{sessionName}-{Guid.NewGuid():N}";
        var captureFile = options.GetValueOrDefault("capture-file") is { } captureFileValue
            ? FullPath(captureFileValue)
            : Path.Combine(sessionDir, "capture.srrdc");
        if (renderDoc is not null)
        {
            ConfigureRenderDocVulkanLayers(renderDoc);
            Directory.CreateDirectory(Path.GetDirectoryName(captureFile)!);
        }

        if (reuseMumu)
        {
            Console.WriteLine(guestRipper is null
                ? "复用已运行的 MUMU，不重启实例、不重置 Android 场景..."
                : "复用已运行的 MUMU VM；为通过 LD_PRELOAD 加载客体 Ripper，将重启目标 Android 游戏进程...");
        }
        else
        {
            Console.WriteLine("停止指定 MUMU 实例，避免桥接模块加载过晚...");
            RunProcess(mumuCli, new[] { "control", "--vmindex", parsedVmIndex.ToString(), "shutdown" });
            // Keep MuMu's manager alive while the asynchronous VM shutdown is
            // observed. Closing it immediately can strand the VM in stopping.
            WaitForMumuStopped(mumuCli, parsedVmIndex, 60);
            RunProcess(mumuCli, new[] { "main", "close" });
        }

        int targetPid;
        var targetControlIdent = 0;
        if (reuseMumu)
        {
            targetPid = FindRunningMumuHost(mumuHost);
            if (module is not null)
            {
                Console.WriteLine($"向已运行的 MUMU 宿主注入桥接模块，PID {targetPid}...");
                SetRemoteHookEnvironmentAndInject(targetPid, module, new Dictionary<string, string>
                {
                    ["MY_HOOK_PROFILE"] = profileId,
                    ["MY_HOOK_OUTPUT"] = runtimeDir,
                    ["MY_HOOK_EVENTS"] = eventsPath,
                    ["MY_HOOK_FILE"] = hookPath,
                    ["MY_HOOK_CAPTURE_SESSION_ID"] = captureSessionId
                });
            }
            WriteSessionHook(hookPath, profile, sessionName, captureSessionId, targetPid, mumuHost,
                module ?? "observer-only", eventsPath,
                module is null ? "reused_observer_only" : "reused_injected");
        }
        else if (renderDoc is not null)
        {
            targetControlIdent = StartRenderDocCapture(renderDoc, mumuHost, Path.Combine(mumuRoot, "nx_main"),
                captureFile, profileId, profile, eventsPath, hookPath, captureSessionId, options);
            targetPid = 0;
            if (module is not null || options.ContainsKey("auto-trigger"))
            {
                var triggerTool = ResolveRenderDocTriggerTool(options, profileId);
                targetPid = RunRenderDocTargetControl(
                    triggerTool, renderDoc, targetControlIdent, queryOnly: true, frames: 1);
            }
            if (module is not null)
            {
                Console.WriteLine("RenderDoc 宿主已启动，加载运行时桥接模块...");
                InjectIntoProcess(targetPid, module);
            }
            WriteSessionHook(hookPath, profile, sessionName, captureSessionId, targetPid, mumuHost,
                module ?? "renderdoc-only", eventsPath,
                module is null ? "renderdoc_launched" : "injected", targetControlIdent, captureFile);
        }
        else if (module is not null)
        {
            targetPid = LaunchSuspendedAndInject(
                mumuHost,
                string.Empty,
                Path.Combine(mumuRoot, "nx_main"),
                module,
                profileId,
                eventsPath,
                hookPath,
                captureSessionId);
            WriteSessionHook(hookPath, profile, sessionName, captureSessionId, targetPid, mumuHost,
                module, eventsPath, "injected");
        }
        else
        {
            targetPid = StartMumuHost(mumuHost, mumuRoot);
            WriteSessionHook(hookPath, profile, sessionName, captureSessionId, targetPid, mumuHost,
                "guest-wrap:pending", eventsPath, "guest_waiting");
        }

        if (!reuseMumu)
        {
            Console.WriteLine("启动指定 MUMU 实例...");
            RunProcess(mumuCli, new[]
            {
                "control", "--vmindex", parsedVmIndex.ToString(),
                "--version", androidVersion, "launch"
            });
        }
        var packageName = options.GetValueOrDefault("launch-package")
            ?? (guestRipper is not null ? StringValue(profile, "androidPackage") : null);
        if (guestRipper is not null && string.IsNullOrWhiteSpace(packageName))
            throw new ArgumentException("客体 Ripper 模式需要 --launch-package，或在 Profile 中配置 androidPackage");
        // CaptureGuestTypeTree owns the wrapped launch. Starting the package
        // here would create an uninstrumented process before wrap.<package>
        if (packageName is not null && guestRipper is null && !reuseMumu)
            LaunchMumuPackage(mumuCli, parsedVmIndex, packageName, options.GetValueOrDefault("launch-timeout-seconds"));

        if (guestRipper is not null)
        {
            var guestClassIds = options.GetValueOrDefault("guest-class-ids");
            if (guestClassIds is null)
            {
                guestClassIds = options.GetValueOrDefault("vtable-probe") == "1"
                    ? StringValue(profile, "vtableProbeClassIds") ?? "21"
                    : StringValue(profile, "observeClassIds");
            }
            var guestArtifact = CaptureGuestTypeTree(
                mumuRoot,
                options.GetValueOrDefault("adb-serial")
                    ?? Environment.GetEnvironmentVariable("MY_HOOK_ADB_SERIAL")
                    ?? ResolveMumuAdbSerial(
                        mumuCli,
                        Path.Combine(mumuRoot, "nx_main", "adb.exe"),
                        parsedVmIndex),
                packageName!,
                guestRipper,
                guestArm64Ripper,
                runtimeDir,
                options.GetValueOrDefault("guest-timeout-seconds"),
                options.GetValueOrDefault("guest-event-seconds"),
                options.GetValueOrDefault("guest-event-max-seconds")
                    ?? StringValue(profile, "guestEventMaxSeconds"),
                options.GetValueOrDefault("guest-event-idle-seconds")
                    ?? StringValue(profile, "guestEventIdleSeconds"),
                options.GetValueOrDefault("guest-total-timeout-seconds")
                    ?? StringValue(profile, "guestTotalTimeoutSeconds"),
                options.GetValueOrDefault("observe-transfers"),
                guestClassIds,
                options.GetValueOrDefault("guest-dump-delay-seconds"),
                options.GetValueOrDefault("observe-vtable-offset"),
                options.GetValueOrDefault("vtable-probe")
                    ?? StringValue(profile, "vtableProbe"),
                options.GetValueOrDefault("max-observer-targets"),
                CsvValue(profile, "factorySkipClassIds"),
                StringValue(profile, "observeVtableOffset"),
                options.GetValueOrDefault("observe-vtable-offsets")
                    ?? StringValue(profile, "observeVtableOffsets"),
                options.GetValueOrDefault("enable-native-transfer"),
                options.GetValueOrDefault("unsafe-experiment"),
                options.GetValueOrDefault("native-layout"),
                options.GetValueOrDefault("vtable-offset")
                    ?? StringValue(profile, "nativeVtableOffset"),
                options.GetValueOrDefault("direct"),
                options.GetValueOrDefault("direct-rva"),
                options.GetValueOrDefault("native-on-transfer"),
                options.GetValueOrDefault("layout-evidence")
                    ?? (IsEnabled(profile, "layoutEvidence") ? "1" : "0"),
                options.GetValueOrDefault("context-observer")
                    ?? (IsEnabled(profile, "contextObserver") ? "1" : "0"),
                options.GetValueOrDefault("context-observer-all")
                    ?? (IsEnabled(profile, "contextObserverAll") ? "1" : "0"),
                options.GetValueOrDefault("transfer-vtable-rva"),
                options.GetValueOrDefault("capture-transfer-words")
                    ?? (IsEnabled(profile, "captureTransferWords") ? "1" : "0"),
                options.GetValueOrDefault("guest-snapshot-bytes")
                    ?? StringValue(profile, "snapshotBytes"),
                options.GetValueOrDefault("guest-snapshot-class-ids")
                    ?? CsvValue(profile, "snapshotClassIds"),
                captureSessionId,
                ResolveProfilePath(profile["typeTreeBaseline"]?.AsObject(), "file"),
                profile["typeTreeBaseline"]?.AsObject(),
                StringValue(profile, "version"),
                StringValue(profile, "scanReadonly"),
                StringValue(profile, "scanStep"),
                options.GetValueOrDefault("typetree-compare-observer")
                    ?? (IsEnabled(profile, "typeTreeCompareObserver") ? "1" : "0"),
                options.GetValueOrDefault("typetree-compare-rva")
                    ?? StringValue(profile, "typeTreeCompareRva"),
                options.GetValueOrDefault("typetree-constructor-observer")
                    ?? (IsEnabled(profile, "typeTreeConstructorObserver") ? "1" : "0"),
                options.GetValueOrDefault("typetree-constructor-rva")
                    ?? StringValue(profile, "typeTreeConstructorRva"),
                options.GetValueOrDefault("typetree-constructor-first-instruction")
                    ?? StringValue(profile, "typeTreeConstructorFirstInstruction"),
                options.GetValueOrDefault("typetree-constructor-target-architecture")
                    ?? StringValue(profile, "typeTreeConstructorTargetArchitecture"),
                options.GetValueOrDefault("serialized-identity-observer")
                    ?? (IsEnabled(profile, "serializedIdentityObserver") ? "1" : "0"),
                options.GetValueOrDefault("serialized-identity-rva")
                    ?? StringValue(profile, "serializedIdentityRva"),
                options.GetValueOrDefault("serialized-identity-first-instruction")
                    ?? StringValue(profile, "serializedIdentityFirstInstruction"),
                StringValue(profile, "serializedIdentityTargetArchitecture"),
                options.GetValueOrDefault("serialized-identity-guest-patch")
                    ?? StringValue(profile, "serializedIdentityGuestPatch"),
                options.GetValueOrDefault("serialized-identity-caller-patch")
                    ?? StringValue(profile, "serializedIdentityCallerPatch"),
                options.GetValueOrDefault("serialized-identity-producer-observer")
                    ?? (IsEnabled(profile, "serializedIdentityProducerObserver") ? "1" : "0"),
                options.GetValueOrDefault("serialized-identity-producer-rva")
                    ?? StringValue(profile, "serializedIdentityProducerRva"),
                options.GetValueOrDefault("serialized-identity-producer-first-instruction")
                    ?? StringValue(profile, "serializedIdentityProducerFirstInstruction"),
                options.GetValueOrDefault("serialized-identity-producer-target-architecture")
                    ?? StringValue(profile, "serializedIdentityProducerTargetArchitecture"),
                options.GetValueOrDefault("serialized-identity-resolve-observer")
                    ?? (IsEnabled(profile, "serializedIdentityResolveObserver") ? "1" : "0"),
                options.GetValueOrDefault("serialized-identity-resolve-rva")
                    ?? StringValue(profile, "serializedIdentityResolveRva"),
                options.GetValueOrDefault("serialized-identity-resolve-first-instruction")
                    ?? StringValue(profile, "serializedIdentityResolveFirstInstruction"),
                options.GetValueOrDefault("serialized-identity-resolve-runtime-first-instruction")
                    ?? StringValue(profile, "serializedIdentityResolveRuntimeFirstInstruction"),
                options.GetValueOrDefault("serialized-identity-resolve-target-architecture")
                    ?? StringValue(profile, "serializedIdentityResolveTargetArchitecture"),
                options.GetValueOrDefault("serialized-pptr-observer")
                    ?? (IsEnabled(profile, "serializedPPtrObserver") ? "1" : "0"),
                options.GetValueOrDefault("serialized-pptr-rva")
                    ?? StringValue(profile, "serializedPPtrRva"),
                options.GetValueOrDefault("serialized-pptr-first-instruction")
                    ?? StringValue(profile, "serializedPPtrFirstInstruction"),
                options.GetValueOrDefault("serialized-pptr-target-architecture")
                    ?? StringValue(profile, "serializedPPtrTargetArchitecture"),
                options.GetValueOrDefault("serialized-pptr-guest-patch")
                    ?? StringValue(profile, "serializedPPtrGuestPatch"),
                options.GetValueOrDefault("hsr45-opaque-callsite-observer")
                    ?? (IsEnabled(profile, "hsr45OpaqueCallsiteObserver") ? "1" : "0"),
                options.GetValueOrDefault("hsr45-opaque-callsite-probe-mask"),
                options.GetValueOrDefault("serialized-object-load-boundary-observer")
                    ?? (IsEnabled(profile, "serializedObjectLoadBoundaryObserver") ? "1" : "0"),
                options.GetValueOrDefault("serialized-object-load-boundary-rva")
                    ?? StringValue(profile, "serializedObjectLoadBoundaryRva"),
                options.GetValueOrDefault("serialized-object-load-boundary-first-instruction")
                    ?? StringValue(profile, "serializedObjectLoadBoundaryFirstInstruction"),
                options.GetValueOrDefault("serialized-object-load-boundary-target-architecture")
                    ?? StringValue(profile, "serializedObjectLoadBoundaryTargetArchitecture"),
                options.GetValueOrDefault("serialized-object-consumer-observer")
                    ?? (IsEnabled(profile, "serializedObjectConsumerObserver") ? "1" : "0"),
                options.GetValueOrDefault("serialized-object-consumer-rva")
                    ?? StringValue(profile, "serializedObjectConsumerRva"),
                options.GetValueOrDefault("serialized-object-consumer-first-instruction")
                    ?? StringValue(profile, "serializedObjectConsumerFirstInstruction"),
                options.GetValueOrDefault("serialized-object-consumer-target-architecture")
                    ?? StringValue(profile, "serializedObjectConsumerTargetArchitecture"),
                options.GetValueOrDefault("serialized-object-consumer-caller-rva")
                    ?? StringValue(profile, "serializedObjectConsumerCallerRva"),
                options.GetValueOrDefault("serialized-object-lookup-observer")
                    ?? StringValue(profile, "serializedObjectLookupObserver"),
                options.GetValueOrDefault("serialized-object-lookup-rva")
                    ?? StringValue(profile, "serializedObjectLookupRva"),
                options.GetValueOrDefault("serialized-object-lookup-first-instruction")
                    ?? StringValue(profile, "serializedObjectLookupFirstInstruction"),
                options.GetValueOrDefault("serialized-object-lookup-target-architecture")
                    ?? StringValue(profile, "serializedObjectLookupTargetArchitecture"),
                options.GetValueOrDefault("serialized-object-resolve-candidate-observer")
                    ?? (IsEnabled(profile, "serializedObjectResolveCandidateObserver") ? "1" : "0"),
                options.GetValueOrDefault("serialized-object-resolve-candidate-rva")
                    ?? StringValue(profile, "serializedObjectResolveCandidateRva"),
                options.GetValueOrDefault("serialized-object-resolve-candidate-first-instruction")
                    ?? StringValue(profile, "serializedObjectResolveCandidateFirstInstruction"),
                options.GetValueOrDefault("serialized-object-resolve-candidate-target-architecture")
                    ?? StringValue(profile, "serializedObjectResolveCandidateTargetArchitecture"),
                options.GetValueOrDefault("serialized-object-resolve-candidate-pptr-caller-rva")
                    ?? StringValue(profile, "serializedObjectResolveCandidatePPtrCallerRvas"),
                options.GetValueOrDefault("serialized-object-resolve-candidate-pptr-to-resolve-caller-delta")
                    ?? StringValue(profile, "serializedObjectResolveCandidatePPtrToResolveCallerDelta"),
                options.GetValueOrDefault("serialized-object-resolve-candidate-pptr-nested-resolve-caller-rva")
                    ?? StringValue(profile, "serializedObjectResolveCandidatePPtrNestedResolveCallerRva"),
                options.GetValueOrDefault("serialized-field-read-observer")
                    ?? StringValue(profile, "serializedFieldReadObserver"),
                options.GetValueOrDefault("serialized-field-read-rva")
                    ?? StringValue(profile, "serializedFieldReadRva"),
                options.GetValueOrDefault("serialized-field-read-first-instruction")
                    ?? StringValue(profile, "serializedFieldReadFirstInstruction"),
                options.GetValueOrDefault("serialized-field-read-target-architecture")
                    ?? StringValue(profile, "serializedFieldReadTargetArchitecture"),
                options.GetValueOrDefault("wait-unity-runtime")
                    ?? (IsEnabled(profile, "waitForUnityRuntime") ? "1" : "0"),
                options.GetValueOrDefault("auto-enter")
                    ?? (IsEnabled(profile, "autoEnter") ? "1" : "0"),
                options.GetValueOrDefault("auto-enter-delay-seconds")
                    ?? StringValue(profile, "autoEnterDelaySeconds"),
                options.GetValueOrDefault("auto-enter-tap-x")
                    ?? StringValue(profile, "autoEnterTapX"),
                options.GetValueOrDefault("auto-enter-tap-y")
                    ?? StringValue(profile, "autoEnterTapY"),
                options.GetValueOrDefault("auto-enter-max-attempts")
                    ?? StringValue(profile, "autoEnterMaxAttempts"),
                options.GetValueOrDefault("auto-enter-retry-interval-seconds")
                    ?? StringValue(profile, "autoEnterRetryIntervalSeconds"),
                IsEnabled(profile, "captureTransferCallers")
                    || Environment.GetEnvironmentVariable("SR_TYPETREE_CAPTURE_CALLERS") == "1",
                requireCatalogOnly: StringValue(profile, "experimentalScope") is "read-only-runtime-catalog" or "passive-factory-entry",
                factoryEntryProbe: profile["factoryEntryProbe"] as JsonObject);
            AttachGuestArtifact(hookPath, guestArtifact);
            Console.WriteLine($"已获取客体 TypeTree 目录: {guestArtifact["file"]}");
        }
        if (renderDoc is not null && options.ContainsKey("auto-trigger"))
        {
            var triggerTool = ResolveRenderDocTriggerTool(options, profileId);
            var frames = ParsePositiveInt(options.GetValueOrDefault("trigger-frames"), "--trigger-frames", 1);
            var delay = ParseNonNegativeInt(options.GetValueOrDefault("trigger-delay-seconds"),
                "--trigger-delay-seconds", 0);
            if (delay > 0)
            {
                Console.WriteLine($"等待 {delay} 秒后请求 RenderDoc 捕获...");
                Thread.Sleep(TimeSpan.FromSeconds(delay));
            }
            var triggerStartedUtc = DateTime.UtcNow;
            var renderDocTargetName = StringValue(profile, "renderDocTargetName");
            RunRenderDocTargetControl(triggerTool, renderDoc, targetControlIdent,
                queryOnly: false, frames,
                targetName: renderDocTargetName);
            var captureTimeout = ParsePositiveInt(options.GetValueOrDefault("capture-timeout-seconds"),
                "--capture-timeout-seconds", 60);
            var completedCapture = WaitForRenderDocCapture(captureFile, triggerStartedUtc, captureTimeout);
            UpdateCaptureSessionHook(hookPath, captureFile, completedCapture, renderDocTargetName);
            if (completedCapture is null)
                Console.WriteLine($"已请求 {frames} 帧捕获，但在 {captureTimeout} 秒内未确认文件落盘；会话标记为 capture_pending。模板: {captureFile}");
            else
                Console.WriteLine($"已捕获 {frames} 帧并确认文件落盘: {completedCapture}");
        }
        Console.WriteLine($"宿主已注入，PID {targetPid}；会话: {hookPath}");
        if (ShouldWatch(options))
            WatchSession(hookPath, eventsPath, targetPid, options);
        else
            Console.WriteLine("运行时桥接模块应向 runtime/events.ndjson 追加 JSON Lines；完成后执行 finalize。");
        return 0;
    }

    private static void ValidateMumuProfile(JsonObject profile, string? profilePath)
    {
        var support = StringValue(profile, "runtimeSupport") ?? "mumu";
        if (string.Equals(support, "mumu", StringComparison.OrdinalIgnoreCase))
            return;

        var profileId = StringValue(profile, "id") ?? "<unknown>";
        if (string.Equals(support, "offline-only", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Profile {profileId} 仅用于离线分析，不能启动 MUMU 运行链路" +
                $"{(profilePath is null ? string.Empty : $": {FullPath(profilePath)}")}" +
                "。请改用当前已安装客户端对应的运行时 Profile。");
        }

        throw new InvalidDataException(
            $"Profile {profileId} 的 runtimeSupport={support} 不支持 MUMU 运行；" +
            "请将其声明为 mumu，或明确标记为 offline-only。");
    }

    private static string ResolveMumuAndroidVersion(
        JsonObject profile,
        Dictionary<string, List<string>> options)
    {
        var value = options.GetValueOrDefault("mumu-android-version")
            ?? StringValue(profile, "mumuAndroidVersion")
            ?? "auto";
        if (!value.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("12", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("15", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"MUMU Android 引擎版本无效: {value}。允许值为 auto、12、15，" +
                "可在 Profile 的 mumuAndroidVersion 或 --mumu-android-version 中设置。");
        }
        return value.ToLowerInvariant();
    }

    private static void ConfigureRenderDocVulkanLayers(string renderDocExecutable)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var renderDocDirectory = Path.GetDirectoryName(renderDocExecutable)
            ?? throw new InvalidOperationException("无法确定 RenderDoc 所在目录");
        var selectedManifest = Path.GetFullPath(Path.Combine(renderDocDirectory, "renderdoc.json"));
        if (!File.Exists(selectedManifest))
            throw new FileNotFoundException("RenderDoc 目录缺少 renderdoc.json", selectedManifest);

        var selectedIs32Bit = Is32BitRenderDocPath(selectedManifest);
        var registryPath = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";
        var views = new[]
        {
            (RegistryView.Registry64, !selectedIs32Bit),
            (RegistryView.Registry32, selectedIs32Bit)
        };

        try
        {
            foreach (var (view, registerSelected) in views)
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var layers = baseKey.CreateSubKey(registryPath, writable: true)
                    ?? throw new InvalidOperationException($"无法打开 Vulkan 隐式层注册表 ({view})");

                foreach (var valueName in layers.GetValueNames())
                {
                    if (IsRenderDocManifest(valueName) &&
                        !PathEquals(valueName, selectedManifest))
                    {
                        layers.DeleteValue(valueName, throwOnMissingValue: false);
                    }
                }

                if (registerSelected)
                    layers.SetValue(selectedManifest, 0, RegistryValueKind.DWord);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                "配置 RenderDoc Vulkan 隐式层需要管理员权限。请以管理员身份运行 Hook 工具，" +
                "否则旧的 RenderDoc 层可能会与当前版本同时加载。", ex);
        }

        Console.WriteLine($"已隔离其他 RenderDoc Vulkan 层，当前使用: {selectedManifest}");
    }

    private static bool IsRenderDocManifest(string valueName)
    {
        return string.Equals(Path.GetFileName(valueName), "renderdoc.json",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool Is32BitRenderDocPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.Contains("\\win32\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\\x86\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int FindRunningMumuHost(string expectedPath)
    {
        var matches = new List<(int Pid, string Path)>();
        foreach (var process in Process.GetProcessesByName("MuMuNxMain"))
        {
            try
            {
                var actualPath = process.MainModule?.FileName;
                if (actualPath is not null && PathEquals(actualPath, expectedPath))
                    matches.Add((process.Id, actualPath));
            }
            catch (Win32Exception)
            {
                // A process can exit or deny MainModule access while being enumerated.
            }
            finally
            {
                process.Dispose();
            }
        }

        return matches.Count switch
        {
            1 => matches[0].Pid,
            0 => throw new InvalidOperationException(
                $"未找到已运行的 MUMU 宿主: {expectedPath}。请先启动指定 MUMU 实例，或去掉 --reuse-mumu 使用默认启动流程。"),
            _ => throw new InvalidOperationException(
                "检测到多个相同路径的 MuMuNxMain.exe，拒绝猜测目标 PID: "
                + string.Join(", ", matches.Select(match => match.Pid)))
        };
    }

    private static int StartMumuHost(string executable, string mumuRoot)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.Combine(mumuRoot, "nx_main"),
            UseShellExecute = false,
            CreateNoWindow = false
        };
        var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 MuMuNxMain.exe");
        Thread.Sleep(3000);
        return process.Id;
    }

    private static int StartRenderDocCapture(
        string renderDoc,
        string target,
        string workingDirectory,
        string captureFile,
        string profileId,
        JsonObject profile,
        string eventsPath,
        string hookPath,
        string captureSessionId,
        Dictionary<string, List<string>> options)
    {
        var renderDocArgs = new List<string> { "capture", "--capture-file", captureFile };
        var targetWorkingDirectory = options.GetValueOrDefault("working-directory") is { } configuredDirectory
            ? FullPath(configuredDirectory)
            : workingDirectory;
        renderDocArgs.AddRange(new[] { "--working-dir", targetWorkingDirectory });
        AddFlag(options, "opt-api-validation", renderDocArgs, "--opt-api-validation");
        if (options.ContainsKey("opt-ref-all-resources") || IsEnabled(profile, "renderDocRefAllResources"))
            renderDocArgs.Add("--opt-ref-all-resources");
        AddFlag(options, "opt-capture-all-cmd-lists", renderDocArgs, "--opt-capture-all-cmd-lists");
        if (options.ContainsKey("opt-hook-children") || IsEnabled(profile, "renderDocHookChildren"))
            renderDocArgs.Add("--opt-hook-children");
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
        start.Environment["MY_HOOK_OUTPUT"] = Path.GetDirectoryName(eventsPath)!;
        start.Environment["MY_HOOK_EVENTS"] = eventsPath;
        start.Environment["MY_HOOK_FILE"] = hookPath;
        start.Environment["MY_HOOK_CAPTURE_SESSION_ID"] = captureSessionId;
        start.Environment.Remove("VK_LOADER_LAYERS_DISABLE");
        ApplyCaptureEnvironment(start, profile);
        foreach (var argument in renderDocArgs)
            start.ArgumentList.Add(argument);

        Console.WriteLine($"使用 RenderDoc 启动 MUMU 宿主，会话: {captureSessionId}");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 RenderDoc capture");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(stdout.Result)) Console.Write(stdout.Result);
        if (!string.IsNullOrWhiteSpace(stderr.Result)) Console.Error.WriteLine(stderr.Result.Trim());
        if (process.ExitCode <= 0)
            throw new InvalidOperationException(
                $"RenderDoc 没有返回有效 TargetControl ident，退出码: {process.ExitCode}");
        return process.ExitCode;
    }

    private static void ApplyCaptureEnvironment(ProcessStartInfo start, JsonObject profile)
    {
        if (profile["captureEnvironment"] is not JsonObject environment)
            return;
        foreach (var pair in environment)
        {
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                start.Environment[pair.Key] = text;
        }
    }

    private static bool IsEnabled(JsonObject profile, string key)
    {
        return profile[key] is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled;
    }

    internal static string ResolveRenderDocTriggerTool(
        Dictionary<string, List<string>> options, string profileId)
    {
        var configured = options.GetValueOrDefault("trigger-tool");
        if (configured is not null)
            return RequiredFile(configured, "trigger-tool");

        var relativePath = Path.Combine("runtime", profileId, "my-hook-renderdoc-trigger.exe");
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, relativePath) };
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, relativePath));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), relativePath));
        var bundled = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (bundled is not null)
            return bundled;
        throw new FileNotFoundException(
            "需要 --trigger-tool，或将 my-hook-renderdoc-trigger.exe 放到运行时 Profile 目录",
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath)));
    }

    internal static int RunRenderDocTargetControl(
        string triggerTool,
        string renderDoc,
        int targetControlIdent,
        bool queryOnly,
        int frames,
        string? captureFile = null,
        int waitSeconds = 120,
        string? targetName = null,
        int? targetPid = null)
    {
        if (targetControlIdent <= 0 && string.IsNullOrWhiteSpace(targetName) && targetPid is not > 0)
            throw new ArgumentException("必须指定 RenderDoc TargetControl ident、目标名或目标 PID");
        if (frames <= 0)
            throw new ArgumentException("RenderDoc 捕获帧数必须是正整数");

        var sourceDirectory = Path.GetDirectoryName(renderDoc)!;
        var renderDocDll = Path.Combine(sourceDirectory, "renderdoc.dll");
        if (!File.Exists(renderDocDll))
            throw new FileNotFoundException("RenderDoc 可执行文件旁缺少 renderdoc.dll", renderDocDll);

        var stageKey = Sha256(renderDocDll)[..16];
        var stageDirectory = Path.Combine(Path.GetTempPath(), "my-hook-tool", "renderdoc-control", stageKey);
        Directory.CreateDirectory(stageDirectory);
        var stagedTool = Path.Combine(stageDirectory, Path.GetFileName(triggerTool));
        var stagedDll = Path.Combine(stageDirectory, "renderdoc.dll");
        File.Copy(triggerTool, stagedTool, true);
        File.Copy(renderDocDll, stagedDll, true);
        foreach (var optionalName in new[]
        {
            "renderdoc.json", "renderdocshim64.dll", "msvcp140.dll", "msvcp140_1.dll",
            "msvcp140_2.dll", "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll",
            "vcruntime140.dll", "vcruntime140_1.dll", "vcruntime140_threads.dll",
            "d3dcompiler_47.dll"
        })
        {
            var optionalSource = Path.Combine(sourceDirectory, optionalName);
            if (File.Exists(optionalSource))
                File.Copy(optionalSource, Path.Combine(stageDirectory, optionalName), true);
        }

        var start = new ProcessStartInfo(stagedTool)
        {
            WorkingDirectory = stageDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--renderdoc-dll");
        start.ArgumentList.Add(stagedDll);
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            start.ArgumentList.Add("--target");
            start.ArgumentList.Add(targetName);
        }
        else if (targetPid is > 0)
        {
            start.ArgumentList.Add("--pid");
            start.ArgumentList.Add(targetPid.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            start.ArgumentList.Add("--ident");
            start.ArgumentList.Add(targetControlIdent.ToString(CultureInfo.InvariantCulture));
        }
        if (queryOnly)
            start.ArgumentList.Add("--query-only");
        else
        {
            start.ArgumentList.Add("--frames");
            start.ArgumentList.Add(frames.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(captureFile))
            {
                start.ArgumentList.Add("--capture-file");
                start.ArgumentList.Add(captureFile);
            }
            start.ArgumentList.Add("--wait-seconds");
            start.ArgumentList.Add(waitSeconds.ToString(CultureInfo.InvariantCulture));
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("无法启动 RenderDoc TargetControl 工具");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(stdout.Result))
            Console.Write(stdout.Result);
        if (!string.IsNullOrWhiteSpace(stderr.Result))
            Console.Error.WriteLine(stderr.Result.Trim());
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"RenderDoc TargetControl 操作失败，退出码: {process.ExitCode}");

        if (!queryOnly)
            return 0;
        var pidLine = stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("target-pid=", StringComparison.Ordinal));
        if (pidLine is null || !int.TryParse(pidLine["target-pid=".Length..], out var pid) || pid <= 0)
            throw new InvalidDataException("TargetControl 未返回有效 target-pid");
        return pid;
    }

    private static string? WaitForRenderDocCapture(string captureTemplate, DateTime startedUtc, int timeoutSeconds)
    {
        var directory = Path.GetDirectoryName(captureTemplate)
            ?? throw new InvalidDataException($"捕获模板没有父目录: {captureTemplate}");
        var extension = Path.GetExtension(captureTemplate);
        var stem = Path.GetFileNameWithoutExtension(captureTemplate);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        long? previousSize = null;
        string? previousPath = null;

        while (DateTime.UtcNow <= deadline)
        {
            var candidates = Directory.EnumerateFiles(directory)
                .Where(path =>
                    string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(path, captureTemplate, StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileNameWithoutExtension(path).StartsWith(stem + "_frame", StringComparison.OrdinalIgnoreCase)))
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists && file.Length > 0 && file.LastWriteTimeUtc >= startedUtc)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            if (candidates.Count > 0)
            {
                var candidate = candidates[0];
                if (string.Equals(previousPath, candidate.FullName, StringComparison.OrdinalIgnoreCase) &&
                    previousSize == candidate.Length)
                    return candidate.FullName;
                previousPath = candidate.FullName;
                previousSize = candidate.Length;
            }

            Thread.Sleep(500);
        }

        return null;
    }

    private static void UpdateCaptureSessionHook(
        string hookPath, string captureTemplate, string? capturePath, string? captureTarget)
    {
        var hook = JsonNode.Parse(File.ReadAllText(hookPath))?.AsObject()
            ?? throw new InvalidDataException($"无法读取会话 .hook: {hookPath}");
        var session = hook["session"]?.AsObject()
            ?? throw new InvalidDataException($"会话 .hook 缺少 session: {hookPath}");
        var hookDirectory = Path.GetDirectoryName(hookPath)!;
        session["captureTemplate"] = RelativeTo(hookDirectory, captureTemplate);
        session["captureFile"] = capturePath is null
            ? RelativeTo(hookDirectory, captureTemplate)
            : RelativeTo(hookDirectory, capturePath);
        session["captureStatus"] = capturePath is null ? "capture_pending" : "captured";
        if (!string.IsNullOrWhiteSpace(captureTarget))
            session["captureTarget"] = captureTarget;
        if (capturePath is null)
        {
            var diagnostics = hook["diagnostics"] as JsonArray ?? new JsonArray();
            diagnostics.Add(new JsonObject
            {
                ["level"] = "warning",
                ["code"] = "capture_not_confirmed",
                ["message"] = "TargetControl 已接受捕获请求，但在等待窗口内未确认捕获文件稳定落盘。"
            });
            hook["diagnostics"] = diagnostics;
        }
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));
    }

    private static void ValidateNativeExperimentOptions(string? enableNativeTransferValue,
        string? unsafeExperimentValue, string classIds, string? vtableOffsetValue,
        string? vtableProbeValue)
    {
        if (enableNativeTransferValue != "1")
            return;
        if (unsafeExperimentValue != "I_UNDERSTAND_UNITY_CRASH_RISK")
            throw new ArgumentException(
                "启用 native TypeTree 前必须显式传入 --unsafe-experiment I_UNDERSTAND_UNITY_CRASH_RISK");

        var nativeClassIds = classIds.Split(',', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (nativeClassIds.Length != 1)
            throw new ArgumentException(
                "native TypeTree 实验必须用 --guest-class-ids 限定为单个 ClassID");
        if (string.IsNullOrWhiteSpace(vtableOffsetValue))
            throw new ArgumentException(
                "native TypeTree 实验必须显式提供 --vtable-offset");
        if (vtableProbeValue == "1")
            throw new ArgumentException(
                "--vtable-probe 1 会在 native 调用前结束当前类型；native 实验必须显式传入 --vtable-probe 0");
    }

    private static void ValidateGuestTimingOptions(string? timeoutValue, string? eventTimeoutValue,
        string? eventMaxValue, string? eventIdleValue, string? dumpDelayValue)
    {
        ValidatePositiveSeconds(timeoutValue, "--guest-timeout-seconds");
        ValidatePositiveSeconds(eventTimeoutValue, "--guest-event-seconds");
        ValidatePositiveSeconds(eventMaxValue, "--guest-event-max-seconds");

        if (eventIdleValue is not null &&
            (!int.TryParse(eventIdleValue, out var eventIdleSeconds) || eventIdleSeconds < 0))
            throw new ArgumentException("--guest-event-idle-seconds 必须是非负整数");
        if (dumpDelayValue is not null &&
            (!int.TryParse(dumpDelayValue, out var dumpDelaySeconds) || dumpDelaySeconds < 0 || dumpDelaySeconds > 300))
            throw new ArgumentException("--guest-dump-delay-seconds 必须是 0 到 300 的整数");
    }

    private static void ValidatePositiveSeconds(string? value, string option)
    {
        if (value is not null && (!int.TryParse(value, out var seconds) || seconds <= 0))
            throw new ArgumentException($"{option} 必须是正整数");
    }

    private static JsonObject CaptureGuestTypeTree(
        string mumuRoot,
        string adbSerial,
        string packageName,
        string guestRipper,
        string? guestArm64Ripper,
        string runtimeDir,
        string? timeoutValue,
        string? eventTimeoutValue,
        string? eventMaxValue,
        string? eventIdleValue,
        string? totalTimeoutValue,
        string? observeTransfersValue,
        string? classIdsValue,
        string? dumpDelayValue,
        string? observeVtableOffsetValue,
        string? vtableProbeValue,
        string? maxObserverTargetsValue,
        string factorySkipIds,
        string? profileObserveVtableOffset,
        string? profileObserveVtableOffsets,
        string? enableNativeTransferValue,
        string? unsafeExperimentValue,
        string? nativeLayoutValue,
        string? vtableOffsetValue,
        string? directValue,
        string? directRvaValue,
        string? nativeOnTransferValue,
        string? layoutEvidenceValue,
        string? contextObserverValue,
        string? contextObserverAllValue,
        string? transferVtableRvaValue,
        string? captureTransferWordsValue,
        string? snapshotBytesValue,
        string snapshotClassIds,
        string captureSessionId,
        string? typeTreeBaselinePath,
        JsonObject? typeTreeBaselineProfile,
         string? expectedProfileVersion,
         string? scanReadonlyValue,
         string? scanStepValue,
         string? typeTreeCompareObserverValue,
         string? typeTreeCompareRvaValue,
         string? typeTreeConstructorObserverValue,
         string? typeTreeConstructorRvaValue,
         string? typeTreeConstructorFirstInstructionValue,
         string? typeTreeConstructorTargetArchitectureValue,
         string? serializedIdentityObserverValue,
         string? serializedIdentityRvaValue,
         string? serializedIdentityFirstInstructionValue,
         string? serializedIdentityTargetArchitectureValue,
         string? serializedIdentityGuestPatchValue,
         string? serializedIdentityCallerPatchValue,
         string? serializedIdentityProducerObserverValue,
         string? serializedIdentityProducerRvaValue,
         string? serializedIdentityProducerFirstInstructionValue,
         string? serializedIdentityProducerTargetArchitectureValue,
         string? serializedIdentityResolveObserverValue,
         string? serializedIdentityResolveRvaValue,
         string? serializedIdentityResolveFirstInstructionValue,
         string? serializedIdentityResolveRuntimeFirstInstructionValue,
         string? serializedIdentityResolveTargetArchitectureValue,
         string? serializedPPtrObserverValue,
         string? serializedPPtrRvaValue,
         string? serializedPPtrFirstInstructionValue,
         string? serializedPPtrTargetArchitectureValue,
         string? serializedPPtrGuestPatchValue,
         string? hsr45OpaqueCallsiteObserverValue,
         string? hsr45OpaqueCallsiteProbeMaskValue,
         string? serializedObjectLoadBoundaryObserverValue,
         string? serializedObjectLoadBoundaryRvaValue,
         string? serializedObjectLoadBoundaryFirstInstructionValue,
         string? serializedObjectLoadBoundaryTargetArchitectureValue,
         string? serializedObjectConsumerObserverValue,
         string? serializedObjectConsumerRvaValue,
         string? serializedObjectConsumerFirstInstructionValue,
         string? serializedObjectConsumerTargetArchitectureValue,
         string? serializedObjectConsumerCallerRvaValue,
         string? serializedObjectLookupObserverValue,
         string? serializedObjectLookupRvaValue,
         string? serializedObjectLookupFirstInstructionValue,
         string? serializedObjectLookupTargetArchitectureValue,
         string? serializedObjectResolveCandidateObserverValue,
         string? serializedObjectResolveCandidateRvaValue,
         string? serializedObjectResolveCandidateFirstInstructionValue,
         string? serializedObjectResolveCandidateTargetArchitectureValue,
         string? serializedObjectResolveCandidatePPtrCallerRvaValue,
         string? serializedObjectResolveCandidatePPtrToResolveCallerDeltaValue,
         string? serializedObjectResolveCandidatePPtrNestedResolveCallerRvaValue,
         string? serializedFieldReadObserverValue,
         string? serializedFieldReadRvaValue,
         string? serializedFieldReadFirstInstructionValue,
         string? serializedFieldReadTargetArchitectureValue,
         string? waitForUnityRuntimeValue,
         string? autoEnterValue,
         string? autoEnterDelayValue,
         string? autoEnterTapXValue,
         string? autoEnterTapYValue,
         string? autoEnterMaxAttemptsValue,
         string? autoEnterRetryIntervalValue,
         bool captureTransferCallers,
         bool requireCatalogOnly,
         JsonObject? factoryEntryProbe)
    {
        ValidateAndroidPackage(packageName);
        var adb = Path.Combine(mumuRoot, "nx_main", "adb.exe");
        if (!File.Exists(adb))
            throw new FileNotFoundException("找不到 MUMU ADB", adb);

        var timeoutSeconds = 60;
        if (timeoutValue is not null &&
            (!int.TryParse(timeoutValue, out timeoutSeconds) || timeoutSeconds <= 0))
            throw new ArgumentException("--guest-timeout-seconds 必须是正整数");

        // The HSR 4.5 launcher is intentionally bounded.  A healthy reused
        // MuMu session should reach a stable event stream quickly; a longer
        // wait hides login/update failures instead of improving evidence.
        var eventTimeoutSeconds = 8;
        if (eventTimeoutValue is not null &&
            (!int.TryParse(eventTimeoutValue, out eventTimeoutSeconds) || eventTimeoutSeconds <= 0))
            throw new ArgumentException("--guest-event-seconds 必须是正整数");

        var eventMaxSeconds = 20;
        if (eventMaxValue is not null &&
            (!int.TryParse(eventMaxValue, out eventMaxSeconds) ||
             eventMaxSeconds < eventTimeoutSeconds))
            throw new ArgumentException(
                "--guest-event-max-seconds 必须是不小于 --guest-event-seconds 的正整数");
        if (serializedIdentityCallerPatchValue == "1" && eventMaxValue is null)
            eventMaxSeconds = Math.Max(eventMaxSeconds, 150);

        var eventIdleSeconds = 3;
        if (eventIdleValue is not null &&
            (!int.TryParse(eventIdleValue, out eventIdleSeconds) || eventIdleSeconds < 0))
            throw new ArgumentException("--guest-event-idle-seconds 必须是非负整数");

        var totalTimeoutSeconds = 90;
        if (totalTimeoutValue is not null &&
            (!int.TryParse(totalTimeoutValue, out totalTimeoutSeconds) || totalTimeoutSeconds < 30))
            throw new ArgumentException("--guest-total-timeout-seconds 必须是不小于 30 的整数");
        if (totalTimeoutSeconds < eventTimeoutSeconds)
            throw new ArgumentException("--guest-total-timeout-seconds 不能小于 --guest-event-seconds");

        var autoEnter = autoEnterValue == "1";
        if (autoEnterValue is not null && autoEnterValue is not ("0" or "1"))
            throw new ArgumentException("--auto-enter 只能是 0 或 1");
        var autoEnterDelaySeconds = 1;
        if (autoEnterDelayValue is not null &&
            (!int.TryParse(autoEnterDelayValue, out autoEnterDelaySeconds) ||
             autoEnterDelaySeconds < 0 || autoEnterDelaySeconds > 15))
            throw new ArgumentException("--auto-enter-delay-seconds 必须是 0 到 15 的整数");
        var autoEnterTapX = 0.5;
        if (autoEnterTapXValue is not null &&
            (!double.TryParse(autoEnterTapXValue, NumberStyles.Float, CultureInfo.InvariantCulture,
                out autoEnterTapX) || autoEnterTapX is < 0 or > 1))
            throw new ArgumentException("--auto-enter-tap-x 必须是 0 到 1 之间的小数");
        var autoEnterTapY = 0.914;
        if (autoEnterTapYValue is not null &&
            (!double.TryParse(autoEnterTapYValue, NumberStyles.Float, CultureInfo.InvariantCulture,
                out autoEnterTapY) || autoEnterTapY is < 0 or > 1))
            throw new ArgumentException("--auto-enter-tap-y 必须是 0 到 1 之间的小数");

        var autoEnterMaxAttempts = 2;
        if (autoEnterMaxAttemptsValue is not null &&
            (!int.TryParse(autoEnterMaxAttemptsValue, out autoEnterMaxAttempts) ||
             autoEnterMaxAttempts < 1 || autoEnterMaxAttempts > 4))
            throw new ArgumentException("--auto-enter-max-attempts 必须是 1 到 4 的整数");

        var autoEnterRetryIntervalSeconds = 5;
        if (autoEnterRetryIntervalValue is not null &&
            (!int.TryParse(autoEnterRetryIntervalValue, out autoEnterRetryIntervalSeconds) ||
             autoEnterRetryIntervalSeconds < 5 || autoEnterRetryIntervalSeconds > 60))
            throw new ArgumentException("--auto-enter-retry-interval-seconds 必须是 5 到 60 的整数");

        var dumpDelaySeconds = 5;
        if (dumpDelayValue is not null &&
            (!int.TryParse(dumpDelayValue, out dumpDelaySeconds) || dumpDelaySeconds < 0 || dumpDelaySeconds > 300))
            throw new ArgumentException("--guest-dump-delay-seconds 必须是 0 到 300 的整数");

        var observeTransfers = observeTransfersValue == "1";
        if (observeTransfersValue is not null && observeTransfersValue is not ("0" or "1"))
            throw new ArgumentException("--observe-transfers 只能是 0 或 1；默认关闭实验性运行时观察器");

        // Creating Unity objects is observable work and can contend with the
        // login scene. Keep the default bounded; callers can opt into a wider
        // set explicitly, while a profile may provide a game-specific safe set.
        var classIds = string.IsNullOrWhiteSpace(classIdsValue)
            ? "21"
            : classIdsValue;
        ValidateClassIds(classIds);
        if (observeVtableOffsetValue is not null &&
            (!TryParseInteger(observeVtableOffsetValue, out var observeVtableOffset) ||
             observeVtableOffset < 0 || observeVtableOffset % IntPtr.Size != 0))
            throw new ArgumentException("--observe-vtable-offset 必须是按指针大小对齐的非负字节偏移");
        if (vtableProbeValue is not null && vtableProbeValue is not ("0" or "1"))
            throw new ArgumentException("--vtable-probe 只能是 0 或 1；默认关闭安全 vtable 观察");
        if (maxObserverTargetsValue is not null &&
            (!int.TryParse(maxObserverTargetsValue, out var maxObserverTargets) || maxObserverTargets < 0))
            throw new ArgumentException("--max-observer-targets 必须是非负整数；0 表示不限制");
        if (enableNativeTransferValue is not null && enableNativeTransferValue is not ("0" or "1"))
            throw new ArgumentException("--enable-native-transfer 只能是 0 或 1");
        if (nativeLayoutValue is not null && nativeLayoutValue is not ("0" or "1"))
            throw new ArgumentException("--native-layout 只能是 0 或 1");
        if (captureTransferWordsValue is not null && captureTransferWordsValue is not ("0" or "1"))
            throw new ArgumentException("--capture-transfer-words 只能是 0 或 1");
        if (contextObserverValue is not null && contextObserverValue is not ("0" or "1"))
            throw new ArgumentException("--context-observer 只能是 0 或 1");
        if (contextObserverAllValue is not null && contextObserverAllValue is not ("0" or "1"))
            throw new ArgumentException("--context-observer-all 只能是 0 或 1");
        if (typeTreeCompareObserverValue is not null
            && typeTreeCompareObserverValue is not ("0" or "1"))
            throw new ArgumentException("--typetree-compare-observer 只能是 0 或 1");
        if (typeTreeCompareRvaValue is not null
            && (!TryParseInteger(typeTreeCompareRvaValue, out var typeTreeCompareRva)
                || typeTreeCompareRva <= 0))
            throw new ArgumentException("--typetree-compare-rva 必须是正整数或 0x 开头的地址偏移");
        if (typeTreeConstructorObserverValue is not null
            && typeTreeConstructorObserverValue is not ("0" or "1"))
            throw new ArgumentException("--typetree-constructor-observer 只能是 0 或 1");
        if (typeTreeConstructorRvaValue is not null
            && (!TryParseInteger(typeTreeConstructorRvaValue, out var typeTreeConstructorRva)
                || typeTreeConstructorRva <= 0))
            throw new ArgumentException("--typetree-constructor-rva 必须是正整数或 0x 开头的地址偏移");
        if (typeTreeConstructorFirstInstructionValue is not null
            && (!TryParseUInt32(typeTreeConstructorFirstInstructionValue,
                    out var typeTreeConstructorFirstInstruction)
                || typeTreeConstructorFirstInstruction == 0))
            throw new ArgumentException(
                "--typetree-constructor-first-instruction 必须是正整数或 0x 开头的指令字");
        if (serializedIdentityObserverValue is not null
            && serializedIdentityObserverValue is not ("0" or "1"))
            throw new ArgumentException("--serialized-identity-observer 只能是 0 或 1");
        if (serializedIdentityResolveObserverValue is not null
            && serializedIdentityResolveObserverValue is not ("0" or "1"))
            throw new ArgumentException("--serialized-identity-resolve-observer 只能是 0 或 1");
        if (serializedIdentityGuestPatchValue is not null
            && serializedIdentityGuestPatchValue is not ("0" or "1"))
            throw new ArgumentException("--serialized-identity-guest-patch 只能是 0 或 1");
        if (serializedIdentityCallerPatchValue is not null
            && serializedIdentityCallerPatchValue is not ("0" or "1"))
            throw new ArgumentException("--serialized-identity-caller-patch 只能是 0 或 1");
        if (serializedIdentityRvaValue is not null
            && (!TryParseInteger(serializedIdentityRvaValue, out var serializedIdentityRva)
                || serializedIdentityRva <= 0))
            throw new ArgumentException("--serialized-identity-rva 必须是正整数或 0x 开头的地址偏移");
        if (serializedIdentityFirstInstructionValue is not null
            && (!TryParseUInt32(serializedIdentityFirstInstructionValue,
                    out var serializedIdentityFirstInstruction)
                || serializedIdentityFirstInstruction == 0))
            throw new ArgumentException(
                "--serialized-identity-first-instruction 必须是正整数或 0x 开头的指令字");
        if (serializedPPtrObserverValue is not null
            && serializedPPtrObserverValue is not ("0" or "1"))
            throw new ArgumentException("--serialized-pptr-observer 只能是 0 或 1");
        if (serializedPPtrGuestPatchValue is not null
            && serializedPPtrGuestPatchValue is not ("0" or "1"))
            throw new ArgumentException("--serialized-pptr-guest-patch 只能是 0 或 1");
        if (hsr45OpaqueCallsiteObserverValue is not null
            && hsr45OpaqueCallsiteObserverValue is not ("0" or "1"))
            throw new ArgumentException("--hsr45-opaque-callsite-observer 只能是 0 或 1");
        if (hsr45OpaqueCallsiteProbeMaskValue is not null
            && (!TryParseInteger(hsr45OpaqueCallsiteProbeMaskValue, out var hsr45OpaqueProbeMask)
                || hsr45OpaqueProbeMask < 0 || hsr45OpaqueProbeMask > 15))
            throw new ArgumentException(
                "--hsr45-opaque-callsite-probe-mask 必须是 0 到 15 的位掩码");
        if (hsr45OpaqueCallsiteObserverValue == "1"
            && (hsr45OpaqueCallsiteProbeMaskValue is null
                || hsr45OpaqueCallsiteProbeMaskValue == "0"))
            throw new ArgumentException(
                "启用 HSR 4.5 opaque-callsite observer 时必须显式选择非零 probe mask");
        if (serializedPPtrRvaValue is not null
            && (!TryParseInteger(serializedPPtrRvaValue, out var serializedPPtrRva)
                || serializedPPtrRva <= 0))
            throw new ArgumentException("--serialized-pptr-rva 必须是正整数或 0x 开头的地址偏移");
        if (serializedPPtrFirstInstructionValue is not null
            && (!TryParseUInt32(serializedPPtrFirstInstructionValue,
                    out var serializedPPtrFirstInstruction)
                || serializedPPtrFirstInstruction == 0))
            throw new ArgumentException(
                "--serialized-pptr-first-instruction 必须是正整数或 0x 开头的指令字");
        if (serializedObjectLookupObserverValue is not null
            && serializedObjectLookupObserverValue is not ("0" or "1"))
            throw new ArgumentException("--serialized-object-lookup-observer 只能是 0 或 1");
        if (serializedObjectLookupRvaValue is not null
            && (!TryParseInteger(serializedObjectLookupRvaValue, out var serializedObjectLookupRva)
                || serializedObjectLookupRva <= 0))
            throw new ArgumentException("--serialized-object-lookup-rva 必须是正整数或 0x 开头的地址偏移");
        if (serializedObjectLookupFirstInstructionValue is not null
            && (!TryParseUInt32(serializedObjectLookupFirstInstructionValue,
                    out var serializedObjectLookupFirstInstruction)
                || serializedObjectLookupFirstInstruction == 0))
            throw new ArgumentException(
                "--serialized-object-lookup-first-instruction 必须是正整数或 0x 开头的指令字");
        if (serializedObjectResolveCandidateObserverValue is not null
            && serializedObjectResolveCandidateObserverValue is not ("0" or "1"))
            throw new ArgumentException(
                "--serialized-object-resolve-candidate-observer 只能是 0 或 1");
        if (serializedObjectResolveCandidateRvaValue is not null
            && (!TryParseInteger(serializedObjectResolveCandidateRvaValue,
                    out var serializedObjectResolveCandidateRva)
                || serializedObjectResolveCandidateRva <= 0))
            throw new ArgumentException(
                "--serialized-object-resolve-candidate-rva 必须是正整数或 0x 开头的地址偏移");
        if (serializedObjectResolveCandidateFirstInstructionValue is not null
            && (!TryParseUInt32(serializedObjectResolveCandidateFirstInstructionValue,
                    out var serializedObjectResolveCandidateFirstInstruction)
                || serializedObjectResolveCandidateFirstInstruction == 0))
            throw new ArgumentException(
                "--serialized-object-resolve-candidate-first-instruction 必须是正整数或 0x 开头的指令字");
        if (serializedFieldReadObserverValue is not null
            && serializedFieldReadObserverValue is not ("0" or "1"))
            throw new ArgumentException("--serialized-field-read-observer 只能是 0 或 1");
        if (serializedFieldReadRvaValue is not null
            && (!TryParseInteger(serializedFieldReadRvaValue, out var serializedFieldReadRva)
                || serializedFieldReadRva <= 0))
            throw new ArgumentException("--serialized-field-read-rva 必须是正整数或 0x 开头的地址偏移");
        if (serializedFieldReadFirstInstructionValue is not null
            && (!TryParseUInt32(serializedFieldReadFirstInstructionValue,
                    out var serializedFieldReadFirstInstruction)
                || serializedFieldReadFirstInstruction == 0))
            throw new ArgumentException(
                "--serialized-field-read-first-instruction 必须是正整数或 0x 开头的指令字");
        ValidateNativeExperimentOptions(enableNativeTransferValue,
            unsafeExperimentValue, classIds, vtableOffsetValue, vtableProbeValue);
        if (vtableOffsetValue is not null &&
            (!TryParseInteger(vtableOffsetValue, out var vtableOffset) ||
             vtableOffset < 0 || vtableOffset % IntPtr.Size != 0))
            throw new ArgumentException("--vtable-offset 必须是按指针大小对齐的非负字节偏移");
        var snapshotBytes = 0;
        if (snapshotBytesValue is not null &&
            (!int.TryParse(snapshotBytesValue, out snapshotBytes) || snapshotBytes < 0 || snapshotBytes > 4096))
            throw new ArgumentException("--guest-snapshot-bytes 必须是 0 到 4096 的整数");
        if (scanReadonlyValue is not null && scanReadonlyValue is not ("0" or "1"))
            throw new ArgumentException("Profile scanReadonly 必须是 0 或 1");
        if (scanStepValue is not null && scanStepValue is not ("4" or "8"))
            throw new ArgumentException("Profile scanStep 必须是 4 或 8");
        if (!string.IsNullOrWhiteSpace(snapshotClassIds))
            ValidateClassIds(snapshotClassIds);
        var nativeTransferEnabled = enableNativeTransferValue == "1";
        var typeTreeCompareObserver = typeTreeCompareObserverValue == "1";
        var typeTreeConstructorObserver = typeTreeConstructorObserverValue == "1";
        var serializedIdentityObserver = serializedIdentityObserverValue == "1";
        var serializedIdentityProducerObserver =
            serializedIdentityProducerObserverValue == "1";
        var serializedIdentityResolveObserver =
            serializedIdentityResolveObserverValue == "1";
        var serializedPPtrObserver = serializedPPtrObserverValue == "1";
        var serializedObjectLoadBoundaryObserver =
            serializedObjectLoadBoundaryObserverValue == "1";
        var serializedObjectConsumerObserver =
            serializedObjectConsumerObserverValue == "1";
        var serializedObjectLookupObserver = serializedObjectLookupObserverValue == "1";
        var serializedObjectResolveCandidateObserver =
            serializedObjectResolveCandidateObserverValue == "1";
        var serializedFieldReadObserver = serializedFieldReadObserverValue == "1";
        if (serializedIdentityObserver
            && (string.IsNullOrWhiteSpace(serializedIdentityRvaValue)
                || string.IsNullOrWhiteSpace(serializedIdentityFirstInstructionValue)))
            throw new ArgumentException(
                "启用 --serialized-identity-observer 时必须同时提供 RVA 和首指令校验值");
        if (typeTreeConstructorObserver
            && (string.IsNullOrWhiteSpace(typeTreeConstructorRvaValue)
                || string.IsNullOrWhiteSpace(typeTreeConstructorFirstInstructionValue)))
            throw new ArgumentException(
                "启用 --typetree-constructor-observer 时必须同时提供 RVA 和首指令校验值");
        if (serializedIdentityProducerObserver
            && (string.IsNullOrWhiteSpace(serializedIdentityProducerRvaValue)
                || string.IsNullOrWhiteSpace(serializedIdentityProducerFirstInstructionValue)))
            throw new ArgumentException(
                "启用 --serialized-identity-producer-observer 时必须同时提供 RVA 和首指令校验值");
        if (serializedIdentityResolveObserver
            && (string.IsNullOrWhiteSpace(serializedIdentityResolveRvaValue)
                || string.IsNullOrWhiteSpace(serializedIdentityResolveFirstInstructionValue)))
            throw new ArgumentException(
                "启用 --serialized-identity-resolve-observer 时必须同时提供 RVA 和首指令校验值");
        if (serializedPPtrObserver
            && (string.IsNullOrWhiteSpace(serializedPPtrRvaValue)
                || string.IsNullOrWhiteSpace(serializedPPtrFirstInstructionValue)))
            throw new ArgumentException(
                "启用 --serialized-pptr-observer 时必须同时提供 RVA 和首指令校验值");
        if (hsr45OpaqueCallsiteObserverValue == "1"
            && (expectedProfileVersion != "4.5"
                || serializedPPtrRvaValue != "0x23a94bc"
                || serializedPPtrFirstInstructionValue != "0xd10183ff"
                || serializedObjectResolveCandidateObserverValue != "1"
                || serializedObjectResolveCandidateRvaValue != "0x23a9a7c"))
            throw new ArgumentException(
                "HSR 4.5 callsite observer requires version-matched PPtr callsite metadata and the typed resolver profile; the PPtr observer itself may be disabled");
        if (serializedObjectLoadBoundaryObserver
            && (string.IsNullOrWhiteSpace(serializedObjectLoadBoundaryRvaValue)
                || string.IsNullOrWhiteSpace(serializedObjectLoadBoundaryFirstInstructionValue)))
            throw new ArgumentException(
                "启用 --serialized-object-load-boundary-observer 时必须同时提供 RVA 和首指令校验值");
        if (serializedObjectConsumerObserver
            && (string.IsNullOrWhiteSpace(serializedObjectConsumerRvaValue)
                || string.IsNullOrWhiteSpace(serializedObjectConsumerFirstInstructionValue)))
            throw new ArgumentException(
                "启用 --serialized-object-consumer-observer 时必须同时提供 RVA 和首指令校验值");
        if (serializedObjectLookupObserver
            && (string.IsNullOrWhiteSpace(serializedObjectLookupRvaValue)
                || string.IsNullOrWhiteSpace(serializedObjectLookupFirstInstructionValue)))
            throw new ArgumentException(
                "启用 --serialized-object-lookup-observer 时必须同时提供 RVA 和首指令校验值");
        if (serializedObjectResolveCandidateObserver
            && (string.IsNullOrWhiteSpace(serializedObjectResolveCandidateRvaValue)
                || string.IsNullOrWhiteSpace(serializedObjectResolveCandidateFirstInstructionValue)))
            throw new ArgumentException(
                "启用 --serialized-object-resolve-candidate-observer 时必须同时提供 RVA 和首指令校验值");
        if (serializedFieldReadObserver
            && (string.IsNullOrWhiteSpace(serializedFieldReadRvaValue)
                || string.IsNullOrWhiteSpace(serializedFieldReadFirstInstructionValue)))
            throw new ArgumentException(
                "启用 --serialized-field-read-observer 时必须同时提供 RVA 和首指令校验值");
        var observeRuntimeEvents = observeTransfers || typeTreeCompareObserver
            || typeTreeConstructorObserver || serializedIdentityObserver
            || serializedIdentityProducerObserver || serializedIdentityResolveObserver
            || serializedPPtrObserver || serializedObjectLoadBoundaryObserver
            || serializedObjectConsumerObserver || serializedObjectLookupObserver;
        observeRuntimeEvents = observeRuntimeEvents || serializedFieldReadObserver
            || serializedObjectResolveCandidateObserver;
        var hsr45Il2CppApiTableEntryObserver = string.Equals(
            Environment.GetEnvironmentVariable(
                "MY_HOOK_HSR45_IL2CPP_API_TABLE_ENTRY_OBSERVER"),
            "1", StringComparison.Ordinal);
        var hsr45TprtDispatchCallObserver = string.Equals(
            Environment.GetEnvironmentVariable(
                "MY_HOOK_HSR45_TPRT_DISPATCH_CALL_OBSERVER"),
            "1", StringComparison.Ordinal);
        var hsr45TprtPfnEntryObserverValue = Environment.GetEnvironmentVariable(
            "MY_HOOK_HSR45_TPRT_PFN_ENTRY_OBSERVER");
        if (hsr45TprtPfnEntryObserverValue is not null
            && hsr45TprtPfnEntryObserverValue is not ("0" or "1"))
            throw new ArgumentException(
                "MY_HOOK_HSR45_TPRT_PFN_ENTRY_OBSERVER 只能是 0 或 1");
        var hsr45TprtPfnEntryObserver = hsr45TprtPfnEntryObserverValue == "1";
        var hsr45MaterialMShaderCallsiteObserver = string.Equals(
            Environment.GetEnvironmentVariable("SR_HSR45_MSHADER_CALLSITE_OBSERVER"),
            "1", StringComparison.Ordinal);
        var hsr45CallerWindowObserver = string.Equals(
            Environment.GetEnvironmentVariable(
                "MY_HOOK_HSR45_CALLER_WINDOW_OBSERVER"),
            "1", StringComparison.Ordinal);
        var hsr45IdentityEntryObserver = string.Equals(
            Environment.GetEnvironmentVariable(
                "MY_HOOK_HSR45_IDENTITY_ENTRY_OBSERVER"),
            "1", StringComparison.Ordinal);
        var serializedFieldCallerObserver = string.Equals(
            Environment.GetEnvironmentVariable(
                "MY_HOOK_SERIALIZED_FIELD_CALLER_OBSERVER"),
            "1", StringComparison.Ordinal);
        var forceNativeBridgeArm64LoadValue = Environment.GetEnvironmentVariable(
            "MY_HOOK_NATIVE_BRIDGE_FORCE_ARM64_LOAD");
        if (forceNativeBridgeArm64LoadValue is not null
            && forceNativeBridgeArm64LoadValue is not ("0" or "1"))
            throw new ArgumentException(
                "MY_HOOK_NATIVE_BRIDGE_FORCE_ARM64_LOAD 只能是 0 或 1");
        var forceNativeBridgeArm64Load = forceNativeBridgeArm64LoadValue == "1";
        observeRuntimeEvents |= hsr45Il2CppApiTableEntryObserver
            || hsr45TprtDispatchCallObserver || hsr45TprtPfnEntryObserver
            || hsr45MaterialMShaderCallsiteObserver
            || hsr45CallerWindowObserver || hsr45IdentityEntryObserver
            || serializedFieldCallerObserver;

        if (requireCatalogOnly && (observeRuntimeEvents || nativeTransferEnabled
            || vtableProbeValue == "1" || directValue == "1" || nativeLayoutValue == "1"
            || captureTransferCallers || captureTransferWordsValue == "1"))
            throw new ArgumentException("Read-only catalog profile rejects function observers, factories and native TypeTree experiments.");

        var catalogFileName = $"rtti-catalog-{Guid.NewGuid():N}.txt";
        var eventsFileName = $"runtime-events-{Guid.NewGuid():N}.ndjson";
        var vtableProbeFileName = $"vtable-probe-{Guid.NewGuid():N}.ndjson";
        var remoteArm64Library = string.Empty;
        string nativeBridgeArm64Library = string.Empty;
        var existingNativeBridgeProbe = Environment.GetEnvironmentVariable(
            "MY_HOOK_NATIVE_BRIDGE_PROBE_EXISTING");
        var autoGuestArm64IdentityLoad = (serializedIdentityObserver
            || typeTreeConstructorObserver
            || serializedPPtrObserver || serializedObjectLoadBoundaryObserver
            || serializedObjectLookupObserver
            || serializedFieldReadObserver || serializedFieldCallerObserver
            || serializedObjectResolveCandidateObserver
            || hsr45Il2CppApiTableEntryObserver || hsr45TprtDispatchCallObserver
            || hsr45CallerWindowObserver || hsr45IdentityEntryObserver
            || forceNativeBridgeArm64Load)
            && guestArm64Ripper is not null
            && string.IsNullOrWhiteSpace(existingNativeBridgeProbe);
        const string remoteExtraArm64Library =
            "/data/local/tmp/my-hook-typetree-ripper-arm64.so";
        var pushArm64Library = guestArm64Ripper is not null
            && string.IsNullOrWhiteSpace(existingNativeBridgeProbe)
            && !autoGuestArm64IdentityLoad;

        WaitForAdbOnline(adb, adbSerial, timeoutSeconds);
        if (guestArm64Ripper is not null)
        {
            if (pushArm64Library)
                EnsureAdbRoot(adb, adbSerial, timeoutSeconds);
            WaitForAdbOnline(adb, adbSerial, timeoutSeconds);
        }
        if (guestArm64Ripper is not null)
        {
            remoteArm64Library = autoGuestArm64IdentityLoad
                ? remoteExtraArm64Library
                : ResolveInstalledArm64LibraryPath(
                    adb, adbSerial, packageName,
                    string.IsNullOrWhiteSpace(existingNativeBridgeProbe)
                        ? "libmyhook-typetree-ripper.so"
                        : ValidateNativeBridgeProbeFileName(existingNativeBridgeProbe));
            nativeBridgeArm64Library = remoteArm64Library;
        }

        var nativeBridgeProbeMode = Environment.GetEnvironmentVariable(
            "MY_HOOK_NATIVE_BRIDGE_PROBE_MODE")
            ?? (autoGuestArm64IdentityLoad ? "6" : "0");
        var nativeBridgeExtraLibrary = autoGuestArm64IdentityLoad
            ? remoteExtraArm64Library
            : Environment.GetEnvironmentVariable("MY_HOOK_NATIVE_BRIDGE_EXTRA_LIBRARY") ?? "";
        // The serialized field reader is an entry point in libunity.so, but
        // MuMu does not report libunity.so through the NativeBridge load
        // callback. libmain.so is the earliest observed callback after the
        // Unity runtime is mapped and before libil2cpp.so startup. Keep the
        // historical trigger for the other observers because their target
        // modules and timing have not been proven equivalent.
        var configuredNativeBridgeExtraTrigger = Environment.GetEnvironmentVariable(
            "MY_HOOK_NATIVE_BRIDGE_EXTRA_TRIGGER");
        var nativeBridgeExtraTrigger = !string.IsNullOrWhiteSpace(configuredNativeBridgeExtraTrigger)
            ? configuredNativeBridgeExtraTrigger
            : autoGuestArm64IdentityLoad
                ? (serializedFieldReadObserver || serializedFieldCallerObserver
                    ? "libmain.so"
                    : (typeTreeConstructorObserver ? "libmain.so" : "libil2cpp.so"))
                : "";

        var configPath = Path.Combine(runtimeDir, "guest-typetree-ripper.conf");
        factoryEntryProbe ??= new JsonObject();
        var observeFactoryEntry = IsEnabled(factoryEntryProbe, "enabled");
        File.WriteAllText(configPath, string.Join(Environment.NewLine, new[]
        {
            "disable_log_hook=1",
            "dump_after_load=1",
            $"dump_delay_ms={dumpDelaySeconds * 1000}",
            "dump_retry_delay_ms=15000",
            $"catalog_only={(observeRuntimeEvents || nativeTransferEnabled || vtableProbeValue == "1" ? "0" : "1")}",
            $"catalog_file={catalogFileName}",
            $"events_file={eventsFileName}",
            $"observe_transfers={(observeTransfers ? "1" : "0")}",
            $"vtable_probe={vtableProbeValue ?? "0"}",
            $"vtable_probe_file={vtableProbeFileName}",
            // Safe observation must not bypass Unity's factory synchronization.
            // The no-lock mode caused Camera construction to fault during HSR startup.
            $"factory_mode=default",
            $"factory_skip_ids={(observeTransfers ? factorySkipIds : "")}",
            $"enable_native_transfer={enableNativeTransferValue ?? "0"}",
            $"unsafe_experiment={unsafeExperimentValue ?? ""}",
            $"native_layout={nativeLayoutValue ?? "0"}",
            $"vtable_offset={vtableOffsetValue ?? "0x90"}",
            $"direct={directValue ?? "0"}",
            // No direct GetTypeTree RVA is known for the selected guest.
            // An empty value must remain unavailable rather than
            // silently reusing an address from an unrelated build.
            $"direct_rva={directRvaValue ?? ""}",
            $"native_on_transfer={nativeOnTransferValue ?? "0"}",
            $"layout_evidence={layoutEvidenceValue ?? "0"}",
            $"context_observer={contextObserverValue ?? "0"}",
            $"context_observer_all={contextObserverAllValue ?? "0"}",
            $"guest_vulkan_draw_observer={Environment.GetEnvironmentVariable("MY_HOOK_GUEST_VK_DRAW_OBSERVER") ?? "0"}",
            $"guest_vulkan_draw_symbol={Environment.GetEnvironmentVariable("MY_HOOK_GUEST_VK_DRAW_SYMBOL") ?? "vkCmdDrawIndexed"}",
            $"guest_vulkan_draw_first_instruction={Environment.GetEnvironmentVariable("MY_HOOK_GUEST_VK_DRAW_FIRST_INSTRUCTION") ?? ""}",
            $"guest_vulkan_draw_probe_only={Environment.GetEnvironmentVariable("MY_HOOK_GUEST_VK_DRAW_PROBE_ONLY") ?? "0"}",
            $"guest_vulkan_indirect_symbol={Environment.GetEnvironmentVariable("MY_HOOK_GUEST_VK_INDIRECT_SYMBOL") ?? "0"}",
            $"guest_vulkan_resolved_hook={Environment.GetEnvironmentVariable("MY_HOOK_GUEST_VK_RESOLVED_HOOK") ?? "0"}",
            $"guest_vulkan_resolved_symbol={Environment.GetEnvironmentVariable("MY_HOOK_GUEST_VK_RESOLVED_SYMBOL") ?? "vkCmdDrawIndexed"}",
            $"guest_vulkan_resolved_first_instruction={Environment.GetEnvironmentVariable("MY_HOOK_GUEST_VK_RESOLVED_FIRST_INSTRUCTION") ?? ""}",
            $"typetree_compare_observer={(typeTreeCompareObserver ? "1" : "0")}",
            $"typetree_compare_rva={typeTreeCompareRvaValue ?? ""}",
            $"typetree_constructor_observer={(typeTreeConstructorObserver ? "1" : "0")}",
            $"typetree_constructor_rva={typeTreeConstructorRvaValue ?? ""}",
            $"typetree_constructor_first_instruction={typeTreeConstructorFirstInstructionValue ?? ""}",
            $"typetree_constructor_target_architecture={typeTreeConstructorTargetArchitectureValue ?? ""}",
            $"serialized_identity_observer={(serializedIdentityObserver ? "1" : "0")}",
            $"serialized_identity_rva={serializedIdentityRvaValue ?? ""}",
            $"serialized_identity_first_instruction={serializedIdentityFirstInstructionValue ?? ""}",
            $"serialized_identity_guest_patch={serializedIdentityGuestPatchValue ?? "0"}",
            $"serialized_identity_caller_patch={serializedIdentityCallerPatchValue ?? "0"}",
            $"serialized_identity_target_architecture={serializedIdentityTargetArchitectureValue ?? ""}",
            $"serialized_identity_record_snapshot_bytes={snapshotBytesValue ?? "0"}",
            $"serialized_identity_producer_observer={(serializedIdentityProducerObserver ? "1" : "0")}",
            $"serialized_identity_producer_rva={serializedIdentityProducerRvaValue ?? ""}",
            $"serialized_identity_producer_first_instruction={serializedIdentityProducerFirstInstructionValue ?? ""}",
            $"serialized_identity_producer_target_architecture={serializedIdentityProducerTargetArchitectureValue ?? ""}",
            $"serialized_identity_resolve_observer={(serializedIdentityResolveObserver ? "1" : "0")}",
            $"serialized_identity_resolve_rva={serializedIdentityResolveRvaValue ?? ""}",
            $"serialized_identity_resolve_first_instruction={serializedIdentityResolveFirstInstructionValue ?? ""}",
            $"serialized_identity_resolve_runtime_first_instruction={serializedIdentityResolveRuntimeFirstInstructionValue ?? ""}",
            $"serialized_identity_resolve_target_architecture={serializedIdentityResolveTargetArchitectureValue ?? ""}",
            $"serialized_pptr_observer={(serializedPPtrObserver ? "1" : "0")}",
            $"serialized_pptr_rva={serializedPPtrRvaValue ?? ""}",
            $"serialized_pptr_first_instruction={serializedPPtrFirstInstructionValue ?? ""}",
            $"serialized_pptr_target_architecture={serializedPPtrTargetArchitectureValue ?? ""}",
            $"serialized_pptr_guest_patch={serializedPPtrGuestPatchValue ?? "0"}",
            $"hsr45_pptr_scope_mode={Environment.GetEnvironmentVariable("MY_HOOK_HSR45_PPTR_SCOPE_MODE") ?? "both"}",
            $"serialized_pptr_callsite_rva={Environment.GetEnvironmentVariable("SR_SERIALIZED_PPTR_CALLSITE_RVA") ?? ""}",
            $"serialized_pptr_callsite_return_rva={Environment.GetEnvironmentVariable("SR_SERIALIZED_PPTR_CALLSITE_RETURN_RVA") ?? ""}",
            $"serialized_pptr_callsite_return_first_instruction={Environment.GetEnvironmentVariable("SR_SERIALIZED_PPTR_CALLSITE_RETURN_FIRST_INSTRUCTION") ?? ""}",
            $"serialized_pptr_callsite_key_stack_offset={Environment.GetEnvironmentVariable("SR_SERIALIZED_PPTR_CALLSITE_KEY_STACK_OFFSET") ?? ""}",
            $"serialized_pptr_callsite_output_stack_offset={Environment.GetEnvironmentVariable("SR_SERIALIZED_PPTR_CALLSITE_OUTPUT_STACK_OFFSET") ?? ""}",
            $"serialized_pptr_callsite_max_records={Environment.GetEnvironmentVariable("SR_SERIALIZED_PPTR_CALLSITE_MAX_RECORDS") ?? ""}",
            $"serialized_pptr_callsite_lookup_caller_rva={Environment.GetEnvironmentVariable("SR_SERIALIZED_PPTR_CALLSITE_LOOKUP_CALLER_RVA") ?? ""}",
            $"serialized_pptr_callsite_lookup_branch_instruction={Environment.GetEnvironmentVariable("SR_SERIALIZED_PPTR_CALLSITE_LOOKUP_BRANCH_INSTRUCTION") ?? ""}",
            $"hsr45_opaque_callsite_observer={hsr45OpaqueCallsiteObserverValue ?? "0"}",
            $"hsr45_opaque_callsite_probe_mask={hsr45OpaqueCallsiteProbeMaskValue ?? "0"}",
            $"hsr45_material_mshader_callsite_observer={(string.Equals(Environment.GetEnvironmentVariable("SR_HSR45_MSHADER_CALLSITE_OBSERVER"), "1", StringComparison.Ordinal) ? "1" : "0")}",
            $"hsr45_caller_window_observer={Environment.GetEnvironmentVariable("MY_HOOK_HSR45_CALLER_WINDOW_OBSERVER") ?? "0"}",
            $"hsr45_identity_entry_observer={Environment.GetEnvironmentVariable("MY_HOOK_HSR45_IDENTITY_ENTRY_OBSERVER") ?? "0"}",
            $"serialized_object_load_boundary_observer={(serializedObjectLoadBoundaryObserver ? "1" : "0")}",
            $"serialized_object_load_boundary_max_records={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_OBJECT_LOAD_BOUNDARY_MAX_RECORDS") ?? "4096"}",
            $"serialized_object_load_boundary_rva={serializedObjectLoadBoundaryRvaValue ?? ""}",
            $"serialized_object_load_boundary_first_instruction={serializedObjectLoadBoundaryFirstInstructionValue ?? ""}",
            $"serialized_object_load_boundary_target_architecture={serializedObjectLoadBoundaryTargetArchitectureValue ?? ""}",
            $"serialized_object_consumer_observer={(serializedObjectConsumerObserver ? "1" : "0")}",
            $"serialized_object_consumer_max_records={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_OBJECT_CONSUMER_MAX_RECORDS") ?? "1024"}",
            $"serialized_object_consumer_rva={serializedObjectConsumerRvaValue ?? ""}",
            $"serialized_object_consumer_first_instruction={serializedObjectConsumerFirstInstructionValue ?? ""}",
            $"serialized_object_consumer_target_architecture={serializedObjectConsumerTargetArchitectureValue ?? ""}",
            $"serialized_object_consumer_caller_rva={serializedObjectConsumerCallerRvaValue ?? ""}",
            $"serialized_object_lookup_observer={(serializedObjectLookupObserver ? "1" : "0")}",
            $"serialized_object_lookup_max_records={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_OBJECT_LOOKUP_MAX_RECORDS") ?? "4096"}",
            $"serialized_object_lookup_rva={serializedObjectLookupRvaValue ?? ""}",
            $"serialized_object_lookup_first_instruction={serializedObjectLookupFirstInstructionValue ?? ""}",
            $"serialized_object_lookup_target_architecture={serializedObjectLookupTargetArchitectureValue ?? ""}",
            $"serialized_object_resolve_candidate_observer={serializedObjectResolveCandidateObserverValue ?? "0"}",
            $"serialized_object_resolve_candidate_max_calls={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_OBJECT_RESOLVE_CANDIDATE_MAX_CALLS") ?? "262144"}",
            $"serialized_object_resolve_candidate_rva={serializedObjectResolveCandidateRvaValue ?? ""}",
            $"serialized_object_resolve_candidate_first_instruction={serializedObjectResolveCandidateFirstInstructionValue ?? ""}",
            $"serialized_object_resolve_candidate_target_architecture={serializedObjectResolveCandidateTargetArchitectureValue ?? ""}",
            $"serialized_object_resolve_candidate_pptr_caller_rva={serializedObjectResolveCandidatePPtrCallerRvaValue ?? ""}",
            $"serialized_object_resolve_candidate_pptr_to_resolve_caller_delta={serializedObjectResolveCandidatePPtrToResolveCallerDeltaValue ?? ""}",
            $"serialized_object_resolve_candidate_pptr_nested_resolve_caller_rva={serializedObjectResolveCandidatePPtrNestedResolveCallerRvaValue ?? ""}",
            $"serialized_field_read_observer={(serializedFieldReadObserver ? "1" : "0")}",
            $"serialized_field_read_rva={serializedFieldReadRvaValue ?? ""}",
            $"serialized_field_read_first_instruction={serializedFieldReadFirstInstructionValue ?? ""}",
            $"serialized_field_read_target_architecture={serializedFieldReadTargetArchitectureValue ?? ""}",
            $"serialized_field_read_mode={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_FIELD_READ_MODE") ?? ""}",
            $"serialized_field_read_max_records={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_FIELD_READ_MAX_RECORDS") ?? "4096"}",
            $"serialized_field_read_names={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_FIELD_READ_NAMES") ?? ""}",
            $"serialized_field_caller_observer={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_FIELD_CALLER_OBSERVER") ?? "0"}",
            $"serialized_field_caller_rva={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_FIELD_CALLER_RVA") ?? ""}",
            $"serialized_field_caller_first_instruction={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_FIELD_CALLER_FIRST_INSTRUCTION") ?? ""}",
            $"serialized_field_caller_target_architecture={Environment.GetEnvironmentVariable("MY_HOOK_SERIALIZED_FIELD_CALLER_TARGET_ARCHITECTURE") ?? ""}",
            $"native_bridge_arm64_library={nativeBridgeArm64Library}",
            $"native_bridge_probe_mode={nativeBridgeProbeMode}",
            $"native_bridge_probe_source={(Environment.GetEnvironmentVariable(
                "MY_HOOK_NATIVE_BRIDGE_PROBE_SOURCE") ?? "")}",
            $"native_bridge_extra_library={nativeBridgeExtraLibrary}",
            $"native_bridge_extra_trigger={nativeBridgeExtraTrigger}",
            $"hsr45_tprt_dispatch_snapshot={(string.Equals(Environment.GetEnvironmentVariable("MY_HOOK_HSR45_TPRT_DISPATCH_SNAPSHOT"), "1", StringComparison.Ordinal) ? "1" : "0")}",
            $"hsr45_il2cpp_api_table_entry_observer={(hsr45Il2CppApiTableEntryObserver ? "1" : "0")}",
            $"hsr45_tprt_dispatch_call_observer={(hsr45TprtDispatchCallObserver ? "1" : "0")}",
            $"hsr45_tprt_pfn_entry_observer={(hsr45TprtPfnEntryObserver ? "1" : "0")}",
            // The current HSR transfer implementation does not read a
            // transfer vtable prefix; never serialize an unverified address.
            "transfer_vtable_rva=",
            $"capture_transfer_words={captureTransferWordsValue ?? "0"}",
            $"capture_transfer_callers={(captureTransferCallers ? "1" : "0")}",
            $"snapshot_bytes={(observeTransfers ? snapshotBytes.ToString() : "0")}",
            $"snapshot_class_ids={(observeTransfers ? snapshotClassIds : "")}",
            $"capture_session_id={captureSessionId}",
            $"observe_vtable_offset={observeVtableOffsetValue
                ?? profileObserveVtableOffset
                ?? "0xa0"}",
            $"observe_vtable_offsets={profileObserveVtableOffsets ?? ""}",
            $"max_observer_targets={maxObserverTargetsValue ?? "0"}",
            $"output_directory=/sdcard/Android/data/{packageName}/files",
            $"class_ids={classIds}",
            "include_il2cpp=0",
            // Anonymous Native Bridge mappings are not TypeTree catalogs. Keep
            // them out of the generic dumper; resolve experiments must use a
            // separate bounded mapping reader instead.
            "include_anonymous=0",
            $"scan_readonly={scanReadonlyValue ?? "0"}",
            $"scan_step={scanStepValue ?? "8"}",
            $"factory_entry_probe={(IsEnabled(factoryEntryProbe, "enabled") ? "1" : "0")}",
            $"factory_entry_build_id={StringValue(factoryEntryProbe, "buildId") ?? ""}",
            $"factory_entry_elf_va={StringValue(factoryEntryProbe, "elfVaddr") ?? ""}",
            $"factory_entry_code_hex={StringValue(factoryEntryProbe, "codeHex") ?? ""}",
            $"factory_entry_fail_code_write_once={(IsEnabled(factoryEntryProbe, "faultCodeWriteOnce") ? "1" : "0")}"
        }) + Environment.NewLine);

        if (requireCatalogOnly)
        {
            var configuration = File.ReadAllLines(configPath);
            if (!configuration.Contains("catalog_only=1") || configuration.Any(line =>
                (line.Contains("observer=1", StringComparison.Ordinal)
                    || line.Contains("patch=1", StringComparison.Ordinal)
                    || line is "vtable_probe=1" or "enable_native_transfer=1")))
                throw new ArgumentException("Catalog-only configuration contains a forbidden observer or native operation.");
        }

        const string remoteLibrary = "/data/local/tmp/my-hook-typetree-ripper.so";
        const string remoteConfig = "/data/local/tmp/typetree-ripper.conf";
        var remoteCatalog = $"/sdcard/Android/data/{packageName}/files/{catalogFileName}";
        var remoteReleaseTypeTree = $"/sdcard/Android/data/{packageName}/files/release.ttbin";
        var remoteEvents = $"/sdcard/Android/data/{packageName}/files/{eventsFileName}";
        var remoteVtableProbe = $"/sdcard/Android/data/{packageName}/files/{vtableProbeFileName}";
        var wrapProperty = $"wrap.{packageName}";
        var localCatalog = Path.Combine(runtimeDir, "typetree", "rtti-catalog.txt");
        var localReleaseTypeTree = Path.Combine(runtimeDir, "typetree", "release.ttbin");
        var localEvents = Path.Combine(runtimeDir, "events.ndjson");
        var localRipperLogcat = Path.Combine(runtimeDir, "typetree", "ripper-logcat.txt");
        var localVtableProbe = Path.Combine(runtimeDir, "typetree", "vtable-probe.ndjson");
        var probeCatalog = Path.Combine(runtimeDir, "typetree", ".rtti-catalog.probe");
        var probeEvents = Path.Combine(runtimeDir, ".runtime-events.probe");
        Directory.CreateDirectory(Path.GetDirectoryName(localCatalog)!);
        File.WriteAllText(localEvents, string.Empty);

        var experimentStopwatch = Stopwatch.StartNew();
        var timingStages = new JsonArray();
        var timingStageStart = DateTime.UtcNow;
        void MarkTimingStage(string name, string status = "completed")
        {
            var now = DateTime.UtcNow;
            timingStages.Add(new JsonObject
            {
                ["name"] = name,
                ["status"] = status,
                ["startedUtc"] = timingStageStart.ToString("O", CultureInfo.InvariantCulture),
                ["endedUtc"] = now.ToString("O", CultureInfo.InvariantCulture),
                ["elapsedSeconds"] = Math.Round((now - timingStageStart).TotalSeconds, 3)
            });
            timingStageStart = now;
        }

        ValidateGuestRipperAbi(adb, adbSerial, guestRipper);
        string? packageVersion = null;
        // MuMu can report ADB readiness before package-manager services are
        // ready after an emulator restart.  Do not turn that transient state
        // into a false cross-version rejection.
        for (var attempt = 0; attempt < 15 && string.IsNullOrWhiteSpace(packageVersion); attempt++)
        {
            packageVersion = TryGetAndroidPackageVersion(adb, adbSerial, packageName);
            if (string.IsNullOrWhiteSpace(packageVersion))
                Thread.Sleep(1000);
        }
        if (!string.IsNullOrWhiteSpace(packageVersion))
            Console.WriteLine($"客体包版本已确认: {packageVersion}");
        MarkTimingStage("package-version-check");
        var expectedVersion = expectedProfileVersion;
        if (!Version.TryParse(packageVersion, out var actualVersion)
            || !Version.TryParse(expectedVersion, out var profileVersion)
            || actualVersion.Major != profileVersion.Major
            || actualVersion.Minor != profileVersion.Minor)
        {
            throw new InvalidOperationException(
                $"客体 TypeTree 拒绝跨版本执行: Profile={expectedVersion ?? "<missing>"}, 客体={packageVersion ?? "<unknown>"}。"
                + "请使用与当前 Profile 同一主次版本的客体，不能把其他版本的运行时结果冒充当前目标。");
        }
        RunAdb(adb, adbSerial, "push", guestRipper, remoteLibrary);
        if (pushArm64Library)
        {
            RunAdb(adb, adbSerial, "push", guestArm64Ripper!, remoteArm64Library);
            RunAdb(adb, adbSerial, "shell", "chmod", "0644", remoteArm64Library);
        }
        else if (autoGuestArm64IdentityLoad)
        {
            RunAdb(adb, adbSerial, "push", guestArm64Ripper!, remoteExtraArm64Library);
            RunAdb(adb, adbSerial, "shell", "chmod", "0755", remoteExtraArm64Library);
        }
        RunAdb(adb, adbSerial, "push", configPath, remoteConfig);
        var eventsAttached = false;
        var sceneObserved = false;
        var observedRuntimeTypes = new HashSet<string>(StringComparer.Ordinal);
        var observationSeconds = 0;
        JsonArray? typeTreeArtifactsInfo = null;
        JsonObject? vtableProbeInfo = null;
        bool? nativeGuestProcessRunning = null;
        bool? guestProcessRunningAfterCatalog = null;
        string? localAndroidErrorLogcat = null;
        var eventStopReason = "not-started";
        JsonObject? autoEnterInfo = null;
        JsonObject? factoryEntryInfo = null;
        var factoryEntryTerminalObserved = false;
        var autoEnterAttempts = new JsonArray();
        try
        {
            var previousSignature = RemoteFileSignature(adb, adbSerial, remoteCatalog, probeCatalog);
            RunAdb(adb, adbSerial, "shell", "setprop", wrapProperty,
                $"LD_PRELOAD={remoteLibrary}");
            RunAdb(adb, adbSerial, "shell", "am", "force-stop", packageName);
            // Resolve the package's declared launcher activity through Android
            // itself. Do not issue a second monkey launch: the duplicate start
            // races the SDK/login activity and made the ready phase flaky.
            if (!StartAndroidPackageLauncher(adb, adbSerial, packageName))
            {
                RunAdb(adb, adbSerial, "shell", "monkey", "-p", packageName,
                    "-c", "android.intent.category.LAUNCHER", "1");
                Console.WriteLine("显式 Activity 启动失败，已使用 Android monkey 作为一次性 fallback。");
            }

            // Foreground, Unity runtime, and RTTI catalog are one readiness
            // phase. Do not reset the full timeout at each sub-stage: a broken
            // or stuck launch must finish within the same 60-120 second budget.
            var totalDeadline = DateTime.UtcNow.AddSeconds(totalTimeoutSeconds);
            var readinessBudgetSeconds = Math.Min(
                Math.Clamp(timeoutSeconds, 30, 60),
                Math.Max(1, totalTimeoutSeconds - eventTimeoutSeconds));
            var readinessDeadline = new[]
            {
                DateTime.UtcNow.AddSeconds(readinessBudgetSeconds),
                totalDeadline
            }.Min();
            int RemainingReadinessSeconds()
            {
                return Math.Max(1, (int)Math.Ceiling(
                    (readinessDeadline - DateTime.UtcNow).TotalSeconds));
            }

            var foregroundConfirmed = WaitForAndroidPackageForeground(
                adb, adbSerial, packageName, RemainingReadinessSeconds());
            var guestProcessRunning = IsAndroidPackageRunning(adb, adbSerial, packageName);
            MarkTimingStage("guest-process-and-foreground-ready",
                guestProcessRunning ? "completed" : "guest-not-running");
            if (waitForUnityRuntimeValue == "1" && guestProcessRunning)
            {
                if (!foregroundConfirmed)
                    Console.WriteLine("前台状态未确认，但 HSR 客体进程仍在运行；继续等待 Unity 原生运行时加载。请在 MuMu 中进入游戏。");
                WaitForAndroidUnityRuntime(adb, adbSerial, packageName,
                    remoteEvents, probeEvents, RemainingReadinessSeconds());
                if (autoEnter)
                {
                    if (autoEnterDelaySeconds > 0)
                        Thread.Sleep(TimeSpan.FromSeconds(autoEnterDelaySeconds));
                    autoEnterInfo = TryAutoEnterAndroidGame(
                        adb, adbSerial, autoEnterTapX, autoEnterTapY);
                    autoEnterAttempts.Add(autoEnterInfo.DeepClone());
                }
                NotifyLoginReady(autoEnter &&
                    string.Equals(autoEnterInfo?["status"]?.GetValue<string>(), "sent", StringComparison.Ordinal));
            }
            else if (foregroundConfirmed)
            {
                if (autoEnter)
                    autoEnterInfo = TryAutoEnterAndroidGame(
                        adb, adbSerial, autoEnterTapX, autoEnterTapY);
                if (autoEnterInfo is not null)
                    autoEnterAttempts.Add(autoEnterInfo.DeepClone());
                NotifyLoginReady(autoEnter &&
                    string.Equals(autoEnterInfo?["status"]?.GetValue<string>(), "sent", StringComparison.Ordinal));
            }
            else
                Console.WriteLine("客体进程已启动，但 MUMU 未确认其前台状态；继续采集 TypeTree，不播放登录提示音。");

            MarkTimingStage("unity-runtime-ready-and-auto-enter");
            Console.WriteLine($"等待客体 TypeTree 目录，启动阶段剩余最多 {RemainingReadinessSeconds()} 秒...");
            var deadline = readinessDeadline;
            var catalogReady = false;
            var catalogPoll = 0;
            while (DateTime.UtcNow < deadline)
            {
                if (catalogPoll++ % 2 == 0 &&
                    !IsAndroidPackageRunning(adb, adbSerial, packageName))
                {
                    guestProcessRunningAfterCatalog = false;
                    Console.WriteLine("客体进程在 TypeTree 目录生成前已退出，提前结束启动等待。");
                    break;
                }
                var signature = RemoteFileSignature(adb, adbSerial, remoteCatalog, probeCatalog);
                if (signature is not null && !string.Equals(signature, previousSignature, StringComparison.Ordinal))
                {
                    RunAdb(adb, adbSerial, "pull", remoteCatalog, localCatalog);
                    try
                    {
                        ValidateRuntimeCatalog(localCatalog);
                        catalogReady = true;
                        break;
                    }
                    catch (InvalidDataException)
                    {
                        // The guest creates the file before its final write. Keep
                        // polling until the pulled snapshot is structurally valid.
                    }
                }
                Thread.Sleep(1000);
            }

            if (!catalogReady)
            {
                // Keep the failure actionable.  A missing catalog can mean
                // either that the guest module was never loaded or that the
                // game exited before its constructor completed; the event
                // stream alone cannot distinguish those cases.
                guestProcessRunningAfterCatalog = IsAndroidPackageRunning(adb, adbSerial, packageName);
                var timeoutLogcat = RunAdbAllowFailure(adb, adbSerial, "shell", "logcat", "-d", "-t", "2000", "*:E");
                if (timeoutLogcat.ExitCode == 0)
                {
                    localAndroidErrorLogcat = Path.Combine(runtimeDir, "typetree", "android-errors-logcat.txt");
                    File.WriteAllText(localAndroidErrorLogcat, timeoutLogcat.Stdout);
                }

                var processState = guestProcessRunningAfterCatalog is null
                    ? "unknown"
                    : guestProcessRunningAfterCatalog.Value ? "running" : "not_running";
                throw new TimeoutException(
                    $"客体未在启动总预算 {readinessBudgetSeconds} 秒内生成新的 {remoteCatalog}"
                    + $"；guestProcess={processState}"
                    + (localAndroidErrorLogcat is null ? string.Empty : $"；错误日志={localAndroidErrorLogcat}"));
            }

            MarkTimingStage("typetree-catalog-ready");
            guestProcessRunningAfterCatalog = IsAndroidPackageRunning(adb, adbSerial, packageName);
            if (guestProcessRunningAfterCatalog == false)
            {
                Console.WriteLine(
                    $"客体程序 {packageName} 在 TypeTree 目录生成后已退出；保留本次 4.5 证据，结果将标记为 guest_exited_after_catalog。");
                var errorLogcat = RunAdbAllowFailure(adb, adbSerial, "shell", "logcat", "-d", "-t", "1000", "*:E");
                if (errorLogcat.ExitCode == 0)
                {
                    localAndroidErrorLogcat = Path.Combine(runtimeDir, "typetree", "android-errors-logcat.txt");
                    File.WriteAllText(localAndroidErrorLogcat, errorLogcat.Stdout);
                }
            }

            // Transfer observations are experimental and disabled by default.
            if (observeFactoryEntry)
            {
                var pidText = RunAdbAllowFailure(adb, adbSerial, "shell", "pidof", packageName).Stdout.Trim();
                if (!int.TryParse(pidText, out var factoryGuestPid) || factoryGuestPid <= 0)
                    throw new InvalidOperationException("Factory entry probe has no unambiguous game PID.");
                var factoryDeadline = new[] { totalDeadline, DateTime.UtcNow.AddSeconds(60) }.Min();
                while (DateTime.UtcNow < factoryDeadline)
                {
                    if (TryPullGuestFile(adb, adbSerial, remoteEvents, localEvents))
                    {
                        var records = ReadEventRecords(localEvents, ignoreIncompleteTail: true).OfType<JsonObject>()
                            .Where(record => StringValue(record, "producer") == "GuestFactoryProbe"
                                && StringValue(record, "captureSessionId") == captureSessionId
                                && Int64Value(record, "processId") == factoryGuestPid).ToArray();
                        var terminal = records.LastOrDefault(record => StringValue(record, "kind") is
                            "factory-entry-summary" or "factory-entry-rejected" or "factory-entry-not-observed");
                        if (terminal is not null)
                        {
                            factoryEntryTerminalObserved = true;
                            var calls = records.Count(record => StringValue(record, "kind") == "factory-entry-call");
                            factoryEntryInfo = new JsonObject
                            {
                                ["status"] = StringValue(terminal, "kind") == "factory-entry-summary"
                                    ? calls > 0 ? "observed_entry_calls" : "not_observed" : "rejected",
                                ["processId"] = factoryGuestPid, ["records"] = calls,
                                ["terminalObserved"] = true, ["abiValidated"] = false,
                                ["terminal"] = terminal.DeepClone()
                            };
                            Console.WriteLine($"ARM64 entry observation terminal received: {factoryEntryInfo["status"]}, records={calls}.");
                            break;
                        }
                    }
                    if (!IsAndroidPackageRunning(adb, adbSerial, packageName)) break;
                    Thread.Sleep(1000);
                }
                if (!factoryEntryTerminalObserved)
                    throw new InvalidOperationException("Factory entry probe did not produce a same-session/PID terminal event; the target game will be stopped.");
                MarkTimingStage("factory-entry-observation", StringValue(factoryEntryInfo!, "status") ?? "unknown");
            }
            // When enabled, give normal Unity startup a bounded grace period.
            eventStopReason = observeRuntimeEvents ? "max-window" : "disabled";
            if (!observeRuntimeEvents)
            {
                Console.WriteLine(nativeTransferEnabled
                    ? "native TypeTree 单次生成已完成；普通 Transfer 运行时观察器保持关闭。"
                    : "已关闭实验性运行时观察器，仅保留安全的 TypeTree 目录；如需测试事件请显式启用对应观察器。");
            }
            else
            {
                // A fixed window can finish while HSR is still on its black
                // loading screen. If an extended window is configured, keep
                // collecting until scene-level Unity types have appeared and
                // the stream has been idle for the requested interval.
                Console.WriteLine($"等待客体运行时事件，初始 {eventTimeoutSeconds} 秒，最长 {eventMaxSeconds} 秒...");
                var observationStartedUtc = DateTime.UtcNow;
                var eventDeadline = new[]
                {
                    observationStartedUtc.AddSeconds(eventMaxSeconds),
                    totalDeadline
                }.Min();
                var initialDeadline = observationStartedUtc.AddSeconds(eventTimeoutSeconds);
                var nextAutoEnterRetryUtc = initialDeadline;
                long? lastRemoteEventSize = null;
                DateTime? lastGrowthUtc = null;
                DateTime? lastProbePullUtc = null;
                long lastObservedEventTimeNs = 0;
                var processPoll = 0;
                while (DateTime.UtcNow < eventDeadline)
                {
                    var nowUtc = DateTime.UtcNow;
                    if (processPoll++ % 3 == 0 &&
                        !IsAndroidPackageRunning(adb, adbSerial, packageName))
                    {
                        eventStopReason = "guest-exited";
                        Console.WriteLine("客体进程已退出，提前结束运行时事件观察。");
                        break;
                    }
                    var remoteEventSize = RemoteFileSize(adb, adbSerial, remoteEvents);
                    if (remoteEventSize is > 0)
                    {
                        eventsAttached = true;
                        if (lastRemoteEventSize != remoteEventSize)
                        {
                            lastRemoteEventSize = remoteEventSize;
                            Console.WriteLine($"运行时事件文件已增长到 {remoteEventSize} bytes。");

                            // Pull at most once every two seconds. The remote
                            // stream is append-only, but pulling and reparsing
                            // the whole file on every 1-second poll made large
                            // context probes needlessly expensive.
                            var meaningfulGrowth = false;
                            if (lastProbePullUtc is null ||
                                (nowUtc - lastProbePullUtc.Value).TotalSeconds >= 2)
                            {
                                lastProbePullUtc = nowUtc;
                                if (TryPullGuestFile(adb, adbSerial, remoteEvents, probeEvents))
                                {
                                    foreach (var record in ReadEventRecords(probeEvents, ignoreIncompleteTail: true)
                                                 .OfType<JsonObject>())
                                    {
                                        var eventTimeNs = Int64Value(record, "monotonicTimeNs");
                                        var isNewEvent = eventTimeNs > lastObservedEventTimeNs;
                                        if (eventTimeNs > lastObservedEventTimeNs)
                                            lastObservedEventTimeNs = eventTimeNs;
                                        // The bridge lookup probe can emit a
                                        // high-frequency stream of failed
                                        // interface lookups. It is diagnostic
                                        // noise, not evidence that a Unity
                                        // object was loaded or observed, and it
                                        // must not keep the session alive.
                                        if (isNewEvent && !string.Equals(
                                                StringValue(record, "kind"),
                                                "native-bridge-interface",
                                                StringComparison.Ordinal))
                                            meaningfulGrowth = true;
                                        if (!IsRuntimeObjectRecord(record))
                                            continue;
                                        var typeName = StringValue(record, "typeName");
                                        if (!string.IsNullOrWhiteSpace(typeName))
                                            observedRuntimeTypes.Add(typeName);
                                    }

                                    sceneObserved = observedRuntimeTypes.Any(IsSceneRuntimeType);
                                    if (sceneObserved)
                                        Console.WriteLine(
                                            $"已观察到场景级运行时类型: {string.Join(", ", observedRuntimeTypes.Where(IsSceneRuntimeType).OrderBy(x => x))}");

                                }
                            }
                            if (meaningfulGrowth)
                                lastGrowthUtc = nowUtc;
                        }
                    }

                    // A tap can land before the title/login surface becomes
                    // interactive. Retry a bounded number of times; stop as
                    // soon as a scene sentinel is observed. This avoids both
                    // false success on adb exit code 0 and unbounded input.
                    var afterInitialWindow = nowUtc >= initialDeadline;
                    if (autoEnter && !sceneObserved &&
                        autoEnterAttempts.Count < autoEnterMaxAttempts &&
                        nowUtc >= nextAutoEnterRetryUtc)
                    {
                        Console.WriteLine(
                            $"尚未观察到场景类型，执行受限自动进入重试 " +
                            $"({autoEnterAttempts.Count + 1}/{autoEnterMaxAttempts})。");
                        var retry = TryAutoEnterAndroidGame(
                            adb, adbSerial, autoEnterTapX, autoEnterTapY);
                        autoEnterAttempts.Add(retry.DeepClone());
                        nextAutoEnterRetryUtc = nowUtc.AddSeconds(autoEnterRetryIntervalSeconds);
                    }

                    observationSeconds = (int)Math.Floor((nowUtc - observationStartedUtc).TotalSeconds);
                    var idle = lastGrowthUtc is null
                        ? TimeSpan.Zero
                        : nowUtc - lastGrowthUtc.Value;
                    var idleThresholdReached = lastGrowthUtc is null
                        ? afterInitialWindow
                        : eventIdleSeconds > 0 && idle.TotalSeconds >= eventIdleSeconds;
                    var autoEnterRetriesComplete = !autoEnter ||
                        autoEnterAttempts.Count >= autoEnterMaxAttempts;
                    if (afterInitialWindow && autoEnterRetriesComplete &&
                        (eventIdleSeconds == 0 || idleThresholdReached))
                    {
                        eventStopReason = sceneObserved
                            ? "idle-after-scene"
                            : "idle-without-scene-sentinel";
                        Console.WriteLine(sceneObserved
                            ? "运行时事件已进入场景稳定期，提前结束观察。"
                            : "运行时有效事件已静默，但未观察到场景哨兵；提前结束，避免桥接诊断噪声拖满超时。"
                              + " 如需继续等待，请显式增大 --guest-event-max-seconds。");
                        break;
                    }
                    Thread.Sleep(1000);
                }

                if (eventsAttached)
                {
                    // Pull once after the observation window so the local copy
                    // contains the complete append-only stream.
                    RunAdb(adb, adbSerial, "pull", remoteEvents, localEvents);
                }
            }

            MarkTimingStage("runtime-event-observation", eventStopReason);

            // Native TypeTree generation runs on a delayed guest thread and may
            // finish after the catalog is already available. Poll its output
            // for a bounded period instead of treating an early miss as a
            // failed generation.
            var nativeArtifactDeadline = new[]
            {
                DateTime.UtcNow.AddSeconds(nativeTransferEnabled ? Math.Min(timeoutSeconds, 30) : 0),
                totalDeadline
            }.Min();
            long? lastNativeArtifactSize = null;
            while (nativeTransferEnabled && DateTime.UtcNow < nativeArtifactDeadline)
            {
                var remoteSize = RemoteFileSize(adb, adbSerial, remoteReleaseTypeTree);
                if (remoteSize is > 0)
                {
                    if (lastNativeArtifactSize != remoteSize)
                    {
                        lastNativeArtifactSize = remoteSize;
                        Console.WriteLine($"客体 TypeTree 文件已生成，大小 {remoteSize} bytes。");
                    }
                    if (TryPullGuestFile(adb, adbSerial, remoteReleaseTypeTree, localReleaseTypeTree))
                        break;
                }
                Thread.Sleep(1000);
            }

            if ((!nativeTransferEnabled || File.Exists(localReleaseTypeTree) == false)
                && TryPullGuestFile(adb, adbSerial, remoteReleaseTypeTree, localReleaseTypeTree))
            {
                typeTreeArtifactsInfo = new JsonArray
                {
                    BuildTypeTreeArtifactInfo(
                        Directory.GetParent(runtimeDir)!.FullName,
                        localReleaseTypeTree)
                };
            }
            else if (File.Exists(localReleaseTypeTree))
            {
                typeTreeArtifactsInfo = new JsonArray
                {
                    BuildTypeTreeArtifactInfo(
                        Directory.GetParent(runtimeDir)!.FullName,
                        localReleaseTypeTree)
                };
            }

            if (nativeTransferEnabled)
            {
                nativeGuestProcessRunning = IsAndroidPackageRunning(adb, adbSerial, packageName);
                if (nativeGuestProcessRunning == false)
                    Console.WriteLine("native TypeTree 调用后客体进程已退出；本次结果标记为失败，不视为有效 TypeTree。");
            }

            MarkTimingStage("typetree-artifacts-pulled",
                File.Exists(localReleaseTypeTree) ? "completed" : "empty");

            if (vtableProbeValue == "1" && TryPullGuestFile(adb, adbSerial,
                remoteVtableProbe, localVtableProbe))
            {
                vtableProbeInfo = new JsonObject
                {
                    ["file"] = RelativeTo(Directory.GetParent(runtimeDir)!.FullName, localVtableProbe),
                    ["sourceFile"] = vtableProbeFileName,
                    ["size"] = new FileInfo(localVtableProbe).Length,
                    ["sha256"] = Sha256(localVtableProbe),
                    ["format"] = "TypeTreeRipper.vtable-probe.v1",
                    ["provenance"] = new JsonObject
                    {
                        ["package"] = packageName,
                        ["adbSerial"] = adbSerial,
                        ["remoteFile"] = remoteVtableProbe,
                        ["mode"] = "safe_probe_no_native_typetree"
                    }
                };
            }

            // Ripper diagnostics are emitted through Android logcat rather
            // than the catalog/event files. Preserve a bounded, session-local
            // copy so vtable probes remain auditable without treating logs as
            // TypeTree fields.
            if (vtableProbeValue == "1" || observeRuntimeEvents || nativeTransferEnabled)
            {
                var logcat = RunAdbAllowFailure(adb, adbSerial, "shell", "logcat", "-d", "-t", "2000",
                    "-s", "TypeTreeRipper:D", "*:S");
                if (logcat.ExitCode == 0)
                    File.WriteAllText(localRipperLogcat, logcat.Stdout);
            }
        }
        finally
        {
            // Preserve diagnostics even when the guest never reaches the
            // TypeTree catalog deadline. NativeBridge and loader probes are
            // emitted before catalog generation and must remain inspectable
            // after a failed startup.
            TryPullGuestFile(adb, adbSerial, remoteEvents, localEvents);

            // A cancelled collector may have a valid last snapshot in the
            // disposable probe file even though the final pull was not
            // reached. Preserve that snapshot so Ctrl+C or target exit does
            // not silently turn a non-empty observation into an empty session.
            if (File.Exists(probeEvents))
            {
                var probeLength = new FileInfo(probeEvents).Length;
                var localLength = File.Exists(localEvents) ? new FileInfo(localEvents).Length : 0;
                if (probeLength > localLength)
                    File.Copy(probeEvents, localEvents, overwrite: true);
            }

            // wrap.<package> is a one-launch setting; always clear it so the
            if (observeFactoryEntry && !factoryEntryTerminalObserved)
                RunAdbAllowFailure(adb, adbSerial, "shell", "am", "force-stop", packageName);
            // next normal game launch is not unexpectedly preloaded.
            RunAdbAllowFailure(adb, adbSerial, "shell", $"setprop {wrapProperty} \"\"");
            var remoteCleanup = new List<string>
            {
                remoteCatalog, remoteReleaseTypeTree, remoteEvents, remoteVtableProbe,
                remoteConfig, remoteLibrary
            };
            if (pushArm64Library && !string.IsNullOrWhiteSpace(remoteArm64Library))
                remoteCleanup.Add(remoteArm64Library);
            RunAdbAllowFailure(adb, adbSerial,
                new[] { "shell", "rm", "-f" }.Concat(remoteCleanup).ToArray());
            // The guest Ripper can still be flushing its probe stream while
            // the collector is being cancelled.  Do not turn that harmless
            // close race into a failed session, and keep the probe file if a
            // writer still owns it after the bounded retry window.
            TryDeleteFile(probeCatalog);
            TryDeleteFile(probeEvents);

            MarkTimingStage("cleanup");
            var timingPath = Path.Combine(runtimeDir, "timing.json");
            var timing = new JsonObject
            {
                ["format"] = "my-hook.experiment-timing.v1",
                ["captureSessionId"] = captureSessionId,
                ["package"] = packageName,
                ["adbSerial"] = adbSerial,
                ["totalElapsedSeconds"] = Math.Round(experimentStopwatch.Elapsed.TotalSeconds, 3),
                ["stages"] = timingStages.DeepClone()
            };
            File.WriteAllText(timingPath, timing.ToJsonString(JsonOptions));
        }

        var hookDir = Directory.GetParent(runtimeDir)!.FullName;
        var eventRecords = ReadEventRecords(localEvents, ignoreIncompleteTail: true);
        foreach (var record in eventRecords.OfType<JsonObject>())
        {
            if (!IsRuntimeObjectRecord(record))
                continue;
            var typeName = StringValue(record, "typeName");
            if (!string.IsNullOrWhiteSpace(typeName))
                observedRuntimeTypes.Add(typeName);
        }
        sceneObserved |= observedRuntimeTypes.Any(IsSceneRuntimeType);
        var eventsInfo = new JsonObject
        {
            ["status"] = eventRecords.Count == 0 ? "empty" : "attached",
            ["coverage"] = eventRecords.Count == 0
                ? "empty"
                : sceneObserved ? "scene-observed" : "startup-only",
            ["format"] = "my-hook.runtime-event.v1",
            ["file"] = RelativeTo(hookDir, localEvents),
            ["sourceFile"] = eventsFileName,
            ["size"] = new FileInfo(localEvents).Length,
            ["sha256"] = Sha256(localEvents),
            ["count"] = eventRecords.Count,
                ["observation"] = new JsonObject
                {
                ["initialWindowSeconds"] = eventTimeoutSeconds,
                ["maxWindowSeconds"] = eventMaxSeconds,
                ["totalTimeoutSeconds"] = totalTimeoutSeconds,
                    ["observedSeconds"] = observationSeconds,
                    ["stopReason"] = eventStopReason,
                    ["sceneObserved"] = sceneObserved,
                    ["autoEnter"] = autoEnter,
                    ["autoEnterMaxAttempts"] = autoEnterMaxAttempts,
                    ["autoEnterRetryIntervalSeconds"] = autoEnterRetryIntervalSeconds,
                    ["autoEnterAction"] = autoEnterInfo,
                    ["autoEnterAttempts"] = autoEnterAttempts,
                ["sceneTypes"] = new JsonArray(
                    observedRuntimeTypes.Where(IsSceneRuntimeType).OrderBy(x => x)
                        .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
            },
            ["provenance"] = new JsonObject
            {
                ["package"] = packageName,
                ["adbSerial"] = adbSerial,
                ["remoteFile"] = remoteEvents,
                ["observer"] = "Unity runtime vtable transfer observation"
            }
        };
        var catalogInfo = new JsonObject
        {
            ["status"] = "attached",
            ["format"] = "TypeTreeRipper.runtime-catalog.v1",
            ["file"] = RelativeTo(hookDir, localCatalog),
            ["sourceFile"] = Path.GetFileName(localCatalog),
            ["size"] = new FileInfo(localCatalog).Length,
            ["sha256"] = Sha256(localCatalog),
            ["provenance"] = new JsonObject
            {
                ["package"] = packageName,
                ["adbSerial"] = adbSerial,
                ["guestRipper"] = Path.GetFileName(guestRipper),
                ["guestRipperSha256"] = Sha256(guestRipper),
                ["packageVersion"] = packageVersion,
                ["config"] = Path.GetFileName(configPath),
                ["configSha256"] = Sha256(configPath),
                ["remoteFile"] = remoteCatalog
            },
            ["events"] = eventsInfo
        };
        var timingInfoPath = Path.Combine(runtimeDir, "timing.json");
        if (File.Exists(timingInfoPath))
        {
            catalogInfo["timing"] = new JsonObject
            {
                ["format"] = "my-hook.experiment-timing.v1",
                ["file"] = RelativeTo(hookDir, timingInfoPath),
                ["size"] = new FileInfo(timingInfoPath).Length,
                ["sha256"] = Sha256(timingInfoPath)
            };
        }
        catalogInfo["execution"] = new JsonObject
        {
            ["catalogReady"] = true,
            ["processRunningAfterCatalog"] = guestProcessRunningAfterCatalog,
            ["status"] = guestProcessRunningAfterCatalog == false
                ? "guest_exited_after_catalog"
                : "catalog_ready"
        };
        var vtableProbeRecords = eventRecords
            .OfType<JsonObject>()
            .Where(record => StringValue(record, "kind") == "unity.vtable-probe"
                || StringValue(record, "format") == "TypeTreeRipper.vtable-probe.v1")
            .ToArray();
        if (vtableProbeRecords.Length > 0)
        {
            var probedTypes = vtableProbeRecords
                .Select(record => StringValue(record, "typeName"))
                .Where(typeName => !string.IsNullOrWhiteSpace(typeName))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(typeName => typeName, StringComparer.Ordinal)
                .Select(typeName => (JsonNode?)JsonValue.Create(typeName))
                .ToArray();
            catalogInfo["vtableProbe"] = new JsonObject
            {
                ["format"] = "TypeTreeRipper.vtable-probe.v1",
                ["mode"] = "safe_probe_no_native_typetree",
                ["source"] = "runtime events",
                ["file"] = RelativeTo(hookDir, localEvents),
                ["recordCount"] = vtableProbeRecords.Length,
                ["typeCount"] = probedTypes.Length,
                ["types"] = new JsonArray(probedTypes)
            };
        }
        if (typeTreeArtifactsInfo is not null)
            catalogInfo["artifacts"] = typeTreeArtifactsInfo;
        if (File.Exists(localRipperLogcat))
        {
            catalogInfo["ripperLogcat"] = new JsonObject
            {
                ["file"] = RelativeTo(hookDir, localRipperLogcat),
                ["sourceFile"] = Path.GetFileName(localRipperLogcat),
                ["size"] = new FileInfo(localRipperLogcat).Length,
                ["sha256"] = Sha256(localRipperLogcat),
                ["provenance"] = new JsonObject
                {
                    ["package"] = packageName,
                    ["adbSerial"] = adbSerial,
                    ["source"] = "adb logcat TypeTreeRipper:D",
                    ["boundedLines"] = 2000
                }
            };
        }
        if (vtableProbeInfo is not null)
            catalogInfo["vtableProbe"] = vtableProbeInfo;
        if (factoryEntryInfo is not null)
            catalogInfo["factoryEntryObservation"] = factoryEntryInfo;
        if (localAndroidErrorLogcat is not null && File.Exists(localAndroidErrorLogcat))
        {
            catalogInfo["androidErrorLogcat"] = new JsonObject
            {
                ["file"] = RelativeTo(hookDir, localAndroidErrorLogcat),
                ["sourceFile"] = Path.GetFileName(localAndroidErrorLogcat),
                ["size"] = new FileInfo(localAndroidErrorLogcat).Length,
                ["sha256"] = Sha256(localAndroidErrorLogcat),
                ["provenance"] = new JsonObject
                {
                    ["package"] = packageName,
                    ["adbSerial"] = adbSerial,
                    ["source"] = "adb logcat -d -t 1000 *:E",
                    ["boundedLines"] = 1000
                }
            };
        }
        if (typeTreeBaselinePath is not null && File.Exists(typeTreeBaselinePath))
        {
            var baselinePath = Path.Combine(runtimeDir, "typetree", "baseline.ttbin");
            File.Copy(typeTreeBaselinePath, baselinePath, overwrite: true);
            var baselineInfo = BuildTypeTreeArtifactInfo(hookDir, baselinePath);
            ValidateProfileBaseline(baselineInfo, typeTreeBaselineProfile, typeTreeBaselinePath);
            baselineInfo["status"] = Int64Value(baselineInfo, "totalNodes") > 0
                    ? "offline_baseline"
                    : "offline_baseline_invalid";
            baselineInfo["provenance"] = new JsonObject
            {
                ["source"] = "profile typeTreeBaseline",
                ["sourcePath"] = typeTreeBaselinePath,
                ["runtimeGenerated"] = false
            };
            catalogInfo["baseline"] = baselineInfo;
        }
        if (nativeTransferEnabled)
        {
            var usableNativeTypeTree = typeTreeArtifactsInfo?.OfType<JsonObject>()
                .Any(artifact => StringValue(artifact, "status") == "attached"
                    && artifact["totalNodes"] is JsonValue nodes
                    && nodes.TryGetValue<long>(out var nodeCount)
                    && nodeCount > 0) == true;
            catalogInfo["nativeTransfer"] = new JsonObject
            {
                ["status"] = nativeGuestProcessRunning == false
                    ? "guest_process_exited"
                    : usableNativeTypeTree
                        ? "completed"
                        : typeTreeArtifactsInfo is null
                        ? "no_release_artifact"
                        : "decoded_empty",
                ["processRunningAfterCall"] = nativeGuestProcessRunning ?? false,
                ["usable"] = usableNativeTypeTree,
                ["safety"] = "explicit opt-in; native ABI is not considered valid without non-empty TypeTree nodes"
            };
        }
        if (catalogInfo["baseline"] is JsonObject baselineEvidenceInfo)
        {
            catalogInfo["baselineEvidence"] = new JsonObject
            {
                ["status"] = StringValue(baselineEvidenceInfo, "status"),
                ["totalNodes"] = Int64Value(baselineEvidenceInfo, "totalNodes"),
                ["runtimeGenerated"] = false,
                ["identity"] = "profile-pinned TypeTree artifact; not evidence from this guest process"
            };
        }
        return catalogInfo;
    }

    private static JsonObject BuildTypeTreeArtifactInfo(string hookDirectory, string path)
    {
        var info = new JsonObject
        {
            ["format"] = "TypeTreeRipper.ttbin",
            ["file"] = RelativeTo(hookDirectory, path),
            ["size"] = new FileInfo(path).Length,
            ["sha256"] = Sha256(path),
            ["provenance"] = "guest TypeTreeRipper release output"
        };

        try
        {
            var decoded = ReadTypeTreeBinary(path);
            var totalNodes = decoded["totalNodes"]?.GetValue<long>() ?? 0;
            info["totalNodes"] = totalNodes;
            info["treeCount"] = decoded["treeCount"]?.DeepClone();
            info["header"] = decoded["header"]?.DeepClone();
            var trees = decoded["trees"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
            var uniqueTrees = new List<JsonObject>();
            var typeSummaries = new JsonArray();
            foreach (var tree in trees)
            {
                var rtti = tree["rtti"] as JsonObject;
                var annotated = (JsonObject)tree.DeepClone();
                var fieldIntegrity = AnalyzeTypeTreeFields(annotated);
                typeSummaries.Add(new JsonObject
                {
                    ["className"] = rtti?["className"]?.DeepClone(),
                    ["persistentTypeId"] = rtti?["persistentTypeId"]?.DeepClone(),
                    ["nodeCount"] = tree["nodeCount"]?.DeepClone(),
                    ["fieldIntegrity"] = fieldIntegrity
                });
                if (!uniqueTrees.Any(existing => JsonNode.DeepEquals(existing, tree)))
                    uniqueTrees.Add(tree);
            }
            info["rawTreeCount"] = trees.Length;
            info["uniqueTreeCount"] = uniqueTrees.Count;
            info["duplicateTreeCount"] = trees.Length - uniqueTrees.Count;
            info["types"] = typeSummaries;
            info["status"] = totalNodes == 0 ? "decoded_empty" : "attached";
            if (totalNodes == 0)
                info["reason"] = "The binary is structurally valid but contains no field nodes; it is not a complete TypeTree.";
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            info["status"] = "invalid";
            info["reason"] = ex.Message;
        }

        return info;
    }

    private static void AddDecodedTypeTreeFieldIntegrity(JsonObject decoded)
    {
        if (decoded["trees"] is not JsonArray trees)
            return;

        var reports = new JsonArray();
        foreach (var tree in trees.OfType<JsonObject>())
        {
            var integrity = AnalyzeTypeTreeFields(tree);
            tree["fieldIntegrity"] = integrity;
            reports.Add(new JsonObject
            {
                ["result"] = integrity.DeepClone()
            });
        }

        if (reports.Count > 0)
            decoded["fieldIntegrity"] = SummarizeTypeTreeFields(reports);
    }

    private static void ValidateProfileBaseline(
        JsonObject artifact,
        JsonObject? profile,
        string sourcePath)
    {
        if (profile is null)
            throw new InvalidDataException(
                $"TypeTree 基线未提供 Profile 校验信息: {sourcePath}");

        var expectedHash = StringValue(profile, "sha256");
        if (!string.IsNullOrWhiteSpace(expectedHash) &&
            !string.Equals(expectedHash, StringValue(artifact, "sha256"),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"TypeTree 基线 SHA-256 不匹配: {sourcePath}");

        var expectedSize = Int64Value(profile, "size");
        if (expectedSize > 0 && expectedSize != Int64Value(artifact, "size"))
            throw new InvalidDataException(
                $"TypeTree 基线大小不匹配: {sourcePath}");

        var expectedTrees = Int64Value(profile, "treeCount");
        if (expectedTrees > 0 && expectedTrees != Int64Value(artifact, "treeCount"))
            throw new InvalidDataException(
                $"TypeTree 基线树数量不匹配: {sourcePath}");

        var expectedNodes = Int64Value(profile, "totalNodes");
        if (expectedNodes > 0 && expectedNodes != Int64Value(artifact, "totalNodes"))
            throw new InvalidDataException(
                $"TypeTree 基线节点数量不匹配: {sourcePath}");

        var header = artifact["header"] as JsonObject;
        var expectedRevision = StringValue(profile, "revision");
        if (!string.IsNullOrWhiteSpace(expectedRevision) &&
            !string.Equals(expectedRevision, StringValue(header ?? [], "revision"),
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"TypeTree 基线 Unity revision 不匹配: {sourcePath}");

        var expectedVariant = StringValue(profile, "variant");
        if (!string.IsNullOrWhiteSpace(expectedVariant) &&
            !string.Equals(expectedVariant, StringValue(header ?? [], "variant"),
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"TypeTree 基线 variant 不匹配: {sourcePath}");
    }

    private static void ValidateClassIds(string classIds)
    {
        foreach (var value in classIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(value, out var classId) || classId < 0)
                throw new ArgumentException("--guest-class-ids 必须是逗号分隔的非负整数");
        }
    }

    private static string? TryGetAndroidPackageVersion(string adb, string serial, string packageName)
    {
        var result = RunAdbAllowFailure(adb, serial, "shell", "dumpsys", "package", packageName);
        if (result.ExitCode != 0)
            return null;

        var match = Regex.Match(
            result.Stdout + Environment.NewLine + result.Stderr,
            @"(?m)^\s*versionName=([^\s]+)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string ResolveInstalledArm64LibraryPath(
        string adb,
        string serial,
        string packageName,
        string libraryFileName)
    {
        var result = RunAdbAllowFailure(adb, serial, "shell", "pm", "path", packageName);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"无法查询客体 APK 路径，不能安全放置 ARM64 Native Bridge 库: {result.Stderr}");

        var apkPath = result.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("package:", StringComparison.Ordinal))
            .Select(line => line["package:".Length..])
            .FirstOrDefault(path => path.EndsWith("/base.apk", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(apkPath))
            throw new InvalidDataException(
                $"pm path 未返回可识别的 base.apk，不能猜测 ARM64 库目录: {result.Stdout}");

        var apkDirectory = apkPath[..apkPath.LastIndexOf('/')];
        var libraryDirectory = apkDirectory + "/lib/arm64";
        var libraryPath = libraryDirectory + "/" + libraryFileName;

        var directoryCheck = RunAdbAllowFailure(
            adb, serial, "shell", "test", "-d", libraryDirectory);
        if (directoryCheck.ExitCode != 0)
            throw new InvalidOperationException(
                $"客体 APK 没有 ARM64 native-lib 目录: {libraryDirectory}");
        return libraryPath;
    }

    private static string ValidateNativeBridgeProbeFileName(string value)
    {
        if (value.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
            !value.EndsWith(".so", StringComparison.Ordinal) ||
            value is "." or "..")
            throw new ArgumentException(
                "MY_HOOK_NATIVE_BRIDGE_PROBE_EXISTING 必须是 APK ARM64 lib 目录中的 .so 文件名");
        return value;
    }

    private static void EnsureAdbRoot(string adb, string serial, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 5, 60));
        var lastError = "设备尚未完成 adbd 重启";
        while (DateTime.UtcNow < deadline)
        {
            var root = RunAdbAllowFailure(adb, serial, "root");
            if (root.ExitCode == 0)
            {
                var identity = RunAdbAllowFailure(adb, serial, "shell", "id");
                if (identity.ExitCode == 0 &&
                    Regex.IsMatch(identity.Stdout, @"uid=0(?:\(|\s|$)",
                        RegexOptions.CultureInvariant))
                    return;

                lastError = string.IsNullOrWhiteSpace(identity.Stderr)
                    ? identity.Stdout.Trim()
                    : identity.Stderr.Trim();
            }
            else
            {
                lastError = string.IsNullOrWhiteSpace(root.Stderr)
                    ? root.Stdout.Trim()
                    : root.Stderr.Trim();
            }

            // MuMu may close the ADB transport while switching adbd to root.
            // Retry only within the startup deadline; a persistent failure is
            // still rejected below rather than silently falling back to shell.
            Thread.Sleep(1000);
        }

        throw new InvalidOperationException(
            $"MUMU ADB root 未在启动期限内生效，不能把 ARM64 Bridge 库放入 APK native-lib 目录: {lastError}");
    }

    private static bool IsSceneRuntimeType(string typeName)
    {
        // The startup/loading presentation also creates static MeshRenderer
        // and Light objects. Require a character/effect-side renderer so a
        // loading screen cannot mark the session as scene-ready.
        return typeName is "SkinnedMeshRenderer" or "ParticleSystem"
            or "ParticleSystemRenderer" or "LineRenderer" or "TrailRenderer";
    }

    private static bool IsRuntimeObjectRecord(JsonObject record)
    {
        return StringValue(record, "kind") is "unity.transfer" or "unity.typetree";
    }

    private static void WaitForAndroidUnityRuntime(
        string adb,
        string serial,
        string packageName,
        string remoteEvents,
        string localProbePath,
        int timeoutSeconds)
    {
        Console.WriteLine($"等待 {packageName} 的 Unity 原生运行时加载，最长 {timeoutSeconds} 秒...");
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var lastMapsError = string.Empty;
        var lastEventSize = 0L;
        while (DateTime.UtcNow < deadline)
        {
            if (TryPullGuestFile(adb, serial, remoteEvents, localProbePath))
            {
                var eventText = File.ReadAllText(localProbePath);
                lastEventSize = new FileInfo(localProbePath).Length;
                if (eventText.Contains("\"kind\":\"native-bridge-extra-load\"", StringComparison.Ordinal)
                    && eventText.Contains("libil2cpp.so", StringComparison.Ordinal))
                {
                    Console.WriteLine("已检测到 Unity 原生运行时：ARM64 Bridge 已随 libil2cpp.so 加载。");
                    return;
                }
                if (eventText.Contains("\"kind\":\"unity.transfer-observer-installed\"", StringComparison.Ordinal)
                    || eventText.Contains("\"kind\":\"unity.typetree\"", StringComparison.Ordinal)
                    || eventText.Contains("\"kind\":\"unity.transfer\"", StringComparison.Ordinal))
                {
                    Console.WriteLine("已检测到 Unity 原生运行时：运行时观察事件已产生。");
                    return;
                }
            }

            var pidResult = RunAdbAllowFailure(adb, serial, "shell", "pidof", packageName);
            var processId = pidResult.Stdout
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(processId))
            {
                var maps = RunAdbAllowFailure(
                    adb, serial, "shell", "cat", $"/proc/{processId}/maps");
                if (maps.ExitCode != 0)
                    lastMapsError = (maps.Stderr + maps.Stdout).Trim();
                var hasUnity = maps.ExitCode == 0
                    && maps.Stdout.Contains("libunity.so", StringComparison.Ordinal);
                var hasIl2Cpp = maps.ExitCode == 0
                    && maps.Stdout.Contains("libil2cpp.so", StringComparison.Ordinal);
                if (hasUnity && hasIl2Cpp)
                {
                    Console.WriteLine($"已检测到 Unity 原生运行时：PID {processId}。");
                    return;
                }
            }
            Thread.Sleep(1000);
        }

        throw new TimeoutException(
            $"客体包 {packageName} 在 {timeoutSeconds} 秒内未加载 libunity.so/libil2cpp.so；"
            + $"当前可能仍停留在 SDK 登录界面，未开始运行时身份实验。events={lastEventSize} bytes"
            + (string.IsNullOrWhiteSpace(lastMapsError)
                ? ""
                : $", /proc/maps={lastMapsError}"));
    }

    private static bool WaitForAndroidPackageForeground(
        string adb,
        string serial,
        string packageName,
        int timeoutSeconds)
    {
        Console.WriteLine($"等待客体包 {packageName} 进入前台，最长 {timeoutSeconds} 秒...");
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var result = RunAdbAllowFailure(
                adb,
                serial,
                "shell",
                "dumpsys",
                "activity",
                "activities");
            var output = result.Stdout + Environment.NewLine + result.Stderr;
            var foreground = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(line =>
                    (line.Contains("mFocusedApp=", StringComparison.Ordinal) ||
                     line.Contains("mResumedActivity=", StringComparison.Ordinal) ||
                     line.Contains("topResumedActivity=", StringComparison.Ordinal) ||
                     line.Contains("mCurrentFocus=", StringComparison.Ordinal)) &&
                    line.Contains(packageName, StringComparison.Ordinal));
            if (foreground)
                return true;
            if (IsAndroidPackageRunning(adb, serial, packageName))
            {
                Console.WriteLine($"已检测到客体进程 {packageName} 运行，但前台窗口状态未知。");
                return false;
            }
            Thread.Sleep(1000);
        }

        throw new TimeoutException(
            $"客体包 {packageName} 未在 {timeoutSeconds} 秒内进入前台，未触发提示音；请确认 MuMu 未停留在启动器且程序未闪退。");
    }

    private static void NotifyLoginReady(bool autoEnter)
    {
        Console.WriteLine(autoEnter
            ? "HSR 已进入前台；工具已执行一次自动进入操作，继续等待游戏场景运行时数据。"
            : "HSR 已进入前台；请完成账号认证并进入游戏场景，工具会持续监听运行时数据。");
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            Console.Beep(880, 350);
            Thread.Sleep(120);
            Console.Beep(1046, 500);
        }
        catch (PlatformNotSupportedException)
        {
            // Console.Beep is optional on non-Windows hosts.
        }
    }

    private static JsonObject TryAutoEnterAndroidGame(
        string adb,
        string serial,
        double normalizedX,
        double normalizedY)
    {
        var foregroundActivity = GetForegroundAndroidActivity(adb, serial);
        if (foregroundActivity?.Contains("com.android.chrome", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine("自动进入操作未执行：当前前台是 Chrome/外部更新页面，保留 HSR 4.5 状态。");
            return new JsonObject
            {
                ["status"] = "blocked",
                ["reason"] = "external-update-page",
                ["foregroundActivity"] = foregroundActivity
            };
        }

        var action = "game-entry";
        var effectiveNormalizedY = normalizedY;
        if (foregroundActivity?.EndsWith("/ProtocolActivity", StringComparison.Ordinal) == true)
        {
            // The first-run privacy screen is an Android SDK activity rather
            // than the Unity login surface. Its accept button is separate from
            // the normal game-entry coordinate.
            action = "protocol-accept";
            effectiveNormalizedY = 0.722;
        }

        // Do not use `uiautomator dump` here. On MuMu/Houdini it can crash the
        // uiautomator helper while the Unity guest is healthy, which used to
        // make the whole collection look like a guest failure. The normalized
        // tap is profile data; wm/input provide enough display geometry.
        var display = RunAdbAllowFailure(adb, serial, "shell", "wm", "size");
        var displayMatch = Regex.Match(
            display.Stdout + Environment.NewLine + display.Stderr,
            "(?:Override|Physical) size:\\s*(?<width>[0-9]+)x(?<height>[0-9]+)",
            RegexOptions.CultureInvariant);
        var input = RunAdbAllowFailure(adb, serial, "shell", "dumpsys", "input");
        var rotationMatch = Regex.Match(
            input.Stdout + Environment.NewLine + input.Stderr,
            "SurfaceOrientation:\\s*(?<rotation>[0-9]+)",
            RegexOptions.CultureInvariant);
        var width = 0;
        var height = 0;
        var hasBounds = displayMatch.Success &&
            int.TryParse(displayMatch.Groups["width"].Value, out width) &&
            int.TryParse(displayMatch.Groups["height"].Value, out height) &&
            width > 0 && height > 0;
        if (hasBounds && int.TryParse(rotationMatch.Groups["rotation"].Value, out var rotation)
            && rotation % 2 == 1)
        {
            (width, height) = (height, width);
        }
        const string boundsSource = "wm-size+surface-orientation";
        if (!hasBounds)
        {
            Console.WriteLine("自动进入操作未执行：无法读取当前 Unity 窗口尺寸。");
            return new JsonObject
            {
                ["status"] = "not-executed",
                ["reason"] = "unable-to-read-current-window-bounds",
                ["boundsSource"] = boundsSource
            };
        }

        var x = Math.Clamp((int)Math.Round(width * normalizedX), 0, width - 1);
        var y = Math.Clamp((int)Math.Round(height * effectiveNormalizedY), 0, height - 1);
        var tap = RunAdbAllowFailure(
            adb, serial, "shell", "input", "tap",
            x.ToString(CultureInfo.InvariantCulture),
            y.ToString(CultureInfo.InvariantCulture));
        var status = tap.ExitCode == 0 ? "sent" : "failed";
        Console.WriteLine($"自动进入操作: {status}, action={action}, window={width}x{height}, tap=({x},{y})");
        return new JsonObject
        {
            ["status"] = status,
            ["action"] = action,
            ["foregroundActivity"] = foregroundActivity,
            ["windowWidth"] = width,
            ["windowHeight"] = height,
            ["boundsSource"] = boundsSource,
            ["normalizedX"] = normalizedX,
            ["normalizedY"] = effectiveNormalizedY,
            ["tapX"] = x,
            ["tapY"] = y,
            ["exitCode"] = tap.ExitCode,
            ["stderr"] = string.IsNullOrWhiteSpace(tap.Stderr) ? null : tap.Stderr.Trim()
        };
    }

    private static string? GetForegroundAndroidActivity(string adb, string serial)
    {
        var result = RunAdbAllowFailure(adb, serial, "shell", "dumpsys", "activity", "activities");
        var text = result.Stdout + Environment.NewLine + result.Stderr;
        var match = Regex.Match(
            text,
            @"(?:topResumedActivity|mResumedActivity)=ActivityRecord\{[^}]*\s(?<activity>[^\s}]+)",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["activity"].Value : null;
    }

    private static bool IsAndroidPackageRunning(string adb, string serial, string packageName)
    {
        var result = RunAdbAllowFailure(adb, serial, "shell", "pidof", packageName);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Stdout);
    }

    private static void ValidateRuntimeCatalog(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 12 ||
            !string.Equals(lines[0], "persistentTypeID|size|abstract|factory|className",
                StringComparison.Ordinal))
            throw new InvalidDataException("客体 TypeTree 目录格式无效或内容过少");

        var required = new[]
        {
            "Object", "GameObject", "Renderer", "SkinnedMeshRenderer",
            "ParticleSystem", "Shader", "Material", "Mesh", "AnimationClip"
        };
        var names = lines.Skip(1)
            .Select(line => line.Split('|').LastOrDefault())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        var missing = required.Where(name => !names.Contains(name)).ToArray();
        if (missing.Length != 0)
            throw new InvalidDataException($"客体 TypeTree 目录缺少关键类型: {string.Join(", ", missing)}");
    }

    private static string ResolveBundledGuestRipper(JsonObject profile, string? profilePath)
    {
        var relativePath = StringValue(profile, "guestRipperPath")
            ?? throw new InvalidDataException("Profile 未配置 guestRipperPath");

        var candidates = new List<string>();
        if (profilePath is not null)
        {
            var profileDirectory = Path.GetDirectoryName(FullPath(profilePath))!;
            candidates.Add(Path.Combine(profileDirectory, "..", relativePath));
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, relativePath));

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), relativePath));
        foreach (var candidate in candidates)
        {
            var path = Path.GetFullPath(candidate);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(
            $"找不到 Profile 指定的内置客体 Ripper: {relativePath}。可使用 --guest-ripper 临时覆盖。",
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath)));
    }

    private static string? ResolveOptionalBundledGuestRipper(
        JsonObject profile, string? profilePath, string profileKey)
    {
        var relativePath = StringValue(profile, profileKey);
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var candidates = new List<string>();
        if (profilePath is not null)
        {
            var profileDirectory = Path.GetDirectoryName(FullPath(profilePath))!;
            candidates.Add(Path.Combine(profileDirectory, "..", relativePath));
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, relativePath));

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), relativePath));
        foreach (var candidate in candidates)
        {
            var path = Path.GetFullPath(candidate);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(
            $"找不到 Profile 指定的可选客体 Ripper: {relativePath}",
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath)));
    }

    private static void AttachGuestArtifact(string hookPath, JsonObject catalogInfo)
    {
        var hook = JsonNode.Parse(File.ReadAllText(hookPath))?.AsObject()
            ?? throw new InvalidDataException(".hook 不是 JSON 对象");
        hook["typetree"] = catalogInfo;
        hook["guest"] = catalogInfo.DeepClone();
        if (catalogInfo["events"] is JsonObject eventsInfo)
        {
            var hookDir = Path.GetDirectoryName(hookPath)!;
            var eventFile = eventsInfo["file"]?.GetValue<string>() ?? "runtime/events.ndjson";
            var eventsPath = Path.GetFullPath(Path.Combine(
                hookDir, eventFile.Replace('/', Path.DirectorySeparatorChar)));
            hook["records"] = ReadEventRecords(eventsPath, ignoreIncompleteTail: true);
            hook["events"] = eventsInfo.DeepClone();
        }
        if (hook["session"] is JsonObject session)
        {
            var records = hook["records"] as JsonArray ?? new JsonArray();
            var recordCount = records.Count;
            var runtimeRecords = records.OfType<JsonObject>().ToArray();
            var configuredPPtrCallChainRecords = runtimeRecords
                .Where(record => record["configuredPPtrToLookupCallChain"] is JsonObject chain
                    && string.Equals(StringValue(chain, "status"), "matched", StringComparison.Ordinal))
                .ToArray();
            var typedObjectCorrelationRecords = runtimeRecords
                .Where(record => record["pptrToTypedObjectCorrelation"] is JsonObject)
                .ToArray();
            var resolverCandidateRecords = runtimeRecords
                .Where(record => StringValue(record, "kind") == "unity.serialized-object-resolve-candidate")
                .ToArray();
            var resolverTypedReturnRecords = resolverCandidateRecords
                .Where(record => record["returnTypeObserved"] is JsonValue observed
                    && observed.TryGetValue<bool>(out var value)
                    && value
                    && record["returnVtableObservedType"] is JsonObject)
                .ToArray();
            var resolverTypeCounts = new JsonObject();
            var resolverTypeEventSequences = new JsonArray();
            foreach (var record in resolverTypedReturnRecords)
            {
                var observedType = record["returnVtableObservedType"] as JsonObject;
                var className = StringValue(observedType ?? [], "className") ?? "<unknown>";
                resolverTypeCounts[className] = Int64Value(resolverTypeCounts, className) + 1;
                if (record["eventSequence"] is JsonNode eventSequence)
                    resolverTypeEventSequences.Add(eventSequence.DeepClone());
            }
            var returnTypeObservedCount = typedObjectCorrelationRecords.Count(record =>
                record["pptrToTypedObjectCorrelation"] is JsonObject correlation
                && correlation["returnTypeObserved"] is JsonValue observed
                && observed.TryGetValue<bool>(out var value)
                && value);
            var exactMaterialShaderBindingCount = typedObjectCorrelationRecords.Count(record =>
                record["pptrToTypedObjectCorrelation"] is JsonObject correlation
                && correlation["exactMaterialShaderFieldBinding"] is JsonValue binding
                && binding.TryGetValue<bool>(out var value)
                && value);
            var transferInvocationTokenCount = runtimeRecords.Count(record =>
                record["transferInvocationId"] is JsonValue);
            var fieldInvocationTokenCount = runtimeRecords.Count(record =>
                record["activeTransferCallChain"] is JsonObject chain
                && chain["invocationId"] is JsonValue);
            var resolverInvocationTokenCount = resolverCandidateRecords.Count(record =>
                record["activeTransferCallChain"] is JsonObject chain
                && chain["invocationId"] is JsonValue);
            var configuredPPtrEventSequences = new JsonArray();
            foreach (var record in configuredPPtrCallChainRecords)
            {
                if (record["eventSequence"] is JsonNode eventSequence)
                    configuredPPtrEventSequences.Add(eventSequence.DeepClone());
            }
            var observerInstallCount = records.OfType<JsonObject>()
                .Count(record => StringValue(record, "kind") == "unity.transfer-observer-installed");
            var transferCallbackCount = records.OfType<JsonObject>()
                .Count(record => StringValue(record, "kind") == "unity.transfer");
            var nonEmptyTypeTree = catalogInfo["artifacts"] is JsonArray artifacts
                && artifacts.OfType<JsonObject>().Any(artifact =>
                    StringValue(artifact, "status") == "attached"
                    && Int64Value(artifact, "totalNodes") > 0);
            var nativeFailed = catalogInfo["nativeTransfer"] is JsonObject nativeTransfer
                && nativeTransfer["status"]?.GetValue<string>() == "guest_process_exited";
            var nativeRequestedWithoutOutput = catalogInfo["nativeTransfer"] is JsonObject
                && !nonEmptyTypeTree;
            var guestExitedAfterCatalog = catalogInfo["execution"] is JsonObject execution
                && execution["processRunningAfterCatalog"] is JsonValue processRunning
                && processRunning.TryGetValue<bool>(out var running)
                && !running;
            var offlineBaselineUsable = catalogInfo["baseline"] is JsonObject baseline
                && StringValue(baseline, "status") == "offline_baseline"
                && Int64Value(baseline, "totalNodes") > 0;
            session["evidence"] = new JsonObject
            {
                ["observerInstallCount"] = observerInstallCount,
                ["transferCallbackCount"] = transferCallbackCount,
                ["nonEmptyTypeTree"] = nonEmptyTypeTree,
                ["offlineBaselineUsable"] = offlineBaselineUsable,
                ["configuredPPtrToLookupCallChainCount"] = configuredPPtrCallChainRecords.Length,
                ["typedObjectCorrelationCount"] = typedObjectCorrelationRecords.Length,
                ["returnTypeObservedCount"] = returnTypeObservedCount,
                ["exactMaterialShaderFieldBindingCount"] = exactMaterialShaderBindingCount,
                ["resolverCandidateCount"] = resolverCandidateRecords.Length,
                ["resolverTypedReturnCount"] = resolverTypedReturnRecords.Length,
                ["transferInvocationTokenCount"] = transferInvocationTokenCount,
                ["fieldInvocationTokenCount"] = fieldInvocationTokenCount,
                ["resolverInvocationTokenCount"] = resolverInvocationTokenCount
            };
            hook["runtimeEvidence"] = new JsonObject
            {
                ["schema"] = "my-hook.runtime-identity-evidence.v1",
                ["runtimeCallChain"] = new JsonObject
                {
                    ["status"] = configuredPPtrCallChainRecords.Length > 0 ? "observed" : "not_observed",
                    ["matchedCount"] = configuredPPtrCallChainRecords.Length,
                    ["eventSequences"] = configuredPPtrEventSequences,
                    ["meaning"] = "same-thread PPtr output to lookup-key dataflow with statically verified lookup branch; not a Material/Shader or RenderDoc EID identity"
                },
                ["typedObjectCorrelation"] = new JsonObject
                {
                    ["status"] = returnTypeObservedCount > 0 ? "observed" : "not_observed",
                    ["recordCount"] = typedObjectCorrelationRecords.Length,
                    ["returnTypeObservedCount"] = returnTypeObservedCount,
                    ["exactMaterialShaderFieldBindingCount"] = exactMaterialShaderBindingCount
                },
                ["resolverTypeObservation"] = new JsonObject
                {
                    ["status"] = resolverTypedReturnRecords.Length > 0 ? "observed" : "not_observed",
                    ["resolverCandidateCount"] = resolverCandidateRecords.Length,
                    ["typedReturnCount"] = resolverTypedReturnRecords.Length,
                    ["classNameCounts"] = resolverTypeCounts,
                    ["eventSequences"] = resolverTypeEventSequences,
                    ["meaning"] = "the resolver returned an object whose runtime vtable was mapped to a TypeTree type in the same event; this is not a Material field or sourceFile#PathID identity"
                },
                ["invocationTokenEvidence"] = new JsonObject
                {
                    ["status"] = fieldInvocationTokenCount > 0 ? "observed" : "not_observed",
                    ["transferRecordCount"] = transferInvocationTokenCount,
                    ["fieldRecordCount"] = fieldInvocationTokenCount,
                    ["resolverRecordCount"] = resolverInvocationTokenCount,
                    ["meaning"] = "a token is accepted only when emitted by the same real Transfer call chain; thread, order, address and opaque values are not substituted"
                },
                ["crossSourceIdentity"] = new JsonObject
                {
                    ["status"] = "not_observed",
                    ["materialToShader"] = false,
                    ["sourceFilePathId"] = false,
                    ["renderDocEid"] = false
                }
            };
            session["status"] = nativeFailed
                ? "failed_guest_process_exited"
                : guestExitedAfterCatalog
                    ? "guest_exited_after_catalog"
                : nativeRequestedWithoutOutput && offlineBaselineUsable
                    ? "completed_with_offline_typetree"
                : nativeRequestedWithoutOutput ? "completed_no_runtime_typetree"
                : recordCount == 0 ? "completed_no_runtime_events"
                : transferCallbackCount == 0 && observerInstallCount > 0 ? "completed_observer_only"
                : "completed";
            session["injectedModule"] = "guest-wrap";
            session["finishedUtc"] = DateTime.UtcNow.ToString("O");
        }
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));
        PrintHookSummary(hook);
    }

    private static void RunAdb(string adb, string serial, params string[] arguments)
    {
        var result = RunAdbAllowFailure(adb, serial, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"ADB 操作失败，退出码 {result.ExitCode}: {result.Stderr.Trim()}");
    }

    private static void WaitForAdbOnline(string adb, string serial, int timeoutSeconds)
    {
        Console.WriteLine($"等待 MuMu ADB 就绪，最长 {timeoutSeconds} 秒...");
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            RunProcessCapture(adb, new[] { "connect", serial });
            var state = RunAdbAllowFailure(adb, serial, "get-state");
            if (state.ExitCode == 0 &&
                string.Equals(state.Stdout.Trim(), "device", StringComparison.OrdinalIgnoreCase))
                return;

            Thread.Sleep(1000);
        }

        throw new TimeoutException($"MuMu ADB 在 {timeoutSeconds} 秒内未进入 online 状态: {serial}");
    }

    private static void WaitForMumuStopped(string mumuCli, int vmIndex, int timeoutSeconds)
    {
        Console.WriteLine($"等待 MuMu 实例完全停止，最长 {timeoutSeconds} 秒...");
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 5, 180));
        while (DateTime.UtcNow < deadline)
        {
            var result = RunProcessCapture(
                mumuCli,
                new[] { "info", "--vmindex", vmIndex.ToString() });
            if (result.ExitCode == 0)
            {
                try
                {
                    var info = JsonNode.Parse(result.Stdout)?.AsObject();
                    var processStarted = info?["is_process_started"]?.GetValue<bool>() ?? true;
                    var androidStarted = info?["is_android_started"]?.GetValue<bool>() ?? true;
                    if (!processStarted && !androidStarted)
                        return;
                }
                catch (JsonException)
                {
                    // The CLI may emit a partial response while the host exits.
                }
            }

            Thread.Sleep(1000);
        }

        throw new TimeoutException($"MuMu 实例在 {timeoutSeconds} 秒内未完全停止: VM {vmIndex}");
    }

    private static void ValidateGuestRipperAbi(string adb, string serial, string guestRipper)
    {
        var manifestPath = Path.Combine(Path.GetDirectoryName(guestRipper)!, "manifest.json");
        if (!File.Exists(manifestPath))
            return;

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException($"客体 Ripper manifest 不是 JSON 对象: {manifestPath}");
        var expectedAbi = StringValue(manifest, "abi");
        var expectedSha256 = StringValue(manifest, "sha256");
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actualSha256 = Sha256(guestRipper);
            if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"客体 Ripper SHA-256 不匹配: manifest={expectedSha256}, actual={actualSha256}。"
                    + "请重新构建库或更新同目录 manifest.json，禁止加载来源不明的二进制。");
            }
            Console.WriteLine($"客体 Ripper SHA-256 已校验: {actualSha256}");
        }

        if (string.IsNullOrWhiteSpace(expectedAbi))
            return;

        var result = RunAdbAllowFailure(adb, serial, "shell", "getprop", "ro.product.cpu.abi");
        var actualAbi = result.ExitCode == 0 ? result.Stdout.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(actualAbi))
            return;

        if (!string.Equals(expectedAbi, actualAbi, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"客体 Ripper ABI 不匹配: manifest={expectedAbi}, MuMu={actualAbi}。"
                + "请使用与 Native Bridge 宿主 ABI 匹配的库，或通过 --guest-ripper 指定正确文件。");
        }

        Console.WriteLine($"客体 Ripper ABI 已校验: {actualAbi}");
    }

    private static ProcessResult RunAdbAllowFailure(string adb, string serial, params string[] arguments)
    {
        var allArguments = new List<string> { "-s", serial };
        allArguments.AddRange(arguments);
        return RunProcessCapture(adb, allArguments);
    }

    private static string ResolveMumuAdbSerial(string mumuCli, string adb, int vmIndex)
    {
        // MUMU reports a running VM before its ADB endpoint is ready. Retry the
        // VM-scoped discovery instead of requiring callers to know a port.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var info = RunProcessCapture(mumuCli, new[] { "info", "--vmindex", vmIndex.ToString() });
            if (info.ExitCode == 0)
            {
                try
                {
                    var node = JsonNode.Parse(info.Stdout);
                    var objectNode = node as JsonObject;
                    var host = StringValue(objectNode ?? [], "adb_host_ip");
                    var port = StringValue(objectNode ?? [], "adb_port");
                    if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(port) &&
                        int.TryParse(port, out var parsedPort) && parsedPort is > 0 and <= 65535)
                    {
                        var endpoint = $"{host}:{parsedPort}";
                        // MuMu can report its endpoint before the ADB server has
                        // published it. Do not return an offline endpoint when
                        // an emulator serial is already usable.
                        if (IsAdbOnline(adb, endpoint))
                            return endpoint;
                    }
                }
                catch (JsonException)
                {
                    // Fall through to the connected-device check below.
                }
            }

            var devices = RunProcessCapture(adb, new[] { "devices" });
            var serials = devices.ExitCode == 0
                ? devices.Stdout
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .Select(line => line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries))
                    .Where(parts => parts.Length >= 2 && parts[1].Equals("device", StringComparison.OrdinalIgnoreCase))
                    .Select(parts => parts[0])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
            if (serials.Length == 1)
                return serials[0];
            if (serials.Length > 1)
            {
                throw new InvalidOperationException(
                    $"无法从 MUMU info 映射 VM {vmIndex} 的 ADB 设备；检测到多个设备，请指定 --adb-serial 或 MY_HOOK_ADB_SERIAL: {string.Join(", ", serials)}");
            }

            Thread.Sleep(1000);
        }

        throw new InvalidOperationException(
            $"等待 VM {vmIndex} 的 ADB 设备超时，请确认 MUMU 已完全启动，或指定 --adb-serial / MY_HOOK_ADB_SERIAL");
    }

    private static bool IsAdbOnline(string adb, string serial)
    {
        // IP endpoints need an explicit connect before get-state can observe
        // them. For emulator-* serials this is harmless and get-state remains
        // the authoritative check.
        RunProcessCapture(adb, new[] { "connect", serial });
        var state = RunAdbAllowFailure(adb, serial, "get-state");
        return state.ExitCode == 0 &&
            string.Equals(state.Stdout.Trim(), "device", StringComparison.OrdinalIgnoreCase);
    }

    private static string? RemoteFileSignature(string adb, string serial, string remotePath, string localProbePath)
    {
        TryDeleteFile(localProbePath);
        var result = RunAdbAllowFailure(adb, serial, "pull", remotePath, localProbePath);
        if (result.ExitCode != 0)
            return null;
        if (!File.Exists(localProbePath))
            return null;
        return Sha256(localProbePath);
    }

    private static long? RemoteFileSize(string adb, string serial, string remotePath)
    {
        var result = RunAdbAllowFailure(adb, serial, "shell", "stat", "-c", "%s", remotePath);
        if (result.ExitCode != 0 || !long.TryParse(result.Stdout.Trim(), out var size))
            return null;
        return size;
    }

    private static bool TryPullGuestFile(
        string adb,
        string serial,
        string remotePath,
        string localPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        TryDeleteFile(localPath);
        var result = RunAdbAllowFailure(adb, serial, "pull", remotePath, localPath);
        return result.ExitCode == 0 && File.Exists(localPath);
    }

    private static bool TryDeleteFile(string path)
    {
        if (!File.Exists(path))
            return true;

        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                File.Delete(path);
                return !File.Exists(path);
            }
            catch (IOException) when (attempt < 11)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException) when (attempt < 11)
            {
                Thread.Sleep(250);
            }
        }

        return false;
    }

    private static void ValidateAndroidPackage(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName) ||
            packageName.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new ArgumentException("Android 包名格式无效");
    }

    private static bool StartAndroidPackageLauncher(
        string adb,
        string serial,
        string packageName)
    {
        var resolve = RunAdbAllowFailure(
            adb,
            serial,
            "shell",
            "cmd",
            "package",
            "resolve-activity",
            "--brief",
            packageName);
        var component = resolve.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Contains('/', StringComparison.Ordinal)
                && line.StartsWith(packageName + "/", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(component))
        {
            Console.WriteLine($"Android Activity 解析失败: {(resolve.Stderr + resolve.Stdout).Trim()}");
            return false;
        }

        var result = RunAdbAllowFailure(
            adb,
            serial,
            "shell",
            "am",
            "start",
            "-n",
            component);
        if (result.ExitCode == 0)
        {
            Console.WriteLine($"已通过 Android Activity 解析并启动程序: {component}");
            return true;
        }
        else
            Console.WriteLine($"Android Activity fallback 启动失败: {(result.Stderr + result.Stdout).Trim()}");
        return false;
    }

    private static void LaunchMumuPackage(
        string mumuCli,
        int vmIndex,
        string packageName,
        string? timeoutValue)
    {
        if (string.IsNullOrWhiteSpace(packageName) ||
            packageName.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new ArgumentException("--launch-package 必须是合法的 Android 包名");

        var timeoutSeconds = 30;
        if (timeoutValue is not null &&
            (!int.TryParse(timeoutValue, out timeoutSeconds) || timeoutSeconds <= 0))
            throw new ArgumentException("--launch-timeout-seconds 必须是正整数");

        var command = $"shell monkey -p {packageName} -c android.intent.category.LAUNCHER 1";
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        Exception? lastError = null;
        Console.WriteLine($"等待 Android 客体上线并启动程序: {packageName}");
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                RunProcess(mumuCli, new[]
                {
                    "adb", "--vmindex", vmIndex.ToString(), "--cmd", command
                });
                Console.WriteLine($"已请求启动程序: {packageName}");
                return;
            }
            catch (InvalidOperationException ex)
            {
                lastError = ex;
                Thread.Sleep(1000);
            }
        }

        throw new TimeoutException(
            $"Android 客体在 {timeoutSeconds} 秒内未能启动程序 {packageName}: {lastError?.Message ?? "未知错误"}");
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
        string? captureSessionId,
        int targetPid,
        string targetPath,
        string module,
        string eventsPath,
        string status,
        int? targetControlIdent = null,
        string? captureTemplate = null)
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
        if (!string.IsNullOrWhiteSpace(captureSessionId))
        {
            hook["session"]!["captureSessionId"] = captureSessionId;
            hook["protocol"]!["environment"]!["MY_HOOK_CAPTURE_SESSION_ID"] = captureSessionId;
        }
        if (targetControlIdent is > 0)
            hook["session"]!["targetControlIdent"] = targetControlIdent.Value;
        if (captureTemplate is not null)
        {
            hook["session"]!["captureTemplate"] = RelativeTo(hookDir, captureTemplate);
            hook["session"]!["captureFile"] = RelativeTo(hookDir, captureTemplate);
            hook["session"]!["captureStatus"] = "awaiting_manual_capture";
        }
        File.WriteAllText(hookPath, hook.ToJsonString(JsonOptions));
    }

    private static int LaunchSuspendedAndInject(
        string executable,
        string arguments,
        string workingDirectory,
        string module,
        string profileId,
        string eventsPath,
        string hookPath,
        string? captureSessionId = null)
    {
        var previous = new Dictionary<string, string?>
        {
            ["MY_HOOK_PROFILE"] = Environment.GetEnvironmentVariable("MY_HOOK_PROFILE"),
            ["MY_HOOK_OUTPUT"] = Environment.GetEnvironmentVariable("MY_HOOK_OUTPUT"),
            ["MY_HOOK_EVENTS"] = Environment.GetEnvironmentVariable("MY_HOOK_EVENTS"),
            ["MY_HOOK_FILE"] = Environment.GetEnvironmentVariable("MY_HOOK_FILE"),
            ["MY_HOOK_CAPTURE_SESSION_ID"] = Environment.GetEnvironmentVariable("MY_HOOK_CAPTURE_SESSION_ID")
        };
        Environment.SetEnvironmentVariable("MY_HOOK_PROFILE", profileId);
        Environment.SetEnvironmentVariable("MY_HOOK_OUTPUT", Path.GetDirectoryName(eventsPath));
        Environment.SetEnvironmentVariable("MY_HOOK_EVENTS", eventsPath);
        Environment.SetEnvironmentVariable("MY_HOOK_FILE", hookPath);
        Environment.SetEnvironmentVariable("MY_HOOK_CAPTURE_SESSION_ID", captureSessionId);

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

    internal static void InjectIntoProcess(int processId, string module)
    {
        var process = OpenProcess(ProcessAccess, false, processId);
        if (process == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess({processId})");
        try { InjectIntoHandle(process, module); }
        finally { CloseHandle(process); }
    }

    private static void SetRemoteHookEnvironmentAndInject(
        int processId,
        string module,
        IReadOnlyDictionary<string, string> environment)
    {
        var process = OpenProcess(ProcessAccess, false, processId);
        if (process == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess({processId})");

        try
        {
            foreach (var pair in environment)
                SetRemoteEnvironmentVariable(processId, process, pair.Key, pair.Value);
            InjectIntoHandle(process, module);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static void SetRemoteEnvironmentVariable(
        int processId,
        IntPtr process,
        string name,
        string value)
    {
        if (Environment.Is64BitProcess != IsProcess64Bit(processId))
            throw new PlatformNotSupportedException(
                "复用 MUMU 宿主写入会话环境变量要求控制器与目标进程使用相同的 64 位架构");

        var localKernel32 = GetModuleHandle("kernel32.dll");
        var localSetter = GetProcAddress(localKernel32, "SetEnvironmentVariableW");
        var remoteKernel32 = GetRemoteModuleBase(processId, "kernel32.dll");
        if (localKernel32 == IntPtr.Zero || localSetter == IntPtr.Zero || remoteKernel32 == IntPtr.Zero)
            throw new InvalidOperationException("无法解析目标进程的 kernel32.dll 或 SetEnvironmentVariableW");

        var setterRva = localSetter.ToInt64() - localKernel32.ToInt64();
        var remoteSetter = new IntPtr(remoteKernel32.ToInt64() + setterRva);
        var nameBytes = Encoding.Unicode.GetBytes(name + "\0");
        var valueBytes = Encoding.Unicode.GetBytes(value + "\0");
        const int dataOffset = 128;
        var valueOffset = dataOffset + nameBytes.Length;
        var code = BuildRemoteSetEnvironmentStub(
            IntPtr.Zero, IntPtr.Zero, remoteSetter);
        var totalBytes = valueOffset + valueBytes.Length;
        var remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)totalBytes,
            AllocationType, PageExecuteReadWrite);
        if (remote == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx(environment stub)");

        try
        {
            var remoteName = IntPtr.Add(remote, dataOffset);
            var remoteValue = IntPtr.Add(remote, valueOffset);
            code = BuildRemoteSetEnvironmentStub(remoteName, remoteValue, remoteSetter);
            WriteRemoteBytes(process, remote, code);
            WriteRemoteBytes(process, remoteName, nameBytes);
            WriteRemoteBytes(process, remoteValue, valueBytes);
            var thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, remote,
                IntPtr.Zero, 0, out _);
            if (thread == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"CreateRemoteThread(SetEnvironmentVariableW: {name})");
            try
            {
                if (WaitForSingleObject(thread, 10000) != WaitObject0)
                    throw new TimeoutException($"等待远程设置环境变量超时: {name}");
                if (!GetExitCodeThread(thread, out var result) || result == 0)
                    throw new InvalidOperationException($"目标进程拒绝设置环境变量: {name}");
            }
            finally
            {
                CloseHandle(thread);
            }
        }
        finally
        {
            VirtualFreeEx(process, remote, UIntPtr.Zero, Release);
        }
    }

    private static byte[] BuildRemoteSetEnvironmentStub(
        IntPtr name,
        IntPtr value,
        IntPtr setter)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0x48, 0xB9 });
        writer.Write(name.ToInt64());
        writer.Write(new byte[] { 0x48, 0xBA });
        writer.Write(value.ToInt64());
        writer.Write(new byte[] { 0x48, 0xB8 });
        writer.Write(setter.ToInt64());
        writer.Write(new byte[] { 0x48, 0x83, 0xEC, 0x28, 0xFF, 0xD0,
            0x48, 0x83, 0xC4, 0x28, 0xC3 });
        return stream.ToArray();
    }

    private static void WriteRemoteBytes(IntPtr process, IntPtr address, byte[] bytes)
    {
        if (!WriteProcessMemory(process, address, bytes, (UIntPtr)bytes.Length, out var written)
            || written.ToUInt64() != (ulong)bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteProcessMemory(remote stub)");
    }

    private static IntPtr GetRemoteModuleBase(int processId, string moduleName)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var module = process.Modules.Cast<ProcessModule>()
                .FirstOrDefault(candidate => string.Equals(candidate.ModuleName, moduleName,
                    StringComparison.OrdinalIgnoreCase));
            return module?.BaseAddress ?? IntPtr.Zero;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"无法读取目标进程模块列表: PID {processId}", ex);
        }
    }

    private static bool IsProcess64Bit(int processId)
    {
        using var process = Process.GetProcessById(processId);
        if (!Environment.Is64BitOperatingSystem)
            return false;
        if (!IsWow64Process(process.Handle, out var wow64))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "IsWow64Process");
        return !wow64;
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
        if (capture["drawReplayEvidence"] is JsonValue evidenceName &&
            !string.IsNullOrWhiteSpace(evidenceName.GetValue<string>()))
        {
            var evidencePath = Path.Combine(output, "renderdoc", evidenceName.GetValue<string>());
            if (File.Exists(evidencePath))
                renderDoc["drawReplayEvidence"] = "renderdoc/" + evidenceName.GetValue<string>();
        }

        var asObject = new JsonObject
        {
            ["shaderReport"] = asInfo.ShaderReportRelative,
            ["materialBindings"] = asInfo.BindingsRelative,
            ["assetIndex"] = asInfo.AssetIndexRelative,
            ["schema"] = asInfo.Schema,
            ["shaderCount"] = asInfo.ShaderCount,
            ["materialCount"] = asInfo.MaterialCount,
            ["materialShaderBindingCount"] = asInfo.MaterialShaderBindingCount,
            ["materialTextureBindingCount"] = asInfo.MaterialTextureBindingCount,
            ["assetCount"] = asInfo.AssetCount,
            ["assetIndexSchema"] = asInfo.AssetIndexSchema,
            ["assetIndexValidation"] = asInfo.AssetIndexValidation,
            ["shaderNames"] = new JsonArray(asInfo.ShaderNames.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray())
        };

        return new JsonObject
        {
            ["format"] = "my-hook.capture.v1",
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
        var assetIndex = CopyOptional(options, "as-index", output, "as/asset-index.json");
        var schema = "none";
        var shaderCount = 0;
        var materialCount = 0;
        var materialShaderBindingCount = 0;
        var materialTextureBindingCount = 0;
        var assetCount = 0;
        string? assetIndexSchema = null;
        string? assetIndexValidation = null;
        var shaderNames = new List<string>();

        if (assetIndex is not null)
        {
            var indexRoot = JsonNode.Parse(File.ReadAllText(assetIndex))?.AsObject()
                ?? throw new InvalidDataException("as-index 不是 JSON 对象");
            assetIndexSchema = StringValue(indexRoot, "schema");
            if (!string.Equals(assetIndexSchema, "anime-studio.asset-index.v1", StringComparison.Ordinal))
                throw new InvalidDataException($"as-index schema 不兼容: {assetIndexSchema ?? "<missing>"}");
            var validation = indexRoot["validation"] as JsonObject;
            assetIndexValidation = validation is null ? null : StringValue(validation, "status");
            if (!string.Equals(assetIndexValidation, "valid", StringComparison.Ordinal))
                throw new InvalidDataException("as-index 未通过 validation=valid，拒绝写入 .hook");
            assetCount = ArrayCount(indexRoot, "assets");
            materialShaderBindingCount = ArrayCount(indexRoot, "materialShaderBindings");
            materialTextureBindingCount = ArrayCount(indexRoot, "materialTextureBindings");
        }

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
            shaderNames.Distinct(StringComparer.Ordinal).OrderBy(x => x).ToList(),
            assetIndex is null ? null : RelativeTo(output, assetIndex),
            assetIndexSchema,
            assetCount,
            materialShaderBindingCount,
            materialTextureBindingCount,
            assetIndexValidation);
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
        var result = RunProcessCapture(executable, arguments);
        Console.Write(result.Stdout);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"外部进程 {Path.GetFileName(executable)} 执行失败，退出码 {result.ExitCode}: {result.Stderr.Trim()}");
        if (!string.IsNullOrWhiteSpace(result.Stderr)) Console.Error.WriteLine(result.Stderr.Trim());
    }

    private static ProcessResult RunProcessCapture(
        string executable,
        IReadOnlyList<string> arguments)
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
        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    private static JsonObject LoadProfile(string? path)
    {
        var resolvedPath = path ?? FindDefaultProfilePath();
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return new JsonObject { ["id"] = "runtime", ["captureExtensions"] = new JsonArray(".rdc", ".srrdc", ".zzzrdc") };
        resolvedPath = FullPath(resolvedPath);
        if (!File.Exists(resolvedPath))
            throw new FileNotFoundException("找不到 Profile", resolvedPath);
        return JsonNode.Parse(File.ReadAllText(resolvedPath))?.AsObject()
            ?? throw new InvalidDataException("profile 不是 JSON 对象");
    }

    private static string? ResolveProfilePath(JsonObject? node, string key)
    {
        var value = StringValue(node ?? [], key);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (Path.IsPathRooted(value))
            return File.Exists(value) ? FullPath(value) : null;

        var candidates = new List<string> { Path.Combine(Directory.GetCurrentDirectory(), value) };
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, value));
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static string? FindDefaultProfilePath()
    {
        var configured = Environment.GetEnvironmentVariable("MY_HOOK_PROFILE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = FullPath(configured);
            if (!File.Exists(configuredPath))
                throw new FileNotFoundException("MY_HOOK_PROFILE_PATH 指向的 Profile 不存在", configuredPath);
            return configuredPath;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuredId = Environment.GetEnvironmentVariable("MY_HOOK_PROFILE");
        if (!string.IsNullOrWhiteSpace(configuredId) &&
            !System.Text.RegularExpressions.Regex.IsMatch(configuredId, "^[A-Za-z0-9._-]+$"))
            throw new InvalidOperationException("MY_HOOK_PROFILE 只能包含字母、数字、点、下划线和短横线");

        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var profilesDirectory = Path.Combine(directory.FullName, "profiles");
            if (!string.IsNullOrWhiteSpace(configuredId))
            {
                var configuredProfile = Path.Combine(profilesDirectory, configuredId + ".json");
                if (File.Exists(configuredProfile))
                    return configuredProfile;
            }

            var defaultProfile = Path.Combine(profilesDirectory, "default.json");
            if (File.Exists(defaultProfile))
                return defaultProfile;

            var profiles = Directory.Exists(profilesDirectory)
                ? Directory.EnumerateFiles(profilesDirectory, "*.json")
                    .Where(file => !string.Equals(Path.GetFileName(file), "default.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
            if (profiles.Length == 1)
                return profiles[0];
            if (profiles.Length > 1)
                throw new InvalidOperationException(
                    $"发现多个 Profile，不能猜测目标版本，请显式指定 --profile 或 MY_HOOK_PROFILE_PATH: {string.Join(", ", profiles.Select(Path.GetFileName))}");
        }

        return null;
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

    private static string RequiredFile(string value, string key)
    {
        var path = FullPath(value);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到 --{key}", path);
        return path;
    }

    private static string RequiredFile(Dictionary<string, List<string>> options, string key) =>
        RequiredFile(RequiredValue(options, key), key);

    private static string OneValue(List<string> values, string key) => values.Count == 0 ? throw new ArgumentException($"--{key} 缺少值") : values[^1];
    private static string? GetValueOrDefault(this Dictionary<string, List<string>> options, string key) => options.TryGetValue(key, out var values) ? OneValue(values, key) : null;
    private static List<string> Positionals(this Dictionary<string, List<string>> options) => options.TryGetValue("__positionals", out var values) ? values : [];
    private static string FullPath(string value) => Path.GetFullPath(value);
    private static string RelativeTo(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static bool TryParseInteger(string value, out int result)
    {
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text[2..], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out result);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
    private static bool TryParseUInt32(string value, out uint result)
    {
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out result);
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
    private static string? StringValue(JsonObject node, string key)
    {
        if (node[key] is not JsonValue value)
            return null;
        if (value.TryGetValue<string>(out var text))
            return text;
        return value.ToJsonString();
    }
    private static string CsvValue(JsonObject node, string key)
    {
        if (node[key] is JsonArray array)
            return string.Join(",", array.Select(value => value?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        return StringValue(node, key) ?? string.Empty;
    }
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
        if (hook["typetree"] is JsonObject typeTree)
            Console.WriteLine($"TypeTree: {typeTree["format"] ?? "?"}, {typeTree["size"] ?? "?"} bytes");
        Console.WriteLine($"AS Shader: {hook["as"]?["shaderCount"] ?? 0}, Material: {hook["as"]?["materialCount"] ?? 0}");
        if (hook["as"]?["assetIndex"] is not null)
            Console.WriteLine($"AS index: {hook["as"]?["assetCount"] ?? 0} assets, Material→Shader {hook["as"]?["materialShaderBindingCount"] ?? 0}, Material→Texture {hook["as"]?["materialTextureBindingCount"] ?? 0}, paths {hook["as"]?["assetIndexValidation"] ?? "?"}");
        var runtimeEvidence = hook["runtimeEvidence"]?.AsObject();
        var runtimeCallChain = runtimeEvidence?["runtimeCallChain"]?.AsObject();
        var typedObjectCorrelation = runtimeEvidence?["typedObjectCorrelation"]?.AsObject();
        var resolverTypeObservation = runtimeEvidence?["resolverTypeObservation"]?.AsObject();
        var invocationTokenEvidence = runtimeEvidence?["invocationTokenEvidence"]?.AsObject();
        Console.WriteLine($"运行时记录: {hook["records"]?.AsArray().Count ?? 0}, PPtr→查找链: {Int64Value(runtimeCallChain, "matchedCount")}, 解析器类型观测: {Int64Value(resolverTypeObservation, "typedReturnCount")}, 精确 Material→Shader: {Int64Value(typedObjectCorrelation, "exactMaterialShaderFieldBindingCount")}, 未解决: {hook["unresolved"]?.AsArray().Count ?? 0}");
        Console.WriteLine($"Invocation token: Transfer {Int64Value(invocationTokenEvidence, "transferRecordCount")}, 字段 {Int64Value(invocationTokenEvidence, "fieldRecordCount")}, 解析器 {Int64Value(invocationTokenEvidence, "resolverRecordCount")}");
        Console.WriteLine("跨源身份绑定: 未观测（未确认 Material→Shader、sourceFile#PathID 或 RenderDoc EID）");
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
        Console.WriteLine("  --as-index <json>   附带已校验的 AS 资产路径和 Material→Shader/Texture 索引");
        Console.WriteLine("  --link-map <json>   仅接受带 asShaderIdentityKey 的显式 Shader 对齐");
        Console.WriteLine("my-hook-tool inspect <file.hook>");
        Console.WriteLine("my-hook-tool analyze <file.hook> [--typetree <file.ttbin>] [--output <analysis.json>]");
        Console.WriteLine("my-hook-tool finalize <file.hook> [--typetree <file.ttbin>] [--analyze] [--analysis-output <analysis.json>]");
        Console.WriteLine("  输出确定性类型、地址、快照和已观测指针引用统计；不猜测资源路径或字段语义");
        Console.WriteLine("my-hook-tool typetree <file.ttbin> [--catalog <rtti-catalog.txt>] [--required-type <name[,name...]>] [--output <report.json>] [--full]");
        Console.WriteLine("  校验 TypeTree 字段节点、父子层级和 RTTI 覆盖率；--full 额外输出逐节点 fieldPath");
        Console.WriteLine("  --expected-revision X --expected-variant X  校验文件头版本");
        Console.WriteLine("  --strict            空树、部分覆盖或版本不匹配时返回失败");
        Console.WriteLine("my-hook-tool typetree-static <libunity.so> [--output <report.json>] [--window-bytes N] [--max-references N] [--identity-target 0xRVA]");
        Console.WriteLine("  只读扫描 ELF/ARM64 字符串引用和有限 BL 调用窗口；默认使用 TypeTree 锚点，结果仅为 candidate evidence");
        Console.WriteLine("  --anchor <exact text> 可重复；提供后替换默认锚点，不调用未知 ABI");
        Console.WriteLine("  --identity-target 只报告该目标的 BL 调用及返回值直接回写证据，不解释 key64 语义");
        Console.WriteLine("my-hook-tool typetree merge <output.ttbin> <input.ttbin>... [--force] [--prefer-more-nodes]");
        Console.WriteLine("  默认冲突即失败；显式 prefer-more-nodes 时选择节点更多的输入，并写入 manifest");
        Console.WriteLine("my-hook-tool typetree import-struct <input.structs.dump> <output.ttbin> --catalog <rtti-catalog.txt> [--revision X] [--variant X] [--force]");
        Console.WriteLine("  将已保存的 legacy StructDump 字段逐项导入统一 TypeTree；缺失 refTypeHash 会明确记录在 manifest");
        Console.WriteLine("my-hook-tool as-index <AS manifest|directory> --output <asset-index.json> [--shader-report <json>] [--as-bindings <json>] [--force]");
        Console.WriteLine("  按 SourceCAB+PathID 建立 AS 资产索引，保留依赖路径和 Material→Shader/Texture 精确绑定");
        Console.WriteLine("my-hook-tool enrich <file.hook> --renderdoc <renderdoccmd.exe> --event <eid> [options]");
        Console.WriteLine("  只为显式 EID 增量导出并挂接 Shader/Variant/Reflection；省略 EID 会直接失败");
        Console.WriteLine("  --capture <capture.srrdc> 覆盖 .hook 中记录的捕获路径");
        Console.WriteLine("  --output <dir>          覆盖增量导出目录");
        Console.WriteLine("  --reconstruct --spirv-cross <exe>  请求 HLSL 重建");
        Console.WriteLine("  --export-resources      导出所选 EID 的纹理和 bounded buffer 快照");
        Console.WriteLine("my-hook-tool align --capture <capture.json> --runtime-hook <file.hook> --output <alignment.json> [--hook <file.hook>] [--as-report <json>] [--as-bindings <json>] [--as-index <asset-index.json>] [--link-map <json>]");
        Console.WriteLine("  --hook 含 as/ 文件时自动读取 AS 报告、绑定和索引；AS index 只提供序列化身份和精确绑定，不推断跨来源关系");
        Console.WriteLine("my-hook-tool verify --as-index <asset-index.json> --typetree-report <report.json> --output <verification.json> [options]");
        Console.WriteLine("  统一验收 AS PathID/路径、Material→Shader/Texture、TypeTree 字段和运行时身份桥；失败或阻塞时返回 1/2");
        Console.WriteLine("  --analysis <analysis.json>  提供运行时 identityBridge；缺失时明确标记 blocked");
        Console.WriteLine("  --profile <profile.json>   检查目标版本与客体观测版本差异");
        Console.WriteLine("  --critical-type <name>     覆盖关键 TypeTree 类型，可重复或逗号分隔");
        Console.WriteLine("  --require-full-catalog     要求 TypeTree report 的 catalog 全部覆盖");
        Console.WriteLine("  --hook <file.hook>         校验 .hook 是否包含可用 AS index");
        Console.WriteLine("my-hook-tool capture-session --renderdoc <renderdoccmd.exe> --target <exe> --module <bridge.dll> --output <dir> [options]");
        Console.WriteLine("  统一创建 captureSessionId，启动 RenderDoc 目标并查询真实 PID 后加载 Hook 模块");
        Console.WriteLine("my-hook-tool trigger --renderdoc <renderdoccmd.exe> [--ident <port> | --target <name> | --target-pid <pid>] [options]");
        Console.WriteLine("  通过 TargetControl 查询目标，或向实际绘制目标请求捕获；--capture-file 会等待并复制完成文件");
        Console.WriteLine("  --capture-file <file> 捕获输出路径；--wait-seconds N 等待 NewCapture 的最长时间");
        Console.WriteLine("  --hook <file.hook>   捕获完成后回写会话的 captureStatus/captureFile");
        Console.WriteLine("my-hook-tool attach --target <exe> --module <bridge.dll> --output <dir> [options]");
        Console.WriteLine("  --target-pid <pid>  向已运行进程注入，而不是启动新进程");
        Console.WriteLine("  --arguments <text>  目标进程参数");
        Console.WriteLine("  --name <name>       会话目录和 .hook 文件名");
        Console.WriteLine("  --watch              注入后持续采集，目标退出或 Ctrl+C 时自动 finalize");
        Console.WriteLine("  --duration-seconds N 注入后采集 N 秒并自动 finalize");
        Console.WriteLine("my-hook-tool mumu --mumu-root <dir> --output <dir> [--module <bridge.dll>] [options]");
        Console.WriteLine("  --vmindex <index>   MUMU 实例编号，默认 0");
        Console.WriteLine("  --reuse-mumu        复用已运行的 MUMU VM；客体 Ripper 模式会重启目标 Android 游戏进程以应用 LD_PRELOAD");
        Console.WriteLine("  --mumu-android-version <auto|12|15>  覆盖启动的 Android 引擎版本，默认从 Profile 读取");
        Console.WriteLine("  --renderdoc <exe>   用 RenderDoc 启动 MUMU 宿主并写入同一会话的 capture.srrdc");
        Console.WriteLine("  --capture-file <file> 覆盖 RenderDoc 输出路径，默认会话目录下的 capture.srrdc");
        Console.WriteLine("  --trigger-tool <exe> TargetControl 触发器；默认从 runtime/<profile>/ 查找");
        Console.WriteLine("  --auto-trigger       客体启动/TypeTree 完成后自动请求一次捕获，默认关闭");
        Console.WriteLine("  --trigger-frames N   自动捕获帧数，默认 1");
        Console.WriteLine("  --trigger-delay-seconds N 自动捕获前等待秒数，默认 0");
        Console.WriteLine("  --capture-timeout-seconds N 等待捕获文件稳定落盘的秒数，默认 60");
        Console.WriteLine("  --opt-hook-children   让 RenderDoc 传播到 MUMU 创建的子进程");
        Console.WriteLine("  --target-arg <arg>  传给 MUMU 宿主的参数，可重复");
        Console.WriteLine("  --opt-api-validation / --opt-ref-all-resources / --opt-capture-all-cmd-lists");
        Console.WriteLine("  --launch-package <id> 启动已安装的 Android 程序");
        Console.WriteLine("  --launch-timeout-seconds N 等待客体上线的最长时间");
        Console.WriteLine("  --guest-ripper <lib.so> 开发覆盖内置 TypeTreeRipper；默认从 Profile 自动解析");
        Console.WriteLine("  --no-guest-ripper 仅运行 Windows/RenderDoc 层，不加载客体 TypeTreeRipper");
        Console.WriteLine("  --adb-serial <serial> ADB 设备地址；默认从 mumu-cli info 按 VM 自动解析，也可用 MY_HOOK_ADB_SERIAL");
        Console.WriteLine("  --guest-timeout-seconds N 等待客体 TypeTree 目录的最长时间，默认 90");
        Console.WriteLine("  --guest-event-seconds N TypeTree 完成后等待运行时事件，默认 30");
        Console.WriteLine("  --guest-event-max-seconds N 场景未加载时的登录/启动等待上限，默认 120");
        Console.WriteLine("  --guest-event-idle-seconds N 场景类型出现后事件稳定多久才收尾，默认 15；0 表示等到上限");
        Console.WriteLine("  --guest-dump-delay-seconds N 延迟客体对象观察器，避免登录页争用，默认 5");
        Console.WriteLine("  --observe-transfers 1 开启实验性运行时观察器，默认关闭");
        Console.WriteLine("  --guest-class-ids <ids> 限定客体扫描的 ClassID，逗号分隔");
        Console.WriteLine("  --observe-vtable-offset <bytes> 覆盖观察器 vtable 字节偏移");
        Console.WriteLine("  --observe-vtable-offsets <a,b> 覆盖并同时观察多个 vtable 字节偏移");
        Console.WriteLine("  --vtable-probe 1 只记录已创建对象的 vtable 证据，不调用 native TypeTree，默认关闭");
        Console.WriteLine("  --max-observer-targets <n> 限制 inline Hook 目标数，0 表示不限制");
        Console.WriteLine("  --enable-native-transfer 1 启用显式高风险 TypeTree 生成，默认关闭");
        Console.WriteLine("  --unsafe-experiment I_UNDERSTAND_UNITY_CRASH_RISK native 模式必需确认串");
        Console.WriteLine("  --native-layout 1 使用 HSR native Transfer ABI");
        Console.WriteLine("  --vtable-offset <bytes> native Transfer 槽位；必须由目标 Profile 的 ABI 证据确认");
        Console.WriteLine("  --native-on-transfer 1 在真实 Transfer 回调线程执行一次 native TypeTree 试验");
        Console.WriteLine("  --transfer-vtable-rva 当前 HSR ABI 不使用，必须留空");
        Console.WriteLine("  --capture-transfer-words 1 记录真实 Transfer 回调前后的有限原始字段");
        Console.WriteLine("  --context-observer 1 观察 Transfer 已调用的序列化上下文 slot 0");
        Console.WriteLine("  --context-observer-all 1 将上下文观察扩展到所有已观测类型，默认仅 Material");
        Console.WriteLine("  --typetree-compare-observer 1 只观察 Profile 指定的 TypeTree 比较候选函数，默认关闭");
        Console.WriteLine("  --typetree-compare-rva <rva> 指定客体 libunity.so 的 TypeTree 比较候选 RVA，不接受猜测地址");
        Console.WriteLine("  --typetree-constructor-observer 1 观察已验证首指令的 TypeTree 构造调用，默认关闭");
        Console.WriteLine("  --typetree-constructor-rva <rva> 指定客体 libunity.so 的 TypeTree 构造候选 RVA");
        Console.WriteLine("  --typetree-constructor-first-instruction <word> 目标构造函数首条 ARM64 指令字");
        Console.WriteLine("  --typetree-constructor-target-architecture <arch> 目标架构，例如 arm64-guest");
        Console.WriteLine("  --serialized-identity-observer 1 观察已验证形状的序列化身份双向映射，默认关闭");
        Console.WriteLine("  --serialized-identity-rva <rva> 指定映射函数 RVA；必须配合首指令校验值");
        Console.WriteLine("  --serialized-identity-first-instruction <word> 目标函数首条 ARM64 指令字");
        Console.WriteLine("  --serialized-identity-guest-patch 1 显式启用 ARM64 guest 原始 trampoline 实验");
        Console.WriteLine("  --serialized-identity-caller-patch 1 显式启用 caller post-store 观测实验；需风险确认串");
        Console.WriteLine("  --serialized-pptr-observer 1 观察已验证形状的 SerializedFile PPtr 候选调用，默认关闭");
        Console.WriteLine("  --hsr45-opaque-callsite-probe-mask <1-15> 逐点选择 HSR 4.5 调用链观测；位 1/2/4/8 分别为 producer 输入/返回、consumer 参数、resolver 参数");
        Console.WriteLine("  --serialized-pptr-rva <rva> 指定 PPtr 候选函数 RVA；必须配合首指令校验值");
        Console.WriteLine("  --serialized-pptr-first-instruction <word> PPtr 候选函数首条 ARM64 指令字");
        Console.WriteLine("  --serialized-pptr-target-architecture <arch> 目标架构，例如 arm64-guest");
        Console.WriteLine("  --serialized-pptr-guest-patch 1 显式启用 ARM64 guest 原始 trampoline 实验");
        Console.WriteLine("  --guest-snapshot-bytes <n> 对指定客体类型采样对象内存，默认关闭，最大 4096");
        Console.WriteLine("  --guest-snapshot-class-ids <ids> 快照 ClassID，逗号分隔");
        Console.WriteLine("  --watch              注入后持续采集，目标退出或 Ctrl+C 时自动 finalize");
        Console.WriteLine("  --duration-seconds N 注入后采集 N 秒并自动 finalize");
        Console.WriteLine("my-hook-tool finalize <session.hook>");
        Console.WriteLine("  --typetree <file>   将 TypeTreeRipper 的 .ttbin/.tpk/结构文件挂入 .hook");
    }

    private const uint CreateSuspended = 0x00000004;
    private const uint ProcessAccess = 0x0000142A;
    private const uint AllocationType = 0x00003000;
    private const uint Release = 0x00008000;
    private const uint PageReadWrite = 0x04;
    private const uint PageExecuteReadWrite = 0x40;
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
    private static extern bool IsWow64Process(IntPtr process, out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
    private sealed record AsReports(
        string? ShaderReportRelative,
        string? BindingsRelative,
        string Schema,
        int ShaderCount,
        int MaterialCount,
        List<string> ShaderNames,
        string? AssetIndexRelative,
        string? AssetIndexSchema,
        int AssetCount,
        int MaterialShaderBindingCount,
        int MaterialTextureBindingCount,
        string? AssetIndexValidation);
}
