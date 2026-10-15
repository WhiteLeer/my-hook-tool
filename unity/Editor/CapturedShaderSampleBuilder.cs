using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class CapturedShaderSampleBuilder
{
    [Serializable] private sealed class Channel { public Vector4[] values; }
    [Serializable] private sealed class MeshData { public Channel[] channels; public int[] triangles; }
    [Serializable] private sealed class Sampler { public string u; public string v; public string w; }
    [Serializable] private sealed class Sample
    {
        public string schema;
        public int eventId;
        public string name;
        public string textureFormat;
        public string textureViewFormat;
        public string textureDataPath;
        public int textureWidth;
        public int textureHeight;
        public string referenceDirectory;
        public int viewportWidth;
        public int viewportHeight;
        public bool depthEnabled;
        public string depthFunction;
        public MeshData mesh;
        public Sampler sampler;
    }
    [Serializable] private sealed class Validation
    {
        public string status;
        public int eventId;
        public int vertexCount;
        public int triangleCount;
        public string graphicsDevice;
        public string colorSpace;
        public string textureGraphicsFormat;
        public string[] shaderErrors;
        public string render;
        public int nonBlackPixels;
        public int expectedChangedPixels;
        public int actualChangedPixels;
        public int overlapChangedPixels;
        public double meanErrorInDrawRegion;
        public double maxErrorInDrawRegion;
        public double backgroundRoundtripMaxError;
        public int backgroundRoundtripChangedPixels;
        public int depthRestoreColorChanges;
        public bool shaderLabGreaterEqualPassesLowerDepth;
        public bool usesReversedZBuffer;
        public double positiveNdcYReadbackCentroid;
        public string limitation;
    }

    public static void ImportFromCommandLine()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Run the capture builder in batch mode");
        string input = Argument("-rdcSampleManifest");
        string output = Argument("-rdcSampleOutput").Replace('\\', '/');
        string reportPath = Argument("-rdcSampleReport");
        if (!output.StartsWith("Assets/", StringComparison.Ordinal) || output.Split('/').Contains(".."))
            throw new ArgumentException("Sample output must remain under Assets");
        Sample sample = JsonUtility.FromJson<Sample>(File.ReadAllText(input));
        if (sample.schema != "my-hook.unity-frozen-draw.v1" || sample.mesh.channels.Length == 0)
            throw new InvalidDataException("Missing captured mesh input");
        Directory.CreateDirectory(output + "/_Preview");
        AssetDatabase.ImportAsset(output, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceSynchronousImport);
        foreach (string file in new[] { "CapturedVertex.hlsl", "CapturedFragment.hlsl", "CapturedGeometry.shader" })
            AssetDatabase.ImportAsset(output + "/" + file, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        string texturePath = output + "/CapturedTexture.png";
        TextureImporter importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
        if (importer == null) throw new InvalidDataException("Missing captured texture importer");
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.sRGBTexture = sample.textureFormat.EndsWith("_SRGB", StringComparison.Ordinal);
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = false;
        importer.isReadable = false;
        importer.filterMode = FilterMode.Bilinear;
        importer.wrapModeU = Address(sample.sampler.u);
        importer.wrapModeV = Address(sample.sampler.v);
        importer.wrapModeW = Address(sample.sampler.w);
        importer.SaveAndReimport();
        byte[] textureBytes = File.ReadAllBytes(sample.textureDataPath);
        if (textureBytes.Length != sample.textureWidth * sample.textureHeight * 16)
            throw new InvalidDataException("Captured linear texture data size mismatch");
        var capturedTexture = new Texture2D(sample.textureWidth, sample.textureHeight, TextureFormat.RGBAFloat, false, true);
        capturedTexture.name = "Captured GPU Texture";
        capturedTexture.LoadRawTextureData(textureBytes);
        capturedTexture.Apply(false, false);
        capturedTexture.filterMode = FilterMode.Bilinear;
        capturedTexture.wrapModeU = Address(sample.sampler.u);
        capturedTexture.wrapModeV = Address(sample.sampler.v);
        capturedTexture.wrapModeW = Address(sample.sampler.w);
        string gpuTexturePath = output + "/CapturedTexture.asset";
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(gpuTexturePath);
        if (texture == null) { texture = capturedTexture; AssetDatabase.CreateAsset(texture, gpuTexturePath); }
        else { EditorUtility.CopySerialized(capturedTexture, texture); UnityEngine.Object.DestroyImmediate(capturedTexture); }
        EditorUtility.SetDirty(texture);
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(output + "/CapturedGeometry.shader");
        if (shader == null) throw new InvalidDataException("Missing reconstructed Shader");
        string materialPath = output + "/CapturedGeometry.mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, materialPath);
        }
        material.shader = shader;
        material.SetTexture("_CapturedTexture0", texture);
        string meshPath = output + "/CapturedGeometry.asset";
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        if (mesh == null)
        {
            mesh = new Mesh();
            AssetDatabase.CreateAsset(mesh, meshPath);
        }
        mesh.Clear();
        mesh.name = sample.name;
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = sample.mesh.channels[0].values.Select(v => new Vector3(v.x, v.y, v.z)).ToArray();
        for (int channel = 0; channel < sample.mesh.channels.Length; channel++)
            mesh.SetUVs(channel, sample.mesh.channels[channel].values.ToList());
        mesh.SetIndices(sample.mesh.triangles, MeshTopology.Triangles, 0, false);
        // Captured clip-space transforms ignore the preview camera's object-space bounds.
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000000);
        ProbeVertices(sample, output, Path.GetDirectoryName(reportPath));
        EditorUtility.SetDirty(mesh);
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var item = new GameObject(sample.name);
        item.AddComponent<MeshFilter>().sharedMesh = mesh;
        item.AddComponent<MeshRenderer>().sharedMaterial = material;
        PrefabUtility.SaveAsPrefabAsset(item, output + "/_Preview/CapturedGeometry.prefab");
        var cameraObject = new GameObject("Captured Draw Camera");
        var camera = cameraObject.AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 0, -10);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.allowHDR = true;
        camera.allowMSAA = false;
        EditorSceneManager.SaveScene(scene, output + "/_Preview/CapturedGeometry.unity");
        string renderPath = Path.Combine(Path.GetDirectoryName(reportPath), "unity-render.exr");
        int nonBlack = 0;
        Texture2D before = LoadReference(sample, "before");
        Texture2D after = LoadReference(sample, "after");
        Texture2D beforeDepth = LoadReference(sample, "before-depth");
        var target = new RenderTexture(sample.viewportWidth, sample.viewportHeight, 24,
            RenderTextureFormat.RGB111110Float, RenderTextureReadWrite.Linear);
        target.Create();
        RenderTexture previous = RenderTexture.active;
        Graphics.SetRenderTarget(target);
        GL.Clear(true, true, Color.clear, SystemInfo.usesReversedZBuffer ? 0 : 1);
        Graphics.Blit(before, target);
        var baseline = new Texture2D(sample.viewportWidth, sample.viewportHeight, TextureFormat.RGBAFloat, false, true);
        baseline.ReadPixels(new Rect(0, 0, sample.viewportWidth, sample.viewportHeight), 0, 0);
        baseline.Apply(false, false);
        var baselinePixels = baseline.GetPixels();
        var sourceBeforePixels = before.GetPixels();
        int backgroundChanges = 0;
        double backgroundMaximum = 0;
        for (int index = 0; index < baselinePixels.Length; index++)
        {
            double difference = RgbDifference(baselinePixels[index], sourceBeforePixels[index]);
            if (difference > 0.000001) backgroundChanges++;
            backgroundMaximum = Math.Max(backgroundMaximum, difference);
        }
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(reportPath), "background-roundtrip.exr"), baseline.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
        UnityEngine.Object.DestroyImmediate(baseline);
        var depthRestore = new Material(Shader.Find("Hidden/RenderDoc/CapturedDepthRestore"));
        bool flippedComparison = ProbeDepthComparison(depthRestore);
        double positiveY = ProbeOrientation(depthRestore);
        if (flippedComparison != SystemInfo.usesReversedZBuffer)
            throw new InvalidOperationException("Measured ShaderLab depth comparison contradicts the platform contract");
        CompareFunction comparison;
        switch (sample.depthEnabled ? sample.depthFunction : "Always")
        {
            case "Greater Equal": comparison = flippedComparison ? CompareFunction.LessEqual : CompareFunction.GreaterEqual; break;
            case "Less Equal": comparison = flippedComparison ? CompareFunction.GreaterEqual : CompareFunction.LessEqual; break;
            case "Always": comparison = CompareFunction.Always; break;
            default: throw new InvalidDataException("Unsupported captured depth function");
        }
        material.SetInt("_CapturedDepthComparison", (int)comparison);
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        Graphics.Blit(beforeDepth, target, depthRestore, 0);
        Graphics.SetRenderTarget(target);
        var depthCheck = new Texture2D(sample.viewportWidth, sample.viewportHeight, TextureFormat.RGBAFloat, false, true);
        depthCheck.ReadPixels(new Rect(0, 0, sample.viewportWidth, sample.viewportHeight), 0, 0);
        depthCheck.Apply(false, false);
        Color[] depthCheckPixels = depthCheck.GetPixels();
        int depthColorChanges = 0;
        for (int index = 0; index < depthCheckPixels.Length; index++)
            if (RgbDifference(depthCheckPixels[index], baselinePixels[index]) > 0.000001) depthColorChanges++;
        UnityEngine.Object.DestroyImmediate(depthCheck);
        if (depthColorChanges != 0)
            throw new InvalidOperationException("The depth upload modified the reference color attachment");
        bool pass = material.SetPass(0);
        if (pass)
        {
            var draw = new CommandBuffer { name = "Captured Draw exact viewport" };
            draw.SetRenderTarget(target);
            draw.SetViewport(new Rect(0, 0, sample.viewportWidth, sample.viewportHeight));
            draw.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0);
            Graphics.ExecuteCommandBuffer(draw);
            draw.Release();
        }
        var pixels = new Texture2D(sample.viewportWidth, sample.viewportHeight, TextureFormat.RGBAFloat, false, true);
        pixels.ReadPixels(new Rect(0, 0, sample.viewportWidth, sample.viewportHeight), 0, 0);
        pixels.Apply(false, false);
        foreach (Color pixel in pixels.GetPixels())
            if (pixel.r > 0.000001f || pixel.g > 0.000001f || pixel.b > 0.000001f) nonBlack++;
        File.WriteAllBytes(renderPath, pixels.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat));
        File.WriteAllBytes(Path.ChangeExtension(renderPath, ".png"), pixels.EncodeToPNG());
        Color[] actualPixels = pixels.GetPixels();
        Color[] beforePixels = before.GetPixels();
        Color[] expectedPixels = after.GetPixels();
        if (actualPixels.Length != beforePixels.Length || actualPixels.Length != expectedPixels.Length)
            throw new InvalidDataException("Reference attachments changed dimensions during import");
        int expectedChanges = 0, actualChanges = 0, overlap = 0, region = 0;
        double totalError = 0, maximumError = 0;
        for (int index = 0; index < actualPixels.Length; index++)
        {
            bool expectedChange = RgbDifference(expectedPixels[index], beforePixels[index]) > 0.000001;
            bool actualChange = RgbDifference(actualPixels[index], beforePixels[index]) > 0.000001;
            if (expectedChange) expectedChanges++;
            if (actualChange) actualChanges++;
            if (expectedChange && actualChange) overlap++;
            if (!expectedChange && !actualChange) continue;
            region++;
            double error = RgbDifference(actualPixels[index], expectedPixels[index]);
            totalError += error;
            maximumError = Math.Max(maximumError, error);
        }
        RenderTexture.active = previous;
        target.Release();
        UnityEngine.Object.DestroyImmediate(target);
        UnityEngine.Object.DestroyImmediate(pixels);
        UnityEngine.Object.DestroyImmediate(depthRestore);
        UnityEngine.Object.DestroyImmediate(before);
        UnityEngine.Object.DestroyImmediate(after);
        UnityEngine.Object.DestroyImmediate(beforeDepth);
        var errors = ShaderUtil.GetShaderMessages(shader)
            .Where(message => message.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error)
            .Select(message => message.message).ToArray();
        var report = new Validation
        {
            status = backgroundChanges > 0 ? "background_roundtrip_failed" :
                pass && errors.Length == 0 && actualChanges > 0 ? "comparison_pending" : "failed",
            eventId = sample.eventId,
            vertexCount = mesh.vertexCount,
            triangleCount = sample.mesh.triangles.Length / 3,
            graphicsDevice = SystemInfo.graphicsDeviceVersion,
            colorSpace = PlayerSettings.colorSpace.ToString(),
            textureGraphicsFormat = texture.graphicsFormat.ToString(),
            shaderErrors = errors,
            render = renderPath,
            nonBlackPixels = nonBlack,
            expectedChangedPixels = expectedChanges,
            actualChangedPixels = actualChanges,
            overlapChangedPixels = overlap,
            meanErrorInDrawRegion = region > 0 ? totalError / region : 0,
            maxErrorInDrawRegion = maximumError,
            backgroundRoundtripMaxError = backgroundMaximum,
            backgroundRoundtripChangedPixels = backgroundChanges,
            depthRestoreColorChanges = depthColorChanges,
            shaderLabGreaterEqualPassesLowerDepth = flippedComparison,
            usesReversedZBuffer = SystemInfo.usesReversedZBuffer,
            positiveNdcYReadbackCentroid = positiveY,
            limitation = "Pixel metrics recorded; no automatic visual acceptance yet."
        };
        File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
        Debug.Log("Captured Shader validation: " + report.status);
        if (report.status == "failed") throw new InvalidOperationException("Captured Shader failed: " + string.Join("; ", errors));
    }

    private static double RgbDifference(Color a, Color b)
    {
        return Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
    }

    private static bool ProbeDepthComparison(Material material)
    {
        var target = new RenderTexture(8, 8, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        target.Create();
        RenderTexture previous = RenderTexture.active;
        Graphics.SetRenderTarget(target);
        GL.Clear(true, true, Color.black, 0.5f);
        Graphics.Blit(Texture2D.whiteTexture, target, material, material.FindPass("ProbeGreaterEqual"));
        var pixels = new Texture2D(8, 8, TextureFormat.RGBAFloat, false, true);
        pixels.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
        pixels.Apply();
        bool passes = pixels.GetPixel(4, 4).r > 0.5f;
        RenderTexture.active = previous;
        UnityEngine.Object.DestroyImmediate(pixels);
        target.Release();
        UnityEngine.Object.DestroyImmediate(target);
        return passes;
    }

    private static double ProbeOrientation(Material material)
    {
        var target = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        target.Create();
        RenderTexture previous = RenderTexture.active;
        var draw = new CommandBuffer();
        draw.SetRenderTarget(target);
        draw.ClearRenderTarget(false, true, Color.black);
        draw.SetViewport(new Rect(0, 0, 32, 32));
        draw.DrawProcedural(Matrix4x4.identity, material, material.FindPass("ProbeOrientation"), MeshTopology.Triangles, 3);
        Graphics.ExecuteCommandBuffer(draw);
        draw.Release();
        RenderTexture.active = target;
        var pixels = new Texture2D(32, 32, TextureFormat.RGBAFloat, false, true);
        pixels.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
        pixels.Apply();
        Color[] colors = pixels.GetPixels();
        int count = 0;
        double rows = 0;
        for (int index = 0; index < colors.Length; index++)
            if (colors[index].r > 0.5f) { count++; rows += index / 32; }
        RenderTexture.active = previous;
        UnityEngine.Object.DestroyImmediate(pixels);
        target.Release();
        UnityEngine.Object.DestroyImmediate(target);
        if (count == 0) throw new InvalidOperationException("The GPU orientation probe did not render");
        return rows / count / 32;
    }

    private static void ProbeVertices(Sample sample, string output, string reportDirectory)
    {
        var probe = AssetDatabase.LoadAssetAtPath<ComputeShader>(output + "/CapturedVertexProbe.compute");
        if (probe == null) throw new InvalidDataException("Missing exact vertex probe");
        int count = sample.mesh.channels[0].values.Length;
        var inputs = new Vector4[count * 3];
        for (int vertex = 0; vertex < count; vertex++)
            for (int channel = 0; channel < 3; channel++)
                inputs[vertex * 3 + channel] = sample.mesh.channels[channel].values[vertex];
        using (var input = new ComputeBuffer(inputs.Length, 16))
        using (var result = new ComputeBuffer(inputs.Length, 16))
        {
            input.SetData(inputs);
            int kernel = probe.FindKernel("Probe");
            probe.SetBuffer(kernel, "CapturedInputs", input);
            probe.SetBuffer(kernel, "CapturedOutputs", result);
            probe.SetInt("CapturedVertexCount", count);
            probe.Dispatch(kernel, (count + 63) / 64, 1, 1);
            var values = new Vector4[inputs.Length];
            result.GetData(values);
            using (var writer = new BinaryWriter(File.Create(Path.Combine(reportDirectory, "unity-postvs.bin"))))
                foreach (Vector4 value in values)
                {
                    writer.Write(value.x); writer.Write(value.y);
                    writer.Write(value.z); writer.Write(value.w);
                }
        }
    }

    private static Texture2D LoadReference(Sample sample, string name)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(sample.referenceDirectory, name + ".rgba32f"));
        if (bytes.Length != sample.viewportWidth * sample.viewportHeight * 16)
            throw new InvalidDataException("Reference float buffer size mismatch");
        var texture = new Texture2D(sample.viewportWidth, sample.viewportHeight, TextureFormat.RGBAFloat, false, true);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.LoadRawTextureData(bytes);
        texture.Apply(false, false);
        return texture;
    }

    private static TextureWrapMode Address(string value)
    {
        switch (value)
        {
            case "Wrap": return TextureWrapMode.Repeat;
            case "ClampEdge": return TextureWrapMode.Clamp;
            case "Mirror": return TextureWrapMode.Mirror;
            case "MirrorOnce": return TextureWrapMode.MirrorOnce;
            default: throw new InvalidDataException("Unsupported captured sampler address " + value);
        }
    }

    private static string Argument(string name)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(arguments, name);
        if (index < 0 || index + 1 == arguments.Length) throw new ArgumentException("Missing " + name);
        return arguments[index + 1];
    }
}
