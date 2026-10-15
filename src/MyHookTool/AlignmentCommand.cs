using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace MyHookTool;

internal static class AlignmentCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static int Run(string[] args)
    {
        var options = ParseOptions(args);
        var capturePath = RequiredFile(options, "capture");
        var outputPath = Path.GetFullPath(RequiredValue(options, "output"));
        var hookPath = OptionalFile(options, "hook");
        var exportHook = hookPath is null ? null : ReadObject(hookPath, "export Hook");
        var asReportPath = OptionalFile(options, "as-report")
            ?? ResolveEmbeddedFile(exportHook, hookPath, "shaderReport", "AS Shader report");
        var asBindingsPath = OptionalFile(options, "as-bindings")
            ?? ResolveEmbeddedFile(exportHook, hookPath, "materialBindings", "AS material bindings");
        var runtimeHookPath = RequiredFile(options, "runtime-hook");
        var linkMapPath = OptionalFile(options, "link-map");
        var asIndexPath = OptionalFile(options, "as-index")
            ?? ResolveEmbeddedFile(exportHook, hookPath, "assetIndex", "AS asset index");

        var capture = ReadObject(capturePath, "RDC capture manifest");
        var asReport = ReadObject(asReportPath, "AS shader report");
        var asBindings = ReadObject(asBindingsPath, "AS material bindings");
        var asIndex = asIndexPath is null ? null : ReadAsIndex(asIndexPath);
        var runtimeHook = ReadObject(runtimeHookPath, "runtime Hook");

        var rdc = ReadRdc(capture);
        var asData = ReadAs(asReport, asBindings);
        var runtime = ReadRuntime(runtimeHook);
        var asShaderIdentityKeys = new HashSet<string>(asData.ShaderIdentityKeys, StringComparer.Ordinal);
        var asMaterialIdentityKeys = new HashSet<string>(asData.MaterialIdentityKeys, StringComparer.Ordinal);
        var asShaderIdentityNames = new Dictionary<string, string>(asData.ShaderIdentityNames, StringComparer.Ordinal);
        AddIndexIdentityKeys(asIndex, "Shader", asShaderIdentityKeys);
        AddIndexIdentityKeys(asIndex, "Material", asMaterialIdentityKeys);
        AddIndexIdentityNames(asIndex, asShaderIdentityNames);
        var explicitLinks = ReadExplicitLinks(
            linkMapPath,
            rdc.ShaderKeys,
            asShaderIdentityKeys,
            asShaderIdentityNames);
        var sessionConsistency = BuildSessionConsistency(capture, runtimeHook, exportHook);
        var result = BuildResult(
            capturePath,
            asReportPath,
            asBindingsPath,
            runtimeHookPath,
            hookPath,
            linkMapPath,
            capture,
            asData,
            asShaderIdentityKeys,
            asMaterialIdentityKeys,
            runtime,
            rdc,
            explicitLinks,
            runtimeHook,
            exportHook,
            sessionConsistency,
            asIndexPath,
            asIndex,
            BuildAsIndexConsistency(asIndex, asData));

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, result.ToJsonString(JsonOptions));
        Console.WriteLine($"已生成: {outputPath}");
        Console.WriteLine($"RDC: {rdc.Shaders.Count} shaders, {rdc.Events.Count} events, {rdc.StageUses.Count} stage uses");
        Console.WriteLine($"AS: {asData.Shaders.Count} shaders, {asData.Materials.Count} materials");
        if (asIndex is not null)
            Console.WriteLine($"AS index: {CountArray(asIndex, "assets")} assets, {CountArray(asIndex, "references")} references, {CountArray(asIndex, "materialShaderBindings")} material→shader bindings");
        Console.WriteLine($"Hook: {runtime.RecordCount} records, {runtime.TypeCount} observed types");
        Console.WriteLine($"显式跨来源关联: {explicitLinks.Count}; 自动跨来源关联: 0");
        Console.WriteLine($"会话编号一致性: {sessionConsistency["status"]?.GetValue<string>() ?? "unknown"}");
        return 0;
    }

    private static JsonObject BuildResult(
        string capturePath,
        string asReportPath,
        string asBindingsPath,
        string runtimeHookPath,
        string? hookPath,
        string? linkMapPath,
        JsonObject capture,
        AsData asData,
        HashSet<string> asShaderIdentityKeys,
        HashSet<string> asMaterialIdentityKeys,
        RuntimeData runtime,
        RdcData rdc,
        JsonArray explicitLinks,
        JsonObject runtimeHook,
        JsonObject? exportHook,
        JsonObject sessionConsistency,
        string? asIndexPath,
        JsonObject? asIndex,
        JsonObject asIndexConsistency)
    {
        var links = new JsonArray();
        var internalExactLinkCount = 0;
        foreach (var stageUse in rdc.StageUses.OfType<JsonObject>())
        {
            links.Add(new JsonObject
            {
                ["kind"] = "rdc_event_stage_to_rdc_shader",
                ["status"] = "exact",
                ["proof"] = "capture event stage references shader ResourceId and variantHash",
                ["eventId"] = stageUse["eventId"]?.DeepClone(),
                ["stage"] = stageUse["stage"]?.DeepClone(),
                ["resourceId"] = stageUse["resourceId"]?.DeepClone(),
                ["shaderHash"] = stageUse["shaderHash"]?.DeepClone(),
                ["interfaceHash"] = stageUse["interfaceHash"]?.DeepClone(),
                ["variantHash"] = stageUse["variantHash"]?.DeepClone()
            });
            internalExactLinkCount++;
        }

        foreach (var material in asData.Materials.OfType<JsonObject>())
        {
            var shaderPathId = material["shaderPathId"]?.DeepClone();
            if (shaderPathId is null) continue;
            links.Add(new JsonObject
            {
                ["kind"] = "as_material_to_as_shader",
                ["status"] = "exact",
                ["proof"] = "AnimeStudio material shader PathID",
                ["materialName"] = material["name"]?.DeepClone(),
                ["materialPathId"] = material["pathId"]?.DeepClone(),
                ["materialIdentity"] = material["assetIdentity"]?.DeepClone(),
                ["shaderPathId"] = shaderPathId,
                ["shaderName"] = material["shaderName"]?.DeepClone(),
                ["shaderIdentity"] = material["shaderIdentity"]?.DeepClone()
            });
            internalExactLinkCount++;

            foreach (var texture in Objects(material["textures"]))
            {
                var textureIdentity = texture["texture"]?["assetIdentity"]?.DeepClone();
                if (textureIdentity is null) continue;
                links.Add(new JsonObject
                {
                    ["kind"] = "as_material_to_as_texture",
                    ["status"] = "exact",
                    ["proof"] = "AnimeStudio serialized Material texture reference",
                    ["materialName"] = material["name"]?.DeepClone(),
                    ["materialPathId"] = material["pathId"]?.DeepClone(),
                    ["materialIdentity"] = material["assetIdentity"]?.DeepClone(),
                    ["property"] = texture["property"]?.DeepClone(),
                    ["textureIdentity"] = textureIdentity
                });
                internalExactLinkCount++;
            }
        }

        var asIndexExactLinkCount = 0;
        if (asIndex is not null)
        {
            foreach (var binding in Objects(asIndex["materialShaderBindings"]))
            {
                links.Add(new JsonObject
                {
                    ["kind"] = "as_index_material_to_as_shader",
                    ["status"] = "exact",
                    ["proof"] = "AnimeStudio asset index SourceCAB+PathID binding",
                    ["materialIdentityKey"] = binding["material"]?.DeepClone(),
                    ["shaderIdentityKey"] = binding["asset"]?.DeepClone(),
                    ["materialName"] = binding["materialName"]?.DeepClone(),
                    ["shaderName"] = binding["assetName"]?.DeepClone(),
                    ["evidence"] = binding["evidence"]?.DeepClone()
                });
                asIndexExactLinkCount++;
            }

            foreach (var binding in Objects(asIndex["materialTextureBindings"]))
            {
                links.Add(new JsonObject
                {
                    ["kind"] = "as_index_material_to_as_texture",
                    ["status"] = "exact",
                    ["proof"] = "AnimeStudio asset index SourceCAB+PathID binding",
                    ["materialIdentityKey"] = binding["material"]?.DeepClone(),
                    ["textureIdentityKey"] = binding["asset"]?.DeepClone(),
                    ["property"] = binding["property"]?.DeepClone(),
                    ["evidence"] = binding["evidence"]?.DeepClone()
                });
                asIndexExactLinkCount++;
            }
        }

        var verifiedIdentityLinkCount = 0;
        foreach (var identity in runtime.IdentityRecords.OfType<JsonObject>())
        {
            var renderDocEventId = ScalarText(identity["renderDocEventId"]);
            if (renderDocEventId is not null && rdc.EventIds.Contains(renderDocEventId))
            {
                links.Add(new JsonObject
                {
                    ["kind"] = "runtime_draw_to_rdc_event",
                    ["status"] = "exact",
                    ["proof"] = "verified runtime draw-context shared event identity",
                    ["renderDocEventId"] = identity["renderDocEventId"]?.DeepClone(),
                    ["frameId"] = identity["frameId"]?.DeepClone()
                });
                verifiedIdentityLinkCount++;
            }

            var shaderIdentityKey = RuntimeIdentityKey(identity, "shader");
            if (shaderIdentityKey is not null && asShaderIdentityKeys.Contains(shaderIdentityKey))
            {
                links.Add(new JsonObject
                {
                    ["kind"] = "runtime_draw_to_as_shader",
                    ["status"] = "exact",
                    ["proof"] = "verified runtime draw-context Shader sourceFile+PathID",
                    ["renderDocEventId"] = identity["renderDocEventId"]?.DeepClone(),
                    ["shaderPathId"] = identity["shaderPathId"]?.DeepClone(),
                    ["shaderSourceFile"] = identity["shaderSourceFile"]?.DeepClone(),
                    ["shaderIdentityKey"] = shaderIdentityKey,
                    ["shaderName"] = identity["shaderName"]?.DeepClone()
                });
                verifiedIdentityLinkCount++;
            }

            var materialIdentityKey = RuntimeIdentityKey(identity, "material");
            if (materialIdentityKey is not null && asMaterialIdentityKeys.Contains(materialIdentityKey))
            {
                links.Add(new JsonObject
                {
                    ["kind"] = "runtime_draw_to_as_material",
                    ["status"] = "exact",
                    ["proof"] = "verified runtime draw-context Material sourceFile+PathID",
                    ["renderDocEventId"] = identity["renderDocEventId"]?.DeepClone(),
                    ["materialPathId"] = identity["materialPathId"]?.DeepClone(),
                    ["materialSourceFile"] = identity["materialSourceFile"]?.DeepClone(),
                    ["materialIdentityKey"] = materialIdentityKey,
                    ["materialName"] = identity["materialName"]?.DeepClone()
                });
                verifiedIdentityLinkCount++;
            }
        }

        foreach (var explicitLink in explicitLinks.OfType<JsonObject>())
        {
            var copy = explicitLink.DeepClone();
            copy["kind"] = "rdc_shader_to_as_shader";
            copy["status"] = "explicit";
            copy["proof"] = "validated user-supplied link-map";
            links.Add(copy);
        }

        var unresolved = new JsonArray
        {
            new JsonObject
            {
                ["kind"] = "rdc_shader_to_as_shader",
                ["status"] = explicitLinks.Count == 0 && verifiedIdentityLinkCount == 0 ? "unresolved" : "partially_resolved",
                ["reason"] = "RDC shader hashes/ResourceIds are capture-local and AS Shader PathIDs/names are asset identities; no shared identity was found",
                ["requiredEvidence"] = "validated link-map or a capture/runtime hook carrying the original Unity shader identity"
            },
            new JsonObject
            {
                ["kind"] = "rdc_event_to_as_material",
                ["status"] = runtime.IdentityRecords.Count == 0 ? "unresolved" : "partially_resolved",
                ["reason"] = "RDC exposes bound GPU resources and pipeline state, but not the original Unity Material PathID/name",
                ["requiredEvidence"] = "material identity captured at runtime or explicit event-to-material mapping"
            },
            new JsonObject
            {
                ["kind"] = "runtime_object_to_as_asset",
                ["status"] = runtime.IdentityRecords.Count == 0 ? "unresolved" : "partially_resolved",
                ["reason"] = "runtime Hook records contain native object/type observations, but no Unity asset PathID or source path",
                ["requiredEvidence"] = "asset identity fields observed by a verified runtime hook"
            },
            new JsonObject
            {
                ["kind"] = "runtime_transfer_to_rdc_eid",
                ["status"] = runtime.IdentityRecords.Any() ? "partially_resolved" : "unresolved",
                ["reason"] = "runtime Hook and RenderDoc capture have no common event identity",
                ["requiredEvidence"] = "shared frame/event marker recorded by both sides"
            }
        };

        return new JsonObject
        {
            ["format"] = "my-hook.alignment.v2",
            ["tool"] = new JsonObject
            {
                ["name"] = "my-hook-tool",
                ["alignmentPolicy"] = "exact-or-explicit-only",
                ["heuristicsUsed"] = false
            },
            ["sources"] = new JsonObject
            {
                ["rdc"] = Source(capturePath, capture["format"]?.GetValue<string>()),
                ["asReport"] = Source(asReportPath, asData.ReportSchema),
                ["asBindings"] = Source(asBindingsPath, asData.BindingsSchema),
                ["runtimeHook"] = Source(runtimeHookPath, runtimeHook["format"]?.GetValue<string>()),
                ["exportHook"] = hookPath is null ? null : Source(hookPath, exportHook?["format"]?.GetValue<string>()),
                ["linkMap"] = linkMapPath is null ? null : Source(linkMapPath, "explicit-link-map"),
                ["asIndex"] = asIndexPath is null ? null : Source(asIndexPath, asIndex?["schema"]?.GetValue<string>())
            },
            ["session"] = sessionConsistency,
            ["rdc"] = new JsonObject
            {
                ["summary"] = capture["summary"]?.DeepClone(),
                ["shaders"] = rdc.Shaders,
                ["events"] = rdc.Events,
                ["stageUses"] = rdc.StageUses
            },
            ["as"] = new JsonObject
            {
                ["reportSchema"] = asData.ReportSchema,
                ["bindingsSchema"] = asData.BindingsSchema,
                ["identityDomain"] = "serialized-asset:sourceFile+pathId",
                ["identitySummary"] = asData.IdentitySummary,
                ["shaders"] = asData.Shaders,
                ["materials"] = asData.Materials
            },
            ["asIndex"] = asIndex is null ? null : SelectAsIndexSummary(asIndex),
            ["asConsistency"] = asIndexConsistency,
            ["runtime"] = new JsonObject
            {
                ["format"] = runtimeHook["format"]?.DeepClone(),
                ["recordCount"] = runtime.RecordCount,
                ["typeCount"] = runtime.TypeCount,
                ["types"] = runtime.Types,
                ["diagnostics"] = runtime.Diagnostics,
                ["verifiedIdentityRecords"] = runtime.IdentityRecords,
                ["rejectedIdentityRecords"] = runtime.RejectedIdentityRecords
            },
            ["links"] = links,
            ["unresolved"] = unresolved,
            ["summary"] = new JsonObject
            {
                ["exactInternalLinks"] = internalExactLinkCount,
                ["asIndexExactLinks"] = asIndexExactLinkCount,
                ["exactCrossSourceLinks"] = verifiedIdentityLinkCount,
                ["explicitCrossSourceLinks"] = explicitLinks.Count,
                ["automaticCrossSourceLinks"] = 0,
                ["unresolvedCrossSourceLinks"] = unresolved.Count
            }
        };
    }

    private static JsonObject BuildAsIndexConsistency(JsonObject? index, AsData asData)
    {
        if (index is null)
        {
            return new JsonObject
            {
                ["status"] = "not_provided",
                ["policy"] = "AS index comparison is optional; report/bindings remain independently auditable"
            };
        }

        var reportShaderBindings = new HashSet<string>(StringComparer.Ordinal);
        var reportTextureBindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var material in asData.Materials.OfType<JsonObject>())
        {
            var materialKey = StringValue(material["assetIdentity"] as JsonObject, "key");
            if (materialKey is null)
                continue;
            var shaderKey = StringValue(material["shaderIdentity"] as JsonObject, "key");
            if (shaderKey is not null)
                reportShaderBindings.Add($"{materialKey}|{shaderKey}");
            foreach (var texture in Objects(material["textures"]))
            {
                var textureKey = StringValue(texture["texture"]?["assetIdentity"] as JsonObject, "key");
                var property = StringValue(texture, "property") ?? "";
                if (textureKey is not null)
                    reportTextureBindings.Add($"{materialKey}|{property}|{textureKey}");
            }
        }

        var indexShaderBindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in Objects(index["materialShaderBindings"]))
        {
            var material = StringValue(binding, "material");
            var shader = StringValue(binding, "asset");
            if (material is not null && shader is not null)
                indexShaderBindings.Add($"{material}|{shader}");
        }
        var indexTextureBindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in Objects(index["materialTextureBindings"]))
        {
            var material = StringValue(binding, "material");
            var property = StringValue(binding, "property") ?? "";
            var texture = StringValue(binding, "asset");
            if (material is not null && texture is not null)
                indexTextureBindings.Add($"{material}|{property}|{texture}");
        }

        return new JsonObject
        {
            ["status"] = reportShaderBindings.SetEquals(indexShaderBindings)
                && reportTextureBindings.SetEquals(indexTextureBindings)
                ? "exact"
                : "mismatch",
            ["reportShaderBindingCount"] = reportShaderBindings.Count,
            ["indexShaderBindingCount"] = indexShaderBindings.Count,
            ["reportTextureBindingCount"] = reportTextureBindings.Count,
            ["indexTextureBindingCount"] = indexTextureBindings.Count,
            ["shaderOnlyInReport"] = StringArray(reportShaderBindings.Except(indexShaderBindings)),
            ["shaderOnlyInIndex"] = StringArray(indexShaderBindings.Except(reportShaderBindings)),
            ["textureOnlyInReport"] = StringArray(reportTextureBindings.Except(indexTextureBindings)),
            ["textureOnlyInIndex"] = StringArray(indexTextureBindings.Except(reportTextureBindings)),
            ["policy"] = "compare serialized SourceCAB+PathID identities and texture property names; no name heuristic"
        };
    }

    private static JsonObject BuildSessionConsistency(
        JsonObject capture,
        JsonObject runtimeHook,
        JsonObject? exportHook)
    {
        var sources = new JsonObject
        {
            ["rdc"] = SessionSource(capture),
            ["runtimeHook"] = SessionSource(runtimeHook),
            ["exportHook"] = exportHook is null ? null : SessionSource(exportHook)
        };
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources.Select(pair => pair.Value).OfType<JsonObject>())
            foreach (var id in StringValues(source["captureSessionIds"]))
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);

        var sourceCount = sources.Select(pair => pair.Value)
            .OfType<JsonObject>()
            .Count(source => StringValues(source["captureSessionIds"]).Any());
        var status = ids.Count > 1
            ? "mismatch"
            : ids.Count == 1 && sourceCount >= 2
                ? "exact"
                : ids.Count == 1
                    ? "partial"
                    : "not_available";
        return new JsonObject
        {
            ["status"] = status,
            ["captureSessionIds"] = StringArray(ids),
            ["sources"] = sources,
            ["policy"] = "session ID is evidence metadata; it does not create Draw/Material identity"
        };
    }

    private static JsonObject SessionSource(JsonObject root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        AddSessionId(ids, root["captureSessionId"]);
        if (root["session"] is JsonObject session)
            AddSessionId(ids, session["captureSessionId"]);
        foreach (var record in Objects(root["records"]))
            AddSessionId(ids, record["captureSessionId"]);
        return new JsonObject
        {
            ["captureSessionIds"] = StringArray(ids)
        };
    }

    private static void AddSessionId(HashSet<string> ids, JsonNode? value)
    {
        var text = ScalarText(value);
        if (!string.IsNullOrWhiteSpace(text)) ids.Add(text);
    }

    private static IEnumerable<string> StringValues(JsonNode? node) => node is JsonArray array
        ? array.Select(ScalarText).OfType<string>()
        : Enumerable.Empty<string>();

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values.OrderBy(x => x, StringComparer.Ordinal))
            result.Add(value);
        return result;
    }

    private static JsonObject Source(string path, string? schema) => new()
    {
        ["path"] = path,
        ["sha256"] = Sha256(path),
        ["schema"] = schema
    };

    private static RdcData ReadRdc(JsonObject capture)
    {
        var shaders = new JsonArray();
        var shaderKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Objects(capture["shaders"]))
        {
            var copy = Select(node, "stage", "resourceId", "shaderHash", "interfaceHash", "rawByteSize", "source", "reflection", "reconstructedHlsl", "status", "statusReason");
            shaders.Add(copy);
            AddKey(shaderKeys, node, "shaderHash");
            AddKey(shaderKeys, node, "variantHash");
        }

        var events = new JsonArray();
        var stageUses = new JsonArray();
        foreach (var node in Objects(capture["events"]))
        {
            var eventId = IntValue(node, "eventId");
            if (node["eventId"] is JsonNode eventNode)
                eventIds.Add(ScalarText(eventNode) ?? eventId.ToString());
            var eventCopy = Select(node, "eventId", "name", "drawcall", "stages", "bindings");
            events.Add(eventCopy);
            foreach (var stage in Objects(node["stages"]))
            {
                var stageCopy = Select(stage, "stage", "resourceId", "shaderHash", "interfaceHash", "variantHash", "pipelineId", "encoding", "entryPoint", "source", "reflection", "reconstructedHlsl", "status", "statusReason");
                stageCopy["eventId"] = eventId;
                stageUses.Add(stageCopy);
                AddKey(shaderKeys, stage, "shaderHash");
                AddKey(shaderKeys, stage, "variantHash");
            }
        }

        return new RdcData(shaders, events, stageUses, shaderKeys, eventIds);
    }

    private static AsData ReadAs(JsonObject report, JsonObject bindings)
    {
        var shaders = new JsonArray();
        var materials = new JsonArray();
        var shaderNames = new HashSet<string>(StringComparer.Ordinal);
        var shaderIdentityKeys = new HashSet<string>(StringComparer.Ordinal);
        var materialIdentityKeys = new HashSet<string>(StringComparer.Ordinal);
        var shaderIdentityNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenIdentities = new Dictionary<string, (string Kind, string? Name)>(StringComparer.Ordinal);
        var duplicateIdentities = new JsonArray();
        var incompleteIdentities = new JsonArray();
        var completeIdentityCount = 0;
        var incompleteIdentityCount = 0;
        var textureReferenceCount = 0;
        foreach (var node in Objects(report["shaders"]))
        {
            var copy = Select(node, "name", "pathId", "path", "source", "byteSize", "typeTreeHash", "dataStatus");
            var identity = AssetIdentity(node, StringValue(node, "name"));
            copy["assetIdentity"] = identity;
            AddShaderArtifactPaths(copy, node);
            RegisterIdentity("Shader", StringValue(node, "name"), identity, seenIdentities,
                duplicateIdentities, incompleteIdentities, ref completeIdentityCount, ref incompleteIdentityCount);
            shaders.Add(copy);
            var name = StringValue(node, "name");
            if (!string.IsNullOrWhiteSpace(name)) shaderNames.Add(name);
            AddIdentityKey(shaderIdentityKeys, identity);
            if (StringValue(identity, "key") is { } shaderIdentityKey)
                shaderIdentityNames[shaderIdentityKey] = name ?? StringValue(identity, "name") ?? "<unnamed>";
        }

        foreach (var node in Objects(bindings["materials"]))
        {
            var shader = node["shader"] as JsonObject;
            var copy = Select(node, "name", "pathId", "path", "shaderPath", "shaderRelativePath", "status", "shaderKeywords", "shaderKeywordNames", "declaredProperties", "unboundProperties", "textures", "floats", "ints", "colors");
            var materialIdentity = AssetIdentity(node, StringValue(node, "name"));
            copy["assetIdentity"] = materialIdentity;
            AddIdentityKey(materialIdentityKeys, materialIdentity);
            RegisterIdentity("Material", StringValue(node, "name"), materialIdentity, seenIdentities,
                duplicateIdentities, incompleteIdentities, ref completeIdentityCount, ref incompleteIdentityCount);
            if (shader is not null)
            {
                copy["shaderPathId"] = shader["pathId"]?.DeepClone();
                copy["shaderName"] = shader["resolvedName"]?.DeepClone();
                var shaderIdentity = AssetIdentity(shader, StringValue(shader, "resolvedName"));
                copy["shaderIdentity"] = shaderIdentity;
                AddIdentityKey(shaderIdentityKeys, shaderIdentity);
                if (StringValue(shaderIdentity, "key") is { } shaderIdentityKey)
                    shaderIdentityNames.TryAdd(
                        shaderIdentityKey,
                        StringValue(shader, "resolvedName") ?? StringValue(shaderIdentity, "name") ?? "<unnamed>");
                RegisterIdentity("Shader", StringValue(shader, "resolvedName"), shaderIdentity,
                    seenIdentities, duplicateIdentities, incompleteIdentities,
                    ref completeIdentityCount, ref incompleteIdentityCount);
            }

            if (copy["textures"] is JsonArray textures)
            {
                foreach (var textureNode in textures.OfType<JsonObject>())
                {
                    if (textureNode["texture"] is not JsonObject texture) continue;
                    var textureIdentity = AssetIdentity(texture, StringValue(texture, "resolvedName"));
                    texture["assetIdentity"] = textureIdentity;
                    RegisterIdentity("Texture", StringValue(texture, "resolvedName"), textureIdentity,
                        seenIdentities, duplicateIdentities, incompleteIdentities,
                        ref completeIdentityCount, ref incompleteIdentityCount);
                    textureReferenceCount++;
                }
            }
            materials.Add(copy);
        }

        var identitySummary = new JsonObject
        {
            ["identityDomain"] = "serialized-asset:sourceFile+pathId",
            ["identityKeyFormat"] = "<sourceFile>#<pathId>",
            ["shaderCount"] = shaders.Count,
            ["materialCount"] = materials.Count,
            ["textureReferenceCount"] = textureReferenceCount,
            ["identityObservationCount"] = completeIdentityCount + incompleteIdentityCount,
            ["uniqueIdentityKeyCount"] = seenIdentities.Count,
            ["completeIdentityCount"] = completeIdentityCount,
            ["incompleteIdentityCount"] = incompleteIdentityCount,
            ["duplicateIdentityKeys"] = duplicateIdentities,
            ["incompleteIdentities"] = incompleteIdentities
        };
        return new AsData(
            report["schema"]?.GetValue<string>() ?? "unknown",
            bindings["schema"]?.GetValue<string>() ?? "unknown",
            shaders,
            materials,
            shaderNames,
            shaderIdentityKeys,
            materialIdentityKeys,
            shaderIdentityNames,
            identitySummary);
    }

    private static void AddShaderArtifactPaths(JsonObject target, JsonObject source)
    {
        if (source["shaderLab"] is JsonObject shaderLab)
        {
            var path = StringValue(shaderLab, "path");
            if (!string.IsNullOrWhiteSpace(path))
                target["shaderLabPath"] = path;
        }

        if (source["sourceShader"] is not JsonObject sourceShader)
            return;

        var sourceInfo = Select(sourceShader, "status", "format", "directSourceImport",
            "directory", "variantCount", "uniqueSourceCount");
        if (sourceInfo.Count > 0)
            target["sourceShader"] = sourceInfo;

        var mappings = new JsonArray();
        foreach (var mapping in Objects(sourceShader["variantMappings"]))
        {
            var selected = Select(mapping, "platform", "subShader", "pass", "variant",
                "shaderRelativePath", "shaderLabName", "vertex", "fragment");
            if (selected.Count > 0)
                mappings.Add(selected);
        }
        if (mappings.Count > 0)
            target["sourceShaderVariantMappings"] = mappings;
    }

    private static JsonObject AssetIdentity(JsonObject node, string? name)
    {
        var source = node["source"] as JsonObject;
        var sourceFile = StringValue(source, "fileName") ?? StringValue(node, "sourceFile");
        var pathId = node["pathId"]?.DeepClone();
        var pathIdText = ScalarText(pathId);
        var identity = new JsonObject
        {
            ["domain"] = "serialized-asset:sourceFile+pathId",
            ["status"] = !string.IsNullOrWhiteSpace(sourceFile) && !string.IsNullOrWhiteSpace(pathIdText)
                ? "complete"
                : "incomplete"
        };
        if (!string.IsNullOrWhiteSpace(sourceFile)) identity["sourceFile"] = sourceFile;
        if (pathId is not null) identity["pathId"] = pathId;
        if (!string.IsNullOrWhiteSpace(name)) identity["name"] = name;
        if (!string.IsNullOrWhiteSpace(sourceFile) && !string.IsNullOrWhiteSpace(pathIdText))
            identity["key"] = $"{sourceFile}#{pathIdText}";
        var originalPath = StringValue(source, "originalPath");
        if (!string.IsNullOrWhiteSpace(originalPath))
        {
            identity["provenance"] = new JsonObject
            {
                ["originalPath"] = originalPath,
                ["pathMeaning"] = "AS source provenance; not a runtime-resolved Unity path"
            };
        }
        return identity;
    }

    private static void RegisterIdentity(
        string kind,
        string? name,
        JsonObject identity,
        Dictionary<string, (string Kind, string? Name)> seen,
        JsonArray duplicates,
        JsonArray incomplete,
        ref int completeCount,
        ref int incompleteCount)
    {
        if (StringValue(identity, "status") == "complete") completeCount++;
        else
        {
            incompleteCount++;
            incomplete.Add(new JsonObject
            {
                ["kind"] = kind,
                ["name"] = name,
                ["identity"] = identity.DeepClone()
            });
        }

        var key = StringValue(identity, "key");
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!seen.TryGetValue(key, out var previous))
        {
            seen[key] = (kind, name);
            return;
        }
        if (previous.Kind == kind && string.Equals(previous.Name, name, StringComparison.Ordinal)) return;
        duplicates.Add(new JsonObject
        {
            ["key"] = key,
            ["first"] = new JsonObject { ["kind"] = previous.Kind, ["name"] = previous.Name },
            ["next"] = new JsonObject { ["kind"] = kind, ["name"] = name },
            ["reason"] = "same serialized sourceFile+PathID was observed with different semantic labels"
        });
    }

    private static RuntimeData ReadRuntime(JsonObject hook)
    {
        var types = new Dictionary<string, TypeStats>(StringComparer.Ordinal);
        var records = hook["records"] as JsonArray;
        var diagnostics = new JsonArray();
        var identityRecords = new JsonArray();
        var rejectedIdentityRecords = new JsonArray();
        foreach (var record in Objects(records))
        {
            if (StringValue(record, "kind") == "unity.draw-context")
            {
                var rejectionReason = StringValue(record, "status") != "verified"
                    ? "identity event is not marked verified"
                    : !HasAll(record, "renderDocEventId", "shaderSourceFile", "shaderPathId",
                        "materialSourceFile", "materialPathId")
                        ? "verified identity event lacks the complete same-record Shader/Material sourceFile#PathID and RenderDoc EID set"
                        : null;
                if (rejectionReason is null)
                    identityRecords.Add(Select(record, "status", "frameId", "renderDocEventId", "drawId",
                        "shaderSourceFile", "shaderPathId", "shaderName",
                        "materialSourceFile", "materialPathId", "materialName"));
                else
                {
                    var rejected = Select(record, "status", "frameId", "renderDocEventId", "drawId",
                        "shaderSourceFile", "shaderPathId", "shaderName",
                        "materialSourceFile", "materialPathId", "materialName");
                    rejected["reason"] = rejectionReason;
                    rejectedIdentityRecords.Add(rejected);
                }
                continue;
            }
            var typeId = IntValue(record, "typeId");
            var typeName = StringValue(record, "typeName") ?? "<unknown>";
            var key = $"{typeId}:{typeName}";
            if (!types.TryGetValue(key, out var stats))
                stats = types[key] = new TypeStats(typeId, typeName);
            stats.Count++;
            AddSet(stats.Objects, StringValue(record, "object"));
            AddSet(stats.Transfers, StringValue(record, "transfer"));
            AddSet(stats.Vtables, StringValue(record, "vtable"));
        }

        foreach (var diagnostic in Objects(hook["diagnostics"]))
            diagnostics.Add(Select(diagnostic, "kind", "typeId", "typeName", "stage", "reason", "count"));

        var typeArray = new JsonArray();
        foreach (var stats in types.Values.OrderBy(x => x.TypeName, StringComparer.Ordinal).ThenBy(x => x.TypeId))
        {
            typeArray.Add(new JsonObject
            {
                ["typeId"] = stats.TypeId,
                ["typeName"] = stats.TypeName,
                ["recordCount"] = stats.Count,
                ["distinctObjects"] = stats.Objects.Count,
                ["distinctTransfers"] = stats.Transfers.Count,
                ["distinctVtables"] = stats.Vtables.Count
            });
        }

        return new RuntimeData(records?.Count ?? 0, typeArray.Count, typeArray, diagnostics, identityRecords, rejectedIdentityRecords);
    }

    private static JsonArray ReadExplicitLinks(
        string? path,
        HashSet<string> validKeys,
        HashSet<string> shaderIdentityKeys,
        Dictionary<string, string> shaderIdentityNames)
    {
        if (path is null) return new JsonArray();
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonArray
            ?? throw new InvalidDataException("link-map 必须是 JSON 数组");
        var result = new JsonArray();
        foreach (var node in root.OfType<JsonObject>())
        {
            var key = StringValue(node, "shaderHash") ?? StringValue(node, "variantHash");
            var identityKey = StringValue(node, "asShaderIdentityKey");
            var name = StringValue(node, "asShaderName");
            if (key is null || !validKeys.Contains(key))
                throw new InvalidDataException($"link-map 中的 RDC shader/variant 标识不存在: {key ?? "<empty>"}");
            if (identityKey is null || !shaderIdentityKeys.Contains(identityKey))
                throw new InvalidDataException(
                    $"link-map 必须引用 AS 的完整 Shader 身份 asShaderIdentityKey，且该身份必须存在: {identityKey ?? "<empty>"}");
            if (name is not null && (!shaderIdentityNames.TryGetValue(identityKey, out var expectedName)
                || !string.Equals(name, expectedName, StringComparison.Ordinal)))
                throw new InvalidDataException(
                    $"link-map 的 asShaderName 与 asShaderIdentityKey 不一致: {identityKey}");
            result.Add(node.DeepClone());
        }
        return result;
    }

    private static JsonObject ReadAsIndex(string path)
    {
        var root = ReadObject(path, "AS asset index");
        var schema = StringValue(root, "schema");
        var identityDomain = StringValue(root, "identityDomain");
        if (!string.Equals(schema, "anime-studio.asset-index.v1", StringComparison.Ordinal)
            || !string.Equals(identityDomain, "serialized-asset:sourceFile+pathId", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"AS index 身份协议不兼容: schema={schema ?? "<missing>"}, identityDomain={identityDomain ?? "<missing>"}");
        }

        var assetKeys = new HashSet<string>(StringComparer.Ordinal);
        var errors = new JsonArray();
        var duplicateIdentityCount = 0;
        foreach (var asset in Objects(root["assets"]))
        {
            var key = StringValue(asset["identity"] as JsonObject, "key");
            if (key is null || !assetKeys.Add(key))
            {
                duplicateIdentityCount++;
                errors.Add(new JsonObject
                {
                    ["kind"] = "asset_identity_duplicate",
                    ["identityKey"] = key,
                    ["asset"] = asset["name"]?.DeepClone(),
                    ["reason"] = key is null
                        ? "asset identity key is missing"
                        : "asset identity key occurs more than once"
                });
            }
        }
        var identityErrors = ValidateIndexIdentities(root["assets"], assetKeys);
        foreach (var error in identityErrors)
            errors.Add(error);
        var bindingErrors = new JsonArray();
        ValidateIndexBindings(root["materialShaderBindings"], assetKeys, bindingErrors, "material_shader");
        ValidateIndexBindings(root["materialTextureBindings"], assetKeys, bindingErrors, "material_texture");
        foreach (var error in bindingErrors)
            errors.Add(error);
        var pathValidation = root["pathValidation"] as JsonObject;
        var pathErrors = pathValidation?["errors"] as JsonArray ?? [];
        foreach (var error in pathErrors)
            if (error is not null) errors.Add(error.DeepClone());
        if (string.Equals(StringValue(pathValidation, "status"), "invalid", StringComparison.Ordinal)
            && pathErrors.Count == 0)
        {
            errors.Add(new JsonObject
            {
                ["kind"] = "path_validation",
                ["reason"] = "AS index reports invalid path validation without a usable error list"
            });
        }
        var indexDiagnostics = Objects(root["diagnostics"]).ToArray();
        foreach (var diagnostic in indexDiagnostics)
            errors.Add(new JsonObject
            {
                ["kind"] = "as_index_diagnostic",
                ["diagnostic"] = diagnostic.DeepClone()
            });
        root["validation"] = new JsonObject
        {
            ["status"] = errors.Count == 0 ? "valid" : "invalid",
            ["assetKeyCount"] = assetKeys.Count,
            ["bindingErrorCount"] = bindingErrors.Count,
            ["identityErrorCount"] = identityErrors.Count,
            ["duplicateIdentityCount"] = duplicateIdentityCount,
            ["pathErrorCount"] = pathErrors.Count,
            ["diagnosticErrorCount"] = indexDiagnostics.Length,
            ["validationErrorCount"] = errors.Count,
            ["errors"] = errors
        };
        if (errors.Count > 0)
            throw new InvalidDataException("AS index 身份、绑定或路径校验失败，拒绝用于精确对齐");
        return root;
    }

    private static List<JsonNode> ValidateIndexIdentities(JsonNode? node, HashSet<string> assetKeys)
    {
        var errors = new List<JsonNode>();
        foreach (var asset in Objects(node))
        {
            var identity = asset["identity"] as JsonObject;
            var sourceFile = StringValue(identity, "sourceFile");
            var pathId = ScalarText(identity?["pathId"]);
            var key = StringValue(identity, "key");
            if (string.IsNullOrWhiteSpace(sourceFile)
                || string.IsNullOrWhiteSpace(pathId)
                || string.IsNullOrWhiteSpace(key)
                || !string.Equals(key, $"{sourceFile}#{pathId}", StringComparison.Ordinal))
            {
                errors.Add(new JsonObject
                {
                    ["kind"] = "asset_identity",
                    ["asset"] = asset["name"]?.DeepClone(),
                    ["identity"] = identity?.DeepClone(),
                    ["reason"] = "asset identity must contain sourceFile, pathId and the exact sourceFile#pathId key"
                });
            }
        }
        return errors;
    }

    private static void ValidateIndexBindings(
        JsonNode? node,
        HashSet<string> assetKeys,
        JsonArray errors,
        string bindingKind)
    {
        foreach (var binding in Objects(node))
        {
            var material = StringValue(binding, "material");
            var asset = StringValue(binding, "asset");
            if (material is null || !assetKeys.Contains(material))
                errors.Add(new JsonObject
                {
                    ["kind"] = bindingKind,
                    ["missing"] = "material",
                    ["identityKey"] = material,
                    ["binding"] = binding.DeepClone()
                });
            if (asset is null || !assetKeys.Contains(asset))
                errors.Add(new JsonObject
                {
                    ["kind"] = bindingKind,
                    ["missing"] = "asset",
                    ["identityKey"] = asset,
                    ["binding"] = binding.DeepClone()
                });
        }
    }

    private static JsonObject SelectAsIndexSummary(JsonObject index)
    {
        return new JsonObject
        {
            ["schema"] = StringValue(index, "schema"),
            ["identityDomain"] = StringValue(index, "identityDomain"),
            ["identityKeyFormat"] = StringValue(index, "identityKeyFormat"),
            ["input"] = index["input"]?.DeepClone(),
            ["summary"] = index["summary"]?.DeepClone(),
            ["validation"] = index["validation"]?.DeepClone(),
            ["pathValidation"] = index["pathValidation"]?.DeepClone(),
            ["diagnostics"] = index["diagnostics"]?.DeepClone() ?? new JsonArray(),
            ["materialShaderBindings"] = index["materialShaderBindings"]?.DeepClone() ?? new JsonArray(),
            ["materialTextureBindings"] = index["materialTextureBindings"]?.DeepClone() ?? new JsonArray()
        };
    }

    private static int CountArray(JsonObject? node, string key) =>
        node?[key] is JsonArray array ? array.Count : 0;

    private static JsonObject ReadObject(string path, string label) =>
        JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidDataException($"{label} 不是 JSON 对象: {path}");

    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array
        ? array.OfType<JsonObject>()
        : Enumerable.Empty<JsonObject>();

    private static JsonObject Select(JsonObject source, params string[] keys)
    {
        var result = new JsonObject();
        foreach (var key in keys)
            if (source[key] is JsonNode value) result[key] = value.DeepClone();
        return result;
    }

    private static void AddKey(HashSet<string> keys, JsonObject node, string key)
    {
        var value = StringValue(node, key);
        if (!string.IsNullOrWhiteSpace(value)) keys.Add(value);
    }

    private static void AddSet(HashSet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) set.Add(value);
    }

    private static void AddIdentityKey(HashSet<string> keys, JsonObject identity)
    {
        if (StringValue(identity, "key") is { } key)
            keys.Add(key);
    }

    private static void AddIndexIdentityKeys(
        JsonObject? index,
        string type,
        HashSet<string> target)
    {
        if (index is null)
            return;
        foreach (var asset in Objects(index["assets"]))
        {
            if (!string.Equals(StringValue(asset, "type"), type, StringComparison.Ordinal))
                continue;
            var key = StringValue(asset["identity"] as JsonObject, "key");
            if (!string.IsNullOrWhiteSpace(key))
                target.Add(key);
        }
    }

    private static void AddIndexIdentityNames(
        JsonObject? index,
        Dictionary<string, string> target)
    {
        if (index is null)
            return;
        foreach (var asset in Objects(index["assets"]))
        {
            if (!string.Equals(StringValue(asset, "type"), "Shader", StringComparison.Ordinal))
                continue;
            var key = StringValue(asset["identity"] as JsonObject, "key");
            var name = StringValue(asset, "name");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
                continue;
            if (target.TryGetValue(key, out var existing)
                && !string.Equals(existing, name, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"AS Shader 身份名称冲突，拒绝显式对齐: {key} ({existing} != {name})");
            }
            target[key] = name;
        }
    }

    private static string? RuntimeIdentityKey(JsonObject record, string prefix)
    {
        var sourceFile = StringValue(record, $"{prefix}SourceFile");
        var pathId = StringValue(record, $"{prefix}PathId");
        return !string.IsNullOrWhiteSpace(sourceFile) && !string.IsNullOrWhiteSpace(pathId)
            ? $"{sourceFile}#{pathId}"
            : null;
    }

    private static bool HasAny(JsonObject node, params string[] keys) =>
        keys.Any(key => node[key] is JsonNode value && ScalarText(value) is not null);

    private static bool HasAll(JsonObject node, params string[] keys) =>
        keys.All(key => node[key] is JsonNode value && ScalarText(value) is not null);

    private static string? StringValue(JsonObject? node, string key)
    {
        if (node?[key] is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<long>(out var integer)) return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value.TryGetValue<ulong>(out var unsignedInteger)) return unsignedInteger.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var number)) return number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (value.TryGetValue<bool>(out var boolean)) return boolean ? "true" : "false";
        return value.ToString();
    }
    private static int IntValue(JsonObject node, string key) => node[key]?.GetValue<int>() ?? 0;
    private static string? ScalarText(JsonNode? value) => value switch
    {
        null => null,
        JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) => text,
        _ => value.ToString()
    };
    private static string RequiredValue(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values) && values.Count > 0
            ? values[^1]
            : throw new ArgumentException($"align 缺少 --{key}");

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

    private static string ResolveEmbeddedFile(
        JsonObject? exportHook,
        string? hookPath,
        string property,
        string label)
    {
        if (exportHook is null || hookPath is null)
            throw new ArgumentException($"align 缺少 --{property}，且没有提供带嵌入 AS 文件的 --hook");

        var relative = StringValue(exportHook["as"] as JsonObject, property);
        if (string.IsNullOrWhiteSpace(relative))
            throw new InvalidDataException($"--hook 未包含 {property}；请显式传入对应路径");

        var path = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(hookPath)!,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到嵌入的 {label}", path);
        return path;
    }

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }
            var key = arg[2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "1";
            if (!result.TryGetValue(key, out var values)) result[key] = values = [];
            values.Add(value);
        }
        result["__positionals"] = positionals;
        return result;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record RdcData(JsonArray Shaders, JsonArray Events, JsonArray StageUses, HashSet<string> ShaderKeys, HashSet<string> EventIds);
    private sealed record AsData(
        string ReportSchema,
        string BindingsSchema,
        JsonArray Shaders,
        JsonArray Materials,
        HashSet<string> ShaderNames,
        HashSet<string> ShaderIdentityKeys,
        HashSet<string> MaterialIdentityKeys,
        Dictionary<string, string> ShaderIdentityNames,
        JsonObject IdentitySummary);
    private sealed record RuntimeData(int RecordCount, int TypeCount, JsonArray Types, JsonArray Diagnostics, JsonArray IdentityRecords, JsonArray RejectedIdentityRecords);
    private sealed class TypeStats(int typeId, string typeName)
    {
        public int TypeId { get; } = typeId;
        public string TypeName { get; } = typeName;
        public int Count { get; set; }
        public HashSet<string> Objects { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Transfers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Vtables { get; } = new(StringComparer.Ordinal);
    }
}
