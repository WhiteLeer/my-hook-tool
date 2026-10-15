using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace MyHookTool;

internal static class VerifyCommand
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly string[] DefaultCriticalTypes =
    [
        "Shader",
        "Material",
        "AnimationClip",
        "SkinnedMeshRenderer",
        "Mesh",
        "GameObject",
        "Transform",
        "ParticleSystem",
        "ParticleSystemRenderer"
    ];

    public static int Run(string[] args)
    {
        var options = ParseOptions(args);
        var asIndexPath = RequiredFile(options, "as-index");
        var typeTreeReportPath = RequiredFile(options, "typetree-report");
        var outputPath = Path.GetFullPath(RequiredValue(options, "output"));
        var analysisPath = OptionalFile(options, "analysis");
        var profilePath = OptionalFile(options, "profile");
        var hookPath = OptionalFile(options, "hook");

        var asIndex = ReadObject(asIndexPath, "AS index");
        var typeTree = ReadObject(typeTreeReportPath, "TypeTree report");
        var analysis = analysisPath is null ? null : ReadObject(analysisPath, "runtime analysis");
        var profile = profilePath is null ? null : ReadObject(profilePath, "profile");
        var hook = hookPath is null ? null : ReadObject(hookPath, ".hook");

        var checks = new JsonArray();
        var asCheck = VerifyAsIndex(asIndex);
        checks.Add(asCheck);
        var materialCheck = VerifyMaterialBindings(asIndex);
        checks.Add(materialCheck);
        var typeTreeCheck = VerifyTypeTree(typeTree, options, profile);
        checks.Add(typeTreeCheck);
        var runtimeCheck = VerifyRuntimeBridge(analysis, profile);
        checks.Add(runtimeCheck);
        var hookCheck = hook is null
            ? new JsonObject
            {
                ["name"] = "hookEmbedding",
                ["status"] = "not_evaluated",
                ["reason"] = "未提供 --hook；不影响离线 AS/TypeTree 校验"
            }
            : VerifyHookEmbedding(hook, hookPath!, asIndexPath);
        checks.Add(hookCheck);

        var status = OverallStatus(checks);
        var report = new JsonObject
        {
            ["schema"] = "my-hook.verification.v1",
            ["status"] = status,
            ["source"] = new JsonObject
            {
                ["asIndex"] = FileInfo(asIndexPath),
                ["typeTreeReport"] = FileInfo(typeTreeReportPath),
                ["analysis"] = analysisPath is null ? null : FileInfo(analysisPath),
                ["profile"] = profilePath is null ? null : FileInfo(profilePath),
                ["hook"] = hookPath is null ? null : FileInfo(hookPath)
            },
            ["checks"] = checks,
            ["policy"] = new JsonObject
            {
                ["identity"] = "AS identities use sourceFile#pathId; no name, order, count, address, ResourceId or hash inference",
                ["typeTree"] = "complete means every requested type has serialized field nodes and passes field integrity",
                ["runtimeBridge"] = "verified requires a verified draw-context containing Shader and Material sourceFile+PathID plus a shared RenderDoc event identity"
            }
        };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, report.ToJsonString(JsonOptions));

        Console.WriteLine($"验证: {status}");
        foreach (var check in checks.OfType<JsonObject>())
            Console.WriteLine($"  {check["name"]}: {check["status"]}");
        Console.WriteLine($"已生成: {outputPath}");
        return status switch
        {
            "verified" => 0,
            "invalid" => 1,
            _ => 2
        };
    }

    private static JsonObject VerifyAsIndex(JsonObject root)
    {
        var reasons = new JsonArray();
        var schema = Text(root, "schema");
        var domain = Text(root, "identityDomain");
        if (schema != "anime-studio.asset-index.v1")
            reasons.Add($"schema 不兼容: {schema ?? "<missing>"}");
        if (domain != "serialized-asset:sourceFile+pathId")
            reasons.Add($"identityDomain 不兼容: {domain ?? "<missing>"}");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var identityErrors = 0;
        foreach (var asset in Objects(root["assets"]))
        {
            var identity = asset["identity"] as JsonObject;
            var sourceFile = Text(identity, "sourceFile");
            var pathId = ScalarText(identity?["pathId"]);
            var key = Text(identity, "key");
            var identityDomain = Text(identity, "domain");
            if (string.IsNullOrWhiteSpace(sourceFile)
                || string.IsNullOrWhiteSpace(pathId)
                || key != $"{sourceFile}#{pathId}"
                || identityDomain != "serialized-asset:sourceFile+pathId")
            {
                identityErrors++;
                continue;
            }
            if (!keys.Add(key))
                identityErrors++;
        }

        var bindingErrors = ValidateBindings(root, keys);
        var validation = root["validation"] as JsonObject;
        var pathValidation = root["pathValidation"] as JsonObject;
        if (Text(validation, "status") != "valid")
            reasons.Add("AS index 自身 validation 不是 valid");
        if (Text(pathValidation, "status") != "valid")
            reasons.Add("AS pathValidation 不是 valid");
        if (identityErrors > 0)
            reasons.Add($"发现 {identityErrors} 个不完整或重复的 asset identity");
        if (bindingErrors > 0)
            reasons.Add($"发现 {bindingErrors} 个指向不存在资产的 Material 绑定");

        var status = reasons.Count == 0 ? "verified" : "invalid";
        return new JsonObject
        {
            ["name"] = "asIdentityAndPaths",
            ["status"] = status,
            ["assetCount"] = keys.Count,
            ["referenceCount"] = Count(root, "references"),
            ["pathCount"] = Number(pathValidation, "pathCount"),
            ["absolutePathCount"] = Number(pathValidation, "absolutePathCount"),
            ["traversalCount"] = Number(pathValidation, "traversalCount"),
            ["identityErrorCount"] = identityErrors,
            ["bindingErrorCount"] = bindingErrors,
            ["reasons"] = reasons
        };
    }

    private static JsonObject VerifyMaterialBindings(JsonObject root)
    {
        var reasons = new JsonArray();
        var keys = new HashSet<string>(
            Objects(root["assets"])
                .Select(asset => Text(asset["identity"] as JsonObject, "key"))
                .Where(key => !string.IsNullOrWhiteSpace(key))!,
            StringComparer.Ordinal);
        var types = Objects(root["assets"])
            .Select(asset => new
            {
                Key = Text(asset["identity"] as JsonObject, "key"),
                Type = Text(asset, "type")
            })
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Type, StringComparer.Ordinal);
        var shaderBindings = Objects(root["materialShaderBindings"]).ToArray();
        var textureBindings = Objects(root["materialTextureBindings"]).ToArray();
        var shaderErrors = CountMissingBindingEndpoints(shaderBindings, keys);
        var textureErrors = CountMissingBindingEndpoints(textureBindings, keys);
        var typeErrors = 0;
        foreach (var binding in shaderBindings)
        {
            if (!HasType(types, Text(binding, "material"), "Material")
                || !HasType(types, Text(binding, "asset"), "Shader")
                || string.IsNullOrWhiteSpace(Text(binding, "evidence")))
                typeErrors++;
        }
        foreach (var binding in textureBindings)
        {
            if (!HasType(types, Text(binding, "material"), "Material")
                || !HasTextureType(types, Text(binding, "asset"))
                || string.IsNullOrWhiteSpace(Text(binding, "property"))
                || string.IsNullOrWhiteSpace(Text(binding, "evidence")))
                typeErrors++;
        }
        if (shaderBindings.Length == 0)
            reasons.Add("没有 Material→Shader 绑定");
        if (textureBindings.Length == 0)
            reasons.Add("没有 Material→Texture 绑定");
        if (shaderErrors > 0)
            reasons.Add($"Material→Shader 绑定端点错误 {shaderErrors}");
        if (textureErrors > 0)
            reasons.Add($"Material→Texture 绑定端点错误 {textureErrors}");
        if (typeErrors > 0)
            reasons.Add($"Material 绑定类型或证据字段错误 {typeErrors}");
        var status = reasons.Count == 0 ? "verified" : "invalid";
        return new JsonObject
        {
            ["name"] = "materialShaderTextureBindings",
            ["status"] = status,
            ["materialShaderBindingCount"] = shaderBindings.Length,
            ["materialTextureBindingCount"] = textureBindings.Length,
            ["bindingErrorCount"] = shaderErrors + textureErrors,
            ["bindingTypeOrEvidenceErrorCount"] = typeErrors,
            ["reasons"] = reasons,
            ["proof"] = "AnimeStudio serialized Material references and SourceCAB#PathID endpoints"
        };
    }

    private static JsonObject VerifyTypeTree(
        JsonObject report,
        Dictionary<string, List<string>> options,
        JsonObject? profile)
    {
        var reasons = new JsonArray();
        var critical = Values(options, "critical-type");
        if (critical.Count == 0)
            critical = DefaultCriticalTypes.ToList();
        var coverage = report["coverage"] as JsonObject;
        var coverageItems = Objects(coverage?["types"]).ToArray();
        // A runtime dump may intentionally omit the RTTI catalog and still
        // contain a complete serialized tree in `trees`. In that mode there
        // is no catalog coverage table to consult; use only the tree's own
        // decoded status and node count, never infer fields from a type name.
        var directTreeItems = Objects(report["trees"]).ToArray();
        var coverageSource = coverageItems.Length > 0
            ? coverageItems
            : directTreeItems.Select(tree => new JsonObject
            {
                ["typeName"] = tree["typeName"]?.DeepClone(),
                ["status"] = tree["status"]?.DeepClone(),
                ["nodeCount"] = tree["nodeCount"]?.DeepClone()
            }).ToArray<JsonObject>();
        var duplicateCoverageCount = coverageItems
            .GroupBy(item => Text(item, "typeName") ?? "<missing>", StringComparer.Ordinal)
            .Count(group => group.Count() > 1);
        var coverageTypes = coverageSource
            .GroupBy(item => Text(item, "typeName") ?? "<missing>", StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        var missingCritical = critical.Where(type =>
            !coverageTypes.TryGetValue(type, out var value)
            || Text(value, "status") != "decoded"
            || Number(value, "nodeCount") <= 0).ToArray();
        var fieldStatus = Text(report["fieldIntegrity"] as JsonObject, "status")
            ?? (directTreeItems.Length > 0
                && directTreeItems.All(tree =>
                    Text(tree["fieldIntegrity"] as JsonObject, "status") == "valid")
                ? "valid"
                : null);
        var compatibilityStatus = Text(report["compatibility"] as JsonObject, "status");
        if (Text(report, "schema") != "my-hook.typetree-report.v1")
            reasons.Add("TypeTree report schema 不兼容");
        if (compatibilityStatus is not null && compatibilityStatus != "compatible")
            reasons.Add($"TypeTree 版本不兼容: {compatibilityStatus}");
        if (fieldStatus != "valid")
            reasons.Add($"fieldIntegrity={fieldStatus ?? "<missing>"}");
        if (missingCritical.Length > 0)
            reasons.Add($"关键类型未完整解码: {string.Join(", ", missingCritical)}");
        if (duplicateCoverageCount > 0)
            reasons.Add($"TypeTree coverage 存在重复类型 {duplicateCoverageCount} 个");

        var requireFullCatalog = options.ContainsKey("require-full-catalog");
        var catalogRequested = Number(coverage, "requestedTypeCount");
        var catalogDecoded = Number(coverage, "decodedTypeCount");
        var directTreeMode = coverageItems.Length == 0 && directTreeItems.Length > 0;
        var catalogComplete = catalogRequested > 0 && catalogRequested == catalogDecoded;
        if (requireFullCatalog && !catalogComplete)
            reasons.Add($"完整 catalog 未通过: {catalogDecoded}/{catalogRequested}");

        var status = reasons.Count == 0 ? "verified" :
            missingCritical.Length == 0 && fieldStatus == "valid" ? "partial" : "invalid";
        var result = new JsonObject
        {
            ["name"] = "typeTreeFields",
            ["status"] = status,
            ["criticalTypeCount"] = critical.Count,
            ["decodedCriticalTypeCount"] = critical.Count - missingCritical.Length,
            ["fieldIntegrity"] = fieldStatus,
            ["catalogRequestedTypeCount"] = catalogRequested,
            ["catalogDecodedTypeCount"] = catalogDecoded,
            ["catalogComplete"] = catalogComplete,
            ["evidenceMode"] = directTreeMode ? "serialized-trees" : "catalog-coverage",
            ["serializedTreeCount"] = directTreeItems.Length,
            ["duplicateCoverageTypeCount"] = duplicateCoverageCount,
            ["refTypeHashCoverage"] = Number(report["fieldIntegrity"] as JsonObject, "refTypeHashCoverage"),
            ["reasons"] = reasons
        };
        if (profile?["typeTreeCompatibility"] is JsonObject expected
            && report["compatibility"] is JsonObject actual)
        {
            result["compatibility"] = new JsonObject
            {
                ["expectedRevision"] = Text(expected, "revision"),
                ["expectedVariant"] = Text(expected, "variant"),
                ["status"] = Text(actual, "status")
            };
        }
        return result;
    }

    private static JsonObject VerifyRuntimeBridge(JsonObject? analysis, JsonObject? profile)
    {
        var reasons = new JsonArray();
        if (analysis is null)
        {
            return new JsonObject
            {
                ["name"] = "runtimeIdentityBridge",
                ["status"] = "blocked",
                ["reasons"] = new JsonArray("未提供 --analysis；不能把离线 AS/TypeTree 证据当作运行时桥")
            };
        }

        var bridge = analysis["identityBridge"] as JsonObject;
        var bridgeStatus = Text(bridge, "status");
        if (bridgeStatus != "verified")
            reasons.Add($"identityBridge={bridgeStatus ?? "<missing>"}");
        var liveTypeTree = analysis["decodedTypeTree"] as JsonObject;
        var liveTypeTreeStatus = Text(liveTypeTree, "status");
        if (liveTypeTreeStatus != "decoded")
            reasons.Add($"客体实时 TypeTree={liveTypeTreeStatus ?? "<missing>"}");
        var runtimeCatalogStatus = Text(analysis["runtimeCatalog"] as JsonObject, "status");
        if (runtimeCatalogStatus != "verified_fields")
            reasons.Add($"运行时 TypeTree catalog={runtimeCatalogStatus ?? "<missing>"}");
        var observedClient = Text(analysis["runtimeCatalog"] as JsonObject, "packageVersion");
        var targetVersion = Text(profile, "version");
        if (!Version.TryParse(observedClient, out var actualVersion)
            || !Version.TryParse(targetVersion, out var expectedVersion)
            || actualVersion.Major != expectedVersion.Major
            || actualVersion.Minor != expectedVersion.Minor)
            reasons.Add($"会话客体版本 {observedClient ?? "<missing>"} 与 Profile 目标版本 {targetVersion ?? "<missing>"} 不匹配");

        return new JsonObject
        {
            ["name"] = "runtimeIdentityBridge",
            ["status"] = reasons.Count == 0 ? "verified" : "blocked",
            ["bridge"] = bridge?.DeepClone(),
            ["liveTypeTreeStatus"] = liveTypeTreeStatus,
            ["runtimeCatalogStatus"] = runtimeCatalogStatus,
            ["reasons"] = reasons,
            ["proof"] = "verified unity.draw-context with Shader/Material sourceFile+PathID and shared RenderDoc event identity"
        };
    }

    private static JsonObject VerifyHookEmbedding(JsonObject hook, string hookPath, string externalAsIndexPath)
    {
        var reasons = new JsonArray();
        var embedded = Text(hook["as"] as JsonObject, "assetIndex");
        if (string.IsNullOrWhiteSpace(embedded))
            reasons.Add(".hook 没有嵌入 AS asset index");
        else
        {
            var hookDir = Path.GetDirectoryName(hookPath)!;
            var embeddedPath = Path.GetFullPath(Path.Combine(hookDir, embedded.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(embeddedPath))
                reasons.Add($".hook 引用的 AS index 不存在: {embedded}");
            else
            {
                var embeddedRoot = ReadObject(embeddedPath, "embedded AS index");
                if (Text(embeddedRoot, "schema") != "anime-studio.asset-index.v1")
                    reasons.Add(".hook 内嵌 AS index schema 不兼容");
                else if (!string.Equals(
                    SemanticIndexFingerprint(embeddedRoot),
                    SemanticIndexFingerprint(ReadObject(externalAsIndexPath, "external AS index")),
                    StringComparison.Ordinal))
                    reasons.Add(".hook 内嵌 AS index 的身份、引用或绑定证据与验收输入不一致");
            }
        }
        return new JsonObject
        {
            ["name"] = "hookEmbedding",
            ["status"] = reasons.Count == 0 ? "verified" : "invalid",
            ["embeddedAssetIndex"] = embedded,
            ["reasons"] = reasons
        };
    }

    private static string OverallStatus(JsonArray checks)
    {
        if (checks.OfType<JsonObject>().Any(check => Text(check, "status") == "invalid"))
            return "invalid";
        if (checks.OfType<JsonObject>().Any(check => Text(check, "status") == "blocked"))
            return "blocked";
        if (checks.OfType<JsonObject>().Any(check => Text(check, "status") is "partial" or "not_evaluated"))
            return "partial";
        return "verified";
    }

    private static int CountMissingBindingEndpoints(IEnumerable<JsonObject> bindings, HashSet<string> keys)
    {
        var count = 0;
        foreach (var binding in bindings)
        {
            var material = Text(binding, "material");
            var asset = Text(binding, "asset");
            if (material is null || !keys.Contains(material)) count++;
            if (asset is null || !keys.Contains(asset)) count++;
        }
        return count;
    }

    private static bool HasType(Dictionary<string, string?> types, string? key, string expected) =>
        key is not null && types.TryGetValue(key, out var actual)
        && string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool HasTextureType(Dictionary<string, string?> types, string? key) =>
        key is not null && types.TryGetValue(key, out var actual)
        && actual is "Texture" or "Texture2D" or "Texture2DArray" or "Texture3D" or "Cubemap" or "CubemapArray";

    private static int ValidateBindings(JsonObject root, HashSet<string> keys) =>
        CountMissingBindingEndpoints(Objects(root["materialShaderBindings"]), keys)
        + CountMissingBindingEndpoints(Objects(root["materialTextureBindings"]), keys);

    private static JsonObject FileInfo(string path)
    {
        using var stream = File.OpenRead(path);
        return new JsonObject
        {
            ["path"] = path,
            ["size"] = new FileInfo(path).Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()
        };
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SemanticIndexFingerprint(JsonObject root)
    {
        var selected = new JsonObject
        {
            ["schema"] = root["schema"]?.DeepClone(),
            ["identityDomain"] = root["identityDomain"]?.DeepClone(),
            ["assets"] = root["assets"]?.DeepClone() ?? new JsonArray(),
            ["references"] = root["references"]?.DeepClone() ?? new JsonArray(),
            ["materialShaderBindings"] = root["materialShaderBindings"]?.DeepClone() ?? new JsonArray(),
            ["materialTextureBindings"] = root["materialTextureBindings"]?.DeepClone() ?? new JsonArray(),
            ["pathValidation"] = root["pathValidation"]?.DeepClone()
        };
        return selected.ToJsonString(JsonOptions);
    }

    private static JsonObject ReadObject(string path, string label) =>
        JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidDataException($"{label} 不是 JSON 对象: {path}");

    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array
        ? array.OfType<JsonObject>()
        : Enumerable.Empty<JsonObject>();

    private static string? Text(JsonObject? node, string key)
    {
        if (node?[key] is not JsonValue value) return null;
        return value.TryGetValue<string>(out var text) ? text : value.ToJsonString();
    }

    private static string? ScalarText(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        return value.ToJsonString();
    }

    private static int Number(JsonObject? node, string key)
    {
        if (node?[key] is not JsonValue value) return 0;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<long>(out var longValue)) return checked((int)longValue);
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int Count(JsonObject node, string key) => node[key] is JsonArray array ? array.Count : 0;

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"verify 不接受位置参数: {args[i]}");
            var key = args[i][2..];
            var value = "true";
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                value = args[++i];
            if (!result.TryGetValue(key, out var values)) result[key] = values = [];
            values.Add(value);
        }
        return result;
    }

    private static string RequiredValue(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values) && values.Count > 0
            ? values[^1]
            : throw new ArgumentException($"缺少 --{key}");

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

    private static List<string> Values(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values)
            ? values.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.Ordinal).ToList()
            : [];
}
