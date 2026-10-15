using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace MyHookTool;

internal static class AsIndexCommand
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    public static int Run(string[] args)
    {
        var options = ParseOptions(args);
        var positionals = Positionals(options);
        if (positionals.Count != 1)
            throw new ArgumentException("as-index 需要一个 AnimeStudio manifest 文件或目录");

        var input = Path.GetFullPath(positionals[0]);
        if (!File.Exists(input) && !Directory.Exists(input))
            throw new FileNotFoundException("找不到 AS 输入", input);

        var manifestPaths = FindManifests(input);
        if (manifestPaths.Count == 0)
            throw new InvalidDataException("输入中没有找到包含 dependency-manifest/Nodes 的 AS manifest.json");

        var index = new AssetIndex(input);
        foreach (var manifestPath in manifestPaths)
            index.ReadManifest(manifestPath);

        foreach (var path in Values(options, "shader-report"))
            index.ReadShaderReport(RequiredFile(path, "shader-report"));
        foreach (var path in Values(options, "as-bindings"))
            index.ReadBindings(RequiredFile(path, "as-bindings"));

        var output = options.TryGetValue("output", out var outputs) && outputs.Count > 0
            ? Path.GetFullPath(outputs[^1])
            : Path.Combine(Directory.Exists(input) ? input : Path.GetDirectoryName(input)!,
                "as-asset-index.json");
        if (File.Exists(output) && !options.ContainsKey("force"))
            throw new IOException($"输出文件已存在；如需覆盖请显式指定 --force: {output}");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var result = index.ToJson(output);
        File.WriteAllText(output, result.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        }));
        Console.WriteLine($"已生成 AS 索引: {output}");
        Console.WriteLine($"manifest={manifestPaths.Count}, assets={index.AssetCount}, references={index.ReferenceCount}, materialShaderBindings={index.MaterialShaderBindingCount}");
        return 0;
    }

    private static List<string> FindManifests(string input)
    {
        var candidates = File.Exists(input)
            ? new[] { input }
            : Directory.EnumerateFiles(input, "manifest.json", SearchOption.AllDirectories);
        var result = new List<string>();
        foreach (var path in candidates)
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(path), NodeOptions)?.AsObject();
                if (root is null || !string.Equals(StringValue(root, "Status"), "dependency-manifest", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (root["Nodes"] is not JsonArray)
                    continue;
                result.Add(Path.GetFullPath(path));
            }
            catch (JsonException)
            {
                // A non-AS manifest in the input directory is not an index input.
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(args[index]);
                continue;
            }
            var key = args[index][2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "1";
            if (!result.TryGetValue(key, out var values))
                result[key] = values = [];
            values.Add(value);
        }
        result["__positionals"] = positionals;
        return result;
    }

    private static List<string> Positionals(Dictionary<string, List<string>> options) =>
        options.TryGetValue("__positionals", out var values) ? values : [];

    private static IEnumerable<string> Values(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values) ? values : [];

    private static string RequiredFile(string path, string option)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"找不到 --{option}", fullPath);
        return fullPath;
    }

    private static string? StringValue(JsonObject? node, string key)
    {
        if (node?[key] is not JsonValue value)
            return null;
        if (value.TryGetValue<string>(out var text)) return text;
        return value.ToString();
    }

    private static string? ScalarText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToString()
    };

    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array
        ? array.OfType<JsonObject>()
        : Enumerable.Empty<JsonObject>();

    private sealed class AssetIndex
    {
        private readonly string input;
        private readonly string baseDirectory;
        private readonly Dictionary<string, JsonObject> assets = new(StringComparer.Ordinal);
        private readonly HashSet<string> referenceKeys = new(StringComparer.Ordinal);
        private readonly JsonArray references = [];
        private readonly JsonArray materialShaderBindings = [];
        private readonly JsonArray materialTextureBindings = [];
        private readonly JsonArray diagnostics = [];
        private readonly JsonArray pathDiagnostics = [];
        private readonly List<string> manifests = [];
        private int pathCount;
        private int absolutePathCount;

        public int AssetCount => assets.Count;
        public int ReferenceCount => references.Count;
        public int MaterialShaderBindingCount => materialShaderBindings.Count;

        public AssetIndex(string input)
        {
            this.input = input;
            baseDirectory = Directory.Exists(input)
                ? input
                : Path.GetDirectoryName(input)!;
        }

        public void ReadManifest(string path)
        {
            var root = JsonNode.Parse(File.ReadAllText(path), NodeOptions)?.AsObject()
                ?? throw new InvalidDataException($"AS manifest 不是 JSON 对象: {path}");
            manifests.Add(path);
            var sourceCab = StringValue(root, "SourceCAB");
            var sourceBlock = StringValue(root, "SourceBlock");
            var rootName = StringValue(root, "Name") ?? Path.GetFileNameWithoutExtension(path);
            var rootPathId = ScalarText(root["RootPathID"]);
            if (sourceCab is not null && rootPathId is not null)
                AddAsset("GameObject", rootName, rootPathId, sourceCab, rootName, sourceBlock, path, null);

            foreach (var node in Objects(root["Nodes"]))
                ReadNode(node, sourceCab, sourceBlock, path, null);
        }

        private void ReadNode(JsonObject node, string? inheritedCab, string? sourceBlock,
            string manifestPath, string? parentPath)
        {
            var name = StringValue(node, "Name") ?? "<unnamed>";
            var relativePath = StringValue(node, "Path") ??
                (parentPath is null ? name : parentPath + "/" + name);
            var sourceCab = StringValue(node, "SourceCAB") ?? inheritedCab;
            var pathId = ScalarText(node["PathID"]);
            var gameObjectKey = pathId is not null && sourceCab is not null
                ? AddAsset("GameObject", name, pathId, sourceCab, relativePath, sourceBlock, manifestPath, null)
                : null;

            foreach (var component in Objects(node["Components"]))
            {
                var componentType = StringValue(component, "Type") ?? "Component";
                var componentCab = StringValue(component, "SourceCAB") ?? sourceCab;
                var componentPathId = ScalarText(component["PathID"]);
                var parametersFile = StringValue(component, "ParametersFile");
                var componentKey = componentPathId is not null && componentCab is not null
                    ? AddAsset(componentType, componentType, componentPathId, componentCab,
                        relativePath, sourceBlock, manifestPath, parametersFile)
                    : null;

                if (componentKey is not null && gameObjectKey is not null)
                    AddReference(componentKey, gameObjectKey, "m_GameObject", componentType, null, null, relativePath, null);

                foreach (var reference in Objects(component["References"]))
                    AddReferenceFromJson(componentKey, reference, componentType, relativePath);
            }

            foreach (var child in Objects(node["Children"]))
                ReadNode(child, sourceCab, sourceBlock, manifestPath, relativePath);
            foreach (var child in Objects(node["Nodes"]))
                ReadNode(child, sourceCab, sourceBlock, manifestPath, relativePath);
        }

        private void AddReferenceFromJson(string? sourceKey, JsonObject reference,
            string sourceType, string sourcePath)
        {
            if (sourceKey is null)
                return;
            var targetCab = StringValue(reference, "SourceCAB");
            var targetPathId = ScalarText(reference["PathID"]);
            if (targetCab is null || targetPathId is null)
            {
                diagnostics.Add(new JsonObject
                {
                    ["code"] = "reference_identity_incomplete",
                    ["source"] = sourceKey,
                    ["sourceType"] = sourceType,
                    ["path"] = sourcePath,
                    ["reference"] = reference.DeepClone()
                });
                return;
            }

            var targetType = StringValue(reference, "Type") ?? "Object";
            var targetName = StringValue(reference, "Name");
            var targetKey = AddAsset(targetType, targetName, targetPathId, targetCab,
                null, null, null, null);
            AddUsagePath(targetKey, sourcePath);
            AddReference(sourceKey, targetKey, null, sourceType, targetName, targetType, sourcePath, reference);
        }

        private string AddAsset(string type, string? name, string pathId, string sourceFile,
            string? path, string? sourceBlock, string? manifestPath, string? parametersFile)
        {
            var key = IdentityKey(sourceFile, pathId);
            if (!assets.TryGetValue(key, out var asset))
            {
                asset = new JsonObject
                {
                    ["identity"] = new JsonObject
                    {
                        ["domain"] = "serialized-asset:sourceFile+pathId",
                        ["key"] = key,
                        ["sourceFile"] = sourceFile,
                        ["pathId"] = ParseScalar(pathId)
                    },
                    ["type"] = type,
                    ["name"] = name,
                    ["paths"] = new JsonArray(),
                    ["usagePaths"] = new JsonArray(),
                    ["sourceBlocks"] = new JsonArray(),
                    ["provenance"] = new JsonArray(),
                    ["pathEvidence"] = "paths are asset paths when present; usagePaths are GameObject relative paths from referencing AS nodes"
                };
                assets.Add(key, asset);
            }
            else if (!string.Equals(StringValue(asset, "type"), type, StringComparison.Ordinal)
                && !string.Equals(StringValue(asset, "type"), "Object", StringComparison.Ordinal))
            {
                diagnostics.Add(new JsonObject
                {
                    ["code"] = "identity_type_conflict",
                    ["identityKey"] = key,
                    ["firstType"] = asset["type"]?.DeepClone(),
                    ["nextType"] = type
                });
            }

            if (asset["name"] is null && name is not null)
                asset["name"] = name;
            AddUniqueString(asset["paths"]!.AsArray(), path);
            ValidateAssetPath(path, key, "paths");
            AddUniqueString(asset["sourceBlocks"]!.AsArray(), sourceBlock);
            if (manifestPath is not null || parametersFile is not null)
            {
                var provenance = new JsonObject
                {
                    ["manifest"] = RelativePath(manifestPath),
                    ["parametersFile"] = parametersFile
                };
                AddUniqueObject(asset["provenance"]!.AsArray(), provenance);
            }
            return key;
        }

        private void AddUsagePath(string assetKey, string? usagePath)
        {
            if (!assets.TryGetValue(assetKey, out var asset) || string.IsNullOrWhiteSpace(usagePath))
                return;
            AddUniqueString(asset["usagePaths"]!.AsArray(), usagePath);
            ValidateAssetPath(usagePath, assetKey, "usagePaths");
        }

        private void ValidateAssetPath(string? path, string assetKey, string field)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            pathCount++;
            var normalized = path.Replace('\\', '/');
            if (Path.IsPathRooted(path) || normalized.StartsWith("/", StringComparison.Ordinal))
                absolutePathCount++;

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (!segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
                return;

            pathDiagnostics.Add(new JsonObject
            {
                ["code"] = "path_traversal",
                ["asset"] = assetKey,
                ["field"] = field,
                ["path"] = path,
                ["reason"] = "AS path contains a parent-directory segment; it is not a safe portable relative path"
            });
        }

        private void AddReference(string sourceKey, string targetKey, string? property,
            string sourceType, string? targetName, string? targetType, string? sourcePath, JsonObject? raw)
        {
            var unique = string.Join("|", sourceKey, targetKey, property ?? "", targetType ?? "");
            if (!referenceKeys.Add(unique))
                return;
            references.Add(new JsonObject
            {
                ["source"] = sourceKey,
                ["target"] = targetKey,
                ["property"] = property,
                ["sourceType"] = sourceType,
                ["sourcePath"] = sourcePath,
                ["targetType"] = targetType,
                ["targetName"] = targetName,
                ["evidence"] = "AnimeStudio dependency manifest serialized reference",
                ["raw"] = raw?.DeepClone()
            });
        }

        public void ReadShaderReport(string path)
        {
            var root = JsonNode.Parse(File.ReadAllText(path), NodeOptions)?.AsObject()
                ?? throw new InvalidDataException($"Shader report 不是 JSON 对象: {path}");
            foreach (var shader in Objects(root["shaders"]))
            {
                var source = shader["source"]?.AsObject();
                var sourceFile = StringValue(source, "fileName");
                var pathId = ScalarText(shader["pathId"]);
                if (sourceFile is not null && pathId is not null)
                    AddAsset("Shader", StringValue(shader, "name"), pathId, sourceFile,
                        StringValue(shader, "path"), null, path, null);
            }
        }

        public void ReadBindings(string path)
        {
            var root = JsonNode.Parse(File.ReadAllText(path), NodeOptions)?.AsObject()
                ?? throw new InvalidDataException($"AS bindings 不是 JSON 对象: {path}");
            foreach (var material in Objects(root["materials"]))
            {
                var materialSource = material["source"]?.AsObject();
                var materialFile = StringValue(materialSource, "fileName");
                var materialPathId = ScalarText(material["pathId"]);
                if (materialFile is null || materialPathId is null)
                    continue;
                var materialKey = AddAsset("Material", StringValue(material, "name"), materialPathId,
                    materialFile, StringValue(material, "path"), null, path, null);

                var shader = material["shader"]?.AsObject();
                var shaderFile = StringValue(shader, "sourceFile");
                var shaderPathId = ScalarText(shader?["pathId"]);
                if (shaderFile is not null && shaderPathId is not null)
                {
                    var shaderKey = AddAsset("Shader", StringValue(shader, "resolvedName"), shaderPathId,
                        shaderFile, null, null, path, null);
                    AddUniqueBinding(materialShaderBindings, materialKey, shaderKey,
                        StringValue(material, "name"), StringValue(shader, "resolvedName"), path);
                }

                foreach (var textureEntry in Objects(material["textures"]))
                {
                    var texture = textureEntry["texture"]?.AsObject();
                    var textureFile = StringValue(texture, "sourceFile");
                    var texturePathId = ScalarText(texture?["pathId"]);
                    if (textureFile is null || texturePathId is null)
                        continue;
                    var textureKey = AddAsset("Texture2D", StringValue(texture, "resolvedName"), texturePathId,
                        textureFile, null, null, path, null);
                    AddUniqueBinding(materialTextureBindings, materialKey, textureKey,
                        StringValue(textureEntry, "property"), path);
                }
            }
        }

        private void AddUniqueBinding(JsonArray target, string materialKey, string assetKey,
            string? materialName, string? assetName, string evidence)
        {
            var exists = target.OfType<JsonObject>().Any(item =>
                string.Equals(StringValue(item, "material"), materialKey, StringComparison.Ordinal)
                && string.Equals(StringValue(item, "asset"), assetKey, StringComparison.Ordinal));
            if (exists) return;
            target.Add(new JsonObject
            {
                ["material"] = materialKey,
                ["asset"] = assetKey,
                ["materialName"] = materialName,
                ["assetName"] = assetName,
                ["evidence"] = evidence
            });
        }

        private void AddUniqueBinding(JsonArray target, string materialKey, string assetKey,
            string? property, string evidence)
        {
            var exists = target.OfType<JsonObject>().Any(item =>
                string.Equals(StringValue(item, "material"), materialKey, StringComparison.Ordinal)
                && string.Equals(StringValue(item, "asset"), assetKey, StringComparison.Ordinal)
                && string.Equals(StringValue(item, "property"), property, StringComparison.Ordinal));
            if (exists) return;
            target.Add(new JsonObject
            {
                ["material"] = materialKey,
                ["asset"] = assetKey,
                ["property"] = property,
                ["evidence"] = evidence
            });
        }

        public JsonObject ToJson(string output)
        {
            var sortedAssets = new JsonArray();
            foreach (var asset in assets.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
                sortedAssets.Add(asset);
            var sourceFiles = new JsonArray();
            foreach (var path in manifests.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                sourceFiles.Add(new JsonObject
                {
                    ["path"] = RelativePath(path),
                    ["sha256"] = Sha256(path)
                });
            }
            return new JsonObject
            {
                ["schema"] = "anime-studio.asset-index.v1",
                ["identityDomain"] = "serialized-asset:sourceFile+pathId",
                ["identityKeyFormat"] = "<sourceFile>#<pathId>",
                ["input"] = new JsonObject
                {
                    ["path"] = input,
                    ["baseDirectory"] = baseDirectory,
                    ["manifestCount"] = manifests.Count
                },
                ["sources"] = sourceFiles,
                ["assets"] = sortedAssets,
                ["references"] = references,
                ["materialShaderBindings"] = materialShaderBindings,
                ["materialTextureBindings"] = materialTextureBindings,
                ["validation"] = BuildValidation(),
                ["pathValidation"] = BuildPathValidation(),
                ["diagnostics"] = diagnostics,
                ["summary"] = new JsonObject
                {
                    ["assetCount"] = assets.Count,
                    ["referenceCount"] = references.Count,
                    ["materialShaderBindingCount"] = materialShaderBindings.Count,
                    ["materialTextureBindingCount"] = materialTextureBindings.Count,
                    ["diagnosticCount"] = diagnostics.Count,
                    ["pathEvidence"] = "GameObject relative paths come from AnimeStudio manifest Nodes; identity comes only from SourceCAB+PathID",
                    ["crossSourcePolicy"] = "This index does not infer RenderDoc ResourceId or runtime native addresses"
                },
                ["output"] = new JsonObject
                {
                    ["path"] = output
                }
            };
        }

        private JsonObject BuildValidation()
        {
            var errors = new JsonArray();
            ValidateBindingTargets(materialShaderBindings, errors, "material_shader");
            ValidateBindingTargets(materialTextureBindings, errors, "material_texture");
            var bindingErrorCount = errors.Count;
            foreach (var diagnostic in pathDiagnostics)
            {
                if (diagnostic is not null)
                    errors.Add(diagnostic.DeepClone());
            }
            var pathErrorCount = errors.Count - bindingErrorCount;
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic is not null)
                    errors.Add(diagnostic.DeepClone());
            }
            return new JsonObject
            {
                ["status"] = errors.Count == 0 ? "valid" : "invalid",
                ["assetKeyCount"] = assets.Count,
                ["bindingErrorCount"] = bindingErrorCount,
                ["pathErrorCount"] = pathErrorCount,
                ["diagnosticErrorCount"] = diagnostics.Count,
                ["validationErrorCount"] = errors.Count,
                ["errors"] = errors
            };
        }

        private JsonObject BuildPathValidation()
        {
            return new JsonObject
            {
                ["status"] = pathDiagnostics.Count == 0 ? "valid" : "invalid",
                ["pathCount"] = pathCount,
                ["absolutePathCount"] = absolutePathCount,
                ["relativePathCount"] = Math.Max(0, pathCount - absolutePathCount),
                ["traversalCount"] = pathDiagnostics.Count,
                ["policy"] = "paths and usagePaths are preserved as evidence; absolute paths are reported, not silently rewritten",
                ["errors"] = pathDiagnostics.DeepClone()
            };
        }

        private void ValidateBindingTargets(JsonArray bindings, JsonArray errors, string kind)
        {
            foreach (var binding in bindings.OfType<JsonObject>())
            {
                var material = StringValue(binding, "material");
                var asset = StringValue(binding, "asset");
                if (material is null || !assets.ContainsKey(material))
                    errors.Add(new JsonObject
                    {
                        ["kind"] = kind,
                        ["missing"] = "material",
                        ["identityKey"] = material
                    });
                if (asset is null || !assets.ContainsKey(asset))
                    errors.Add(new JsonObject
                    {
                        ["kind"] = kind,
                        ["missing"] = "asset",
                        ["identityKey"] = asset
                    });
            }
        }

        private string? RelativePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetRelativePath(baseDirectory, path).Replace('\\', '/'); }
            catch { return path; }
        }

        private static string IdentityKey(string sourceFile, string pathId) => $"{sourceFile}#{pathId}";

        private static JsonNode ParseScalar(string text)
        {
            if (long.TryParse(text, out var signed)) return signed;
            if (ulong.TryParse(text, out var unsigned)) return unsigned;
            return text;
        }

        private static void AddUniqueString(JsonArray array, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!array.OfType<JsonValue>().Any(item => StringValue(item) == value))
                array.Add(value);
        }

        private static void AddUniqueObject(JsonArray array, JsonObject value)
        {
            if (!array.OfType<JsonObject>().Any(item => JsonNode.DeepEquals(item, value)))
                array.Add(value);
        }

        private static string? StringValue(JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return text;
            return value.ToString();
        }

        private static string? StringValue(JsonObject? node, string key)
        {
            if (node?[key] is not JsonValue value)
                return null;
            if (value.TryGetValue<string>(out var text)) return text;
            return value.ToString();
        }

        private static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }
}
