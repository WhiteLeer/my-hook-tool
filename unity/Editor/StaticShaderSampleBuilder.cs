using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class StaticShaderSampleBuilder
{
    [Serializable] private sealed class FloatValue { public string name; public float value; }
    [Serializable] private sealed class ColorValue { public string name; public float[] value; }
    [Serializable] private sealed class TagValue { public string name; public string value; }
    [Serializable] private sealed class TextureSlot
    {
        public string property;
        public string kind;
        public string file;
        public bool srgbTexture;
        public int wrapU;
        public int wrapV;
        public int filterMode;
        public float[] scale;
        public float[] offset;
        public int width;
        public int height;
        public string rawSha256;
        public float[] constantColor;
    }
    [Serializable] private sealed class Sample
    {
        public string schema;
        public string name;
        public string shaderPath;
        public string shaderName;
        public string shaderSha256;
        public string texturePath;
        public bool srgbTexture;
        public int textureWrap;
        public float[] textureScale;
        public float[] textureOffset;
        public FloatValue[] floats;
        public ColorValue[] colors;
        public TextureSlot[] textures;
        public string sharedShaderAssetPath;
        public bool hasTimeAdapter;
        public bool hasMaterialState;
        public int renderQueue;
        public bool enableInstancing;
        public bool doubleSidedGI;
        public int lightmapFlags;
        public string[] keywords;
        public string[] disabledShaderPasses;
        public TagValue[] tags;
        public string sourcePassLightMode;
        public bool exactSerializedPassState;
    }

    public static void BuildFromCommandLine()
    {
        Build(Argument("-staticSample"), Argument("-staticSampleOutput"), Argument("-staticSampleReport"));
    }

    public static void Build(string source, string output, string reportPath)
    {
        using (var gate = new Mutex(false, "Local\\MyHookGpuValidation"))
        {
            bool owned;
            try { owned = gate.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("Another GPU validation is running.");
            try { BuildCore(source, output, reportPath); }
            finally { gate.ReleaseMutex(); }
        }
    }

    private static void BuildCore(string source, string output, string reportPath)
    {
        source = Path.GetFullPath(source);
        output = output.Replace('\\', '/').TrimEnd('/');
        reportPath = Path.GetFullPath(reportPath);
        if (EditorApplication.isPlaying || EditorApplication.isCompiling)
            throw new InvalidOperationException("Build samples only while the editor is idle.");
        if (!output.StartsWith("Assets/", StringComparison.Ordinal) || output.Split('/').Contains(".."))
            throw new ArgumentException("Output must remain under this project's Assets directory.");
        // Unity import/scene-save APIs can flush other dirty assets implicitly.
        // Refuse this state rather than silently saving the user's work.
        var dirty = Resources.FindObjectsOfTypeAll<UnityEngine.Object>().Where(x => EditorUtility.IsPersistent(x) && EditorUtility.IsDirty(x))
            .Select(AssetDatabase.GetAssetPath).Distinct().Where(path => path.StartsWith("Assets/",StringComparison.Ordinal)
                && !path.StartsWith(output + "/",StringComparison.Ordinal)).ToArray();
        if (dirty.Length != 0) throw new InvalidOperationException("Unrelated dirty assets must be saved or reverted by the user before building: " + string.Join(", ",dirty));
        Sample sample = JsonUtility.FromJson<Sample>(File.ReadAllText(source));
        if (sample.schema != "my-hook.unity-static-shader-sample.v1" && sample.schema != "my-hook.unity-static-shader-sample.v2")
            throw new InvalidDataException("Unknown sample schema.");
        string shaderPath = string.IsNullOrEmpty(sample.sharedShaderAssetPath) ? output + "/" + sample.shaderPath : sample.sharedShaderAssetPath;
        if (!shaderPath.StartsWith("Assets/", StringComparison.Ordinal) || shaderPath.Split('/').Contains("..")
            || sample.shaderPath != Path.GetFileName(sample.shaderPath))
            throw new InvalidDataException("Shader must remain under Assets with a safe source filename.");
        VerifyShaderBytes(sample,string.IsNullOrEmpty(sample.sharedShaderAssetPath)
            ? Path.Combine(Path.GetDirectoryName(source),sample.shaderPath) : shaderPath);
        TextureSlot[] slots = sample.textures ?? new[] { new TextureSlot { property="_MainTex", kind="serialized_texture",
            file=sample.texturePath, srgbTexture=sample.srgbTexture, wrapU=sample.textureWrap, wrapV=sample.textureWrap,
            filterMode=1, scale=sample.textureScale, offset=sample.textureOffset } };
        Directory.CreateDirectory(output + "/_Preview");
        var files = slots.Where(x => x.kind == "serialized_texture" || x.kind == "serialized_half_texture").Select(x => x.file).Append("sample.json");
        if (string.IsNullOrEmpty(sample.sharedShaderAssetPath)) files = files.Append(sample.shaderPath);
        foreach (string file in files.Distinct())
        {
            if (file != Path.GetFileName(file)) throw new InvalidDataException("Sample file must not traverse directories.");
            string input = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), file));
            string destination = Path.GetFullPath(Path.Combine(output, file));
            if (!string.Equals(input, destination, StringComparison.OrdinalIgnoreCase))
                File.Copy(input, destination, true);
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        foreach (TextureSlot slot in slots.Where(x => x.kind == "serialized_texture"))
        {
            var importer = AssetImporter.GetAtPath(output + "/" + slot.file) as TextureImporter;
            if (importer == null) throw new InvalidDataException("Texture importer was not created.");
            importer.textureType = TextureImporterType.Default;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.sRGBTexture = slot.srgbTexture;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = false;
            importer.filterMode = (FilterMode)slot.filterMode;
            importer.wrapModeU = (TextureWrapMode)slot.wrapU;
            importer.wrapModeV = (TextureWrapMode)slot.wrapV;
            importer.SaveAndReimport();
        }
        VerifyShaderBytes(sample,shaderPath);
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
        if (shader == null) throw new InvalidDataException("Recovered Shader was not imported.");
        if (shader.name != sample.shaderName) throw new InvalidDataException("Shader name disagrees with recovered manifest.");
        Material material = AssetDatabase.LoadAssetAtPath<Material>(output + "/" + sample.name + ".mat");
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, output + "/" + sample.name + ".mat");
        }
        // Rebuild from Shader defaults, not the previous sample's edited values.
        var defaults = new Material(shader) { name = sample.name };
        try { EditorUtility.CopySerialized(defaults, material); }
        finally { UnityEngine.Object.DestroyImmediate(defaults); }
        if (sample.hasMaterialState)
        {
            material.renderQueue=sample.renderQueue;
            material.enableInstancing=sample.enableInstancing;
            material.doubleSidedGI=sample.doubleSidedGI;
            material.globalIlluminationFlags=(MaterialGlobalIlluminationFlags)sample.lightmapFlags;
            material.shaderKeywords=sample.keywords ?? Array.Empty<string>();
            foreach(string pass in sample.disabledShaderPasses ?? Array.Empty<string>()) material.SetShaderPassEnabled(pass,false);
            foreach(var tag in sample.tags ?? Array.Empty<TagValue>()) material.SetOverrideTag(tag.name,tag.value);
        }
        foreach (FloatValue item in sample.floats)
            material.SetFloat(item.name, item.value);
        foreach (ColorValue item in sample.colors)
            material.SetVector(item.name, new Vector4(item.value[0], item.value[1], item.value[2], item.value[3]));
        foreach (TextureSlot slot in slots)
        {
            if (!material.HasProperty(slot.property)) throw new InvalidDataException("Shader has no declared texture property " + slot.property);
            if(slot.kind=="scene_fixture_constant_3d")
            {
                string path=output+"/"+slot.file;
                var texture=AssetDatabase.LoadAssetAtPath<Texture3D>(path);
                if(texture==null)
                {
                    texture=new Texture3D(1,1,1,TextureFormat.RGBA32,false){name="Explicit empty-scene volume",wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
                    var c=slot.constantColor;texture.SetPixels(new[]{new Color(c[0],c[1],c[2],c[3])});texture.Apply();
                    AssetDatabase.CreateAsset(texture,path);
                }
                material.SetTexture(slot.property,texture);
            }
            else material.SetTexture(slot.property, slot.kind == "serialized_texture" || slot.kind=="serialized_half_texture" ? BoundTexture(output,slot) : null);
            material.SetTextureScale(slot.property, new Vector2(slot.scale[0],slot.scale[1]));
            material.SetTextureOffset(slot.property, new Vector2(slot.offset[0],slot.offset[1]));
        }
        material.SetVector("_ClipRect", new Vector4(-10000, -10000, 10000, 10000));
        material.SetFloat("_UI_CLIP_RECT", 0);
        if (sample.hasTimeAdapter) material.SetFloat("_PreviewTime", -1);
        EditorUtility.SetDirty(material);
        if(!string.IsNullOrEmpty(sample.sourcePassLightMode)||sample.exactSerializedPassState)
        {
            AssetDatabase.SaveAssetIfDirty(material);
            string[] errors=ShaderUtil.GetShaderMessages(shader).Where(x=>x.severity.ToString()=="Error").Select(x=>x.message).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
            File.WriteAllText(reportPath,JsonUtility.ToJson(new Validation {status=errors.Length==0?"compiled_material_mesh_render_pending":"failed",shader=shader.name,shaderErrors=errors},true));
            if(errors.Length!=0)throw new InvalidDataException(string.Join("\n",errors));
            return;
        }
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(output + "/PreviewQuad.asset");
        if (mesh == null) { mesh = new Mesh(); AssetDatabase.CreateAsset(mesh, output + "/PreviewQuad.asset"); }
        mesh.Clear();
        mesh.name = "PreviewQuad";
        mesh.vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) };
        mesh.uv = new[] { new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1) };
        mesh.uv2=mesh.uv;
        mesh.colors = Enumerable.Repeat(Color.white, 4).ToArray();
        mesh.triangles = new[] { 0,2,1,0,3,2 };
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        EditorUtility.SetDirty(mesh);
        Scene previousScene = SceneManager.GetActiveScene();
        NewSceneMode mode = Application.isBatchMode && string.IsNullOrEmpty(previousScene.path)
            ? NewSceneMode.Single : NewSceneMode.Additive;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
        SceneManager.SetActiveScene(scene);
        try
        {
        var itemObject = new GameObject(sample.name + " - recovered static material");
        itemObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        itemObject.AddComponent<MeshRenderer>().sharedMaterial = material;
        var camera = new GameObject("Preview Camera").AddComponent<Camera>();
        camera.tag = "MainCamera";
        camera.transform.position = new Vector3(0,0,-4);
        camera.orthographic = true;
        camera.orthographicSize = 1.15f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 20;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.035f, 0.045f, 0.07f, 1);
        camera.allowHDR = false;
        camera.allowMSAA = false;
        int isolatedLayer = Enumerable.Range(8,24).Reverse().FirstOrDefault(layer =>
            !UnityEngine.Object.FindObjectsOfType<Renderer>(true).Any(r => r.gameObject.layer == layer));
        if (isolatedLayer < 8) throw new InvalidOperationException("No unused layer is available for isolated validation.");
        itemObject.layer = isolatedLayer;
        camera.cullingMask = 1 << isolatedLayer;
        AssetDatabase.SaveAssetIfDirty(material);
        AssetDatabase.SaveAssetIfDirty(mesh);
        PrefabUtility.SaveAsPrefabAsset(itemObject, output + "/_Preview/StaticShaderPreview.prefab");
        EditorSceneManager.SaveScene(scene, output + "/_Preview/StaticShaderPreview.unity");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
        float[] times = sample.hasTimeAdapter ? new[] {0f, 1f, 2f} : new[] {0f};
        var frames = new Frame[times.Length];
        Color32[] first = null;
        for (int i = 0; i < times.Length; i++)
        {
            if (sample.hasTimeAdapter) material.SetFloat("_PreviewTime", times[i]);
            string image = Path.Combine(Path.GetDirectoryName(reportPath), "static-preview" + (i==0 ? "" : "-t" + i) + ".png");
            frames[i] = Capture(camera,image,times[i],ref first);
        }
        if (sample.hasTimeAdapter) material.SetFloat("_PreviewTime", -1);
        string[] errors = ShaderUtil.GetShaderMessages(shader).Where(x => x.severity.ToString() == "Error")
            .Select(x => x.message + " at " + x.file + ":" + x.line).ToArray();
        bool valid = errors.Length == 0 && frames.All(x => x.changedPixels>100 && x.magentaPixels==0)
            && (!sample.hasTimeAdapter || frames.Skip(1).Any(x => x.differenceFromFirst>100));
        var report = new Validation { shader = shader.name, graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
            colorSpace = QualitySettings.activeColorSpace.ToString(), shaderErrors = errors,
            changedPixels = frames[0].changedPixels, magentaPixels = frames[0].magentaPixels, preview = frames[0].image,
            scene = output + "/_Preview/StaticShaderPreview.unity", frames = frames,
            isolatedLayer = isolatedLayer,
            status = valid ? "compiled_and_visible_not_game_reference_verified" : "failed" };
        File.WriteAllText(reportPath, JsonUtility.ToJson(report,true));
        if (!valid) throw new InvalidOperationException("Static sample validation failed. See " + reportPath);
        Debug.Log("Static Shader sample is compiled and visible; original game comparison remains pending. " + reportPath);
        }
        finally
        {
            if (sample.hasTimeAdapter && material!=null)
            {
                material.SetFloat("_PreviewTime", -1);
                AssetDatabase.SaveAssetIfDirty(material);
            }
            EditorSceneManager.CloseScene(scene,true);
            if (previousScene.IsValid() && previousScene.isLoaded) SceneManager.SetActiveScene(previousScene);
        }
    }

    private static Frame Capture(Camera camera, string image, float time, ref Color32[] first)
    {
        var target = new RenderTexture(512,512,24,RenderTextureFormat.ARGB32) {name="MyHookStaticShaderSampleCapture"};
        RenderTexture previous = RenderTexture.active;
        RenderTexture previousTarget = camera.targetTexture;
        Texture2D result = null;
        try
        {
        target.Create();
        camera.targetTexture = target;
        camera.Render();
        RenderTexture.active = target;
        result = new Texture2D(512,512,TextureFormat.RGBA32,false);
        result.ReadPixels(new Rect(0,0,512,512),0,0);
        result.Apply();
        File.WriteAllBytes(image, result.EncodeToPNG());
        Color32[] pixels = result.GetPixels32();
        Color32 background = pixels[0];
        int changedPixels = pixels.Count(p => Math.Abs(p.r-background.r)>2 || Math.Abs(p.g-background.g)>2 || Math.Abs(p.b-background.b)>2);
        int magentaPixels = pixels.Count(p => p.r>240 && p.b>240 && p.g<20);
        Color32[] baseline = first;
        int differences = baseline == null ? 0 : Enumerable.Range(0,pixels.Length).Count(i =>
            Math.Abs(pixels[i].r-baseline[i].r)>2 || Math.Abs(pixels[i].g-baseline[i].g)>2 || Math.Abs(pixels[i].b-baseline[i].b)>2);
        if (first == null) first = pixels;
        return new Frame {time=time,image=image,changedPixels=changedPixels,magentaPixels=magentaPixels,differenceFromFirst=differences};
        }
        finally
        {
            RenderTexture.active = previous;
            camera.targetTexture = previousTarget;
            if (result != null) UnityEngine.Object.DestroyImmediate(result);
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
        }
    }

    private static Texture2D BoundTexture(string output, TextureSlot slot)
    {
        string path = output + "/" + slot.file;
        if(slot.kind=="serialized_half_texture")
        {
            byte[] raw=File.ReadAllBytes(path);
            using(var sha=SHA256.Create())
                if(BitConverter.ToString(sha.ComputeHash(raw)).Replace("-","").ToLowerInvariant()!=slot.rawSha256)
                    throw new InvalidDataException("Half-float texture provenance differs.");
            if(raw.Length!=slot.width*slot.height*8)throw new InvalidDataException("RGBAHalf size mismatch.");
            var copy=new Texture2D(slot.width,slot.height,TextureFormat.RGBAHalf,false,true);
            copy.name=Path.GetFileNameWithoutExtension(slot.file);
            copy.LoadRawTextureData(raw);copy.Apply(false,false);
            copy.filterMode=(FilterMode)slot.filterMode;copy.wrapModeU=(TextureWrapMode)slot.wrapU;copy.wrapModeV=(TextureWrapMode)slot.wrapV;
            string halfAssetPath=output+"/"+copy.name+".asset";
            var existing=AssetDatabase.LoadAssetAtPath<Texture2D>(halfAssetPath);
            if(existing==null){existing=copy;AssetDatabase.CreateAsset(existing,halfAssetPath);}
            else{EditorUtility.CopySerialized(copy,existing);UnityEngine.Object.DestroyImmediate(copy);}
            EditorUtility.SetDirty(existing);AssetDatabase.SaveAssetIfDirty(existing);
            if(!existing.GetRawTextureData<byte>().SequenceEqual(raw))throw new InvalidDataException("RGBAHalf upload changed source bytes.");
            return existing;
        }
        Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (texture == null) throw new InvalidDataException("Missing imported texture " + path);
        if (!slot.srgbTexture || UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat))
            return texture;
        if (QualitySettings.activeColorSpace != ColorSpace.Gamma)
            throw new InvalidDataException("Declared sRGB texture was not loaded with an sRGB view.");
        // In Gamma projects Unity can discard the sRGB view. Convert texels
        // before bilinear filtering; a pow() after sampling is not equivalent.
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        bool readable = importer.isReadable;
        Color32[] source;
        try { importer.isReadable = true; importer.SaveAndReimport(); source = texture.GetPixels32(); }
        finally { importer.isReadable = readable; importer.SaveAndReimport(); }
        Color[] pixels = source.Select(p => new Color(Srgb(p.r/255f),Srgb(p.g/255f),Srgb(p.b/255f),p.a/255f)).ToArray();
        var converted = new Texture2D(texture.width,texture.height,TextureFormat.RGBAFloat,false,true);
        string name = Path.GetFileNameWithoutExtension(slot.file) + "_LinearSampling";
        converted.name = name;
        converted.SetPixels(pixels);
        converted.Apply(false,false);
        converted.filterMode = (FilterMode)slot.filterMode;
        converted.wrapModeU = (TextureWrapMode)slot.wrapU;
        converted.wrapModeV = (TextureWrapMode)slot.wrapV;
        string assetPath = output + "/" + name + ".asset";
        Texture2D asset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        if (asset == null) { asset=converted; AssetDatabase.CreateAsset(asset,assetPath); }
        else { EditorUtility.CopySerialized(converted,asset); UnityEngine.Object.DestroyImmediate(converted); }
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);
        return asset;
    }

    private static float Srgb(float value)
    {
        return value <= 0.04045f ? value/12.92f : Mathf.Pow((value+0.055f)/1.055f,2.4f);
    }

    private static void VerifyShaderBytes(Sample sample,string path)
    {
        if (sample.schema != "my-hook.unity-static-shader-sample.v2") return;
        using (var hash=SHA256.Create())
        {
            string actual=BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
            if (actual!=sample.shaderSha256) throw new InvalidDataException("Shader bytes changed after static recovery.");
        }
    }

    public static void OpenFromCommandLine()
    {
        EditorSceneManager.OpenScene(Argument("-staticSampleScene"), OpenSceneMode.Single);
    }

    public static object Audit(string output)
    {
        Sample sample = JsonUtility.FromJson<Sample>(File.ReadAllText(output + "/sample.json"));
        Material material = AssetDatabase.LoadAssetAtPath<Material>(output + "/" + sample.name + ".mat");
        if (material == null) throw new InvalidDataException("Sample material is missing.");
        var textures = material.GetTexturePropertyNames().Select(name => new {
            property=name, asset=AssetDatabase.GetAssetPath(material.GetTexture(name)),
            graphicsFormat=material.GetTexture(name)==null ? null : material.GetTexture(name).graphicsFormat.ToString()
        }).ToArray();
        return new {material=material.name,shader=material.shader.name,colorSpace=QualitySettings.activeColorSpace.ToString(),
            textures=textures,activeScene=SceneManager.GetActiveScene().path,sceneDirty=SceneManager.GetActiveScene().isDirty};
    }

    [Serializable] private sealed class Validation
    {
        public string status;
        public string shader;
        public string graphicsDevice;
        public string colorSpace;
        public string[] shaderErrors;
        public int changedPixels;
        public int magentaPixels;
        public string preview;
        public string scene;
        public int isolatedLayer;
        public Frame[] frames;
    }

    [Serializable] private sealed class Frame
    {
        public float time;
        public string image;
        public int changedPixels;
        public int magentaPixels;
        public int differenceFromFirst;
    }

    private static string Argument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i+1];
        throw new ArgumentException("Missing " + name);
    }
}
