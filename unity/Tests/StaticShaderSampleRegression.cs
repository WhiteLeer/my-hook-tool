using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Run only in the isolated validation project, never in the user's editor.
public static class StaticShaderSampleRegression
{
    public static void RunFromCommandLine()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Regression requires an isolated batch editor.");
        string[] args = Environment.GetCommandLineArgs();
        Func<string,string> argument = key => args[Array.IndexOf(args,key)+1];
        string sample = argument("-staticSample");
        string output = argument("-staticSampleOutput");
        string report = argument("-staticSampleReport");
        StaticShaderSampleBuilder.Build(sample,output,report);
        Material material = AssetDatabase.FindAssets("t:Material",new[] {output})
            .Select(x => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(x))).Single();
        string materialPath = AssetDatabase.GetAssetPath(material);
        var defaults = new Material(material.shader);
        float expectedOpacity = defaults.GetFloat("_Opacity");
        var sentinel = new Material(material.shader) {name="UnsavedSentinel"};
        string sentinelPath = "Assets/StaticShaderSafetySentinel.mat";
        AssetDatabase.CreateAsset(sentinel,sentinelPath);
        byte[] original = File.ReadAllBytes(sentinelPath);
        Scene previous = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        EditorSceneManager.SaveScene(previous,output + "/RegressionScene.unity");
        sentinel.SetFloat("_Opacity",0.23f);
        EditorUtility.SetDirty(sentinel);
        try
        {
            Check(original.SequenceEqual(File.ReadAllBytes(sentinelPath)) && EditorUtility.IsDirty(sentinel),"Sentinel baseline is invalid before build.");
            bool blocked = false;
            try { StaticShaderSampleBuilder.Build(sample,output,report); }
            catch (InvalidOperationException error) { blocked=error.Message.Contains("Unrelated dirty assets"); }
            Check(blocked,"Unsaved unrelated asset was not rejected before import.");
            Check(original.SequenceEqual(File.ReadAllBytes(sentinelPath)) && EditorUtility.IsDirty(sentinel),"Unrelated dirty asset was saved.");
            Check(Mathf.Approximately(sentinel.GetFloat("_Opacity"),0.23f) && SceneManager.GetActiveScene()==previous,"Rejected build altered user state.");
            AssetDatabase.DeleteAsset(sentinelPath);
            material.SetFloat("_Opacity",0.125f);
            EditorUtility.SetDirty(material);
            StaticShaderSampleBuilder.Build(sample,output,report);
            material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            Check(Mathf.Approximately(material.GetFloat("_Opacity"),expectedOpacity),"Stale default survived rebuild.");
            Check(!EditorUtility.IsDirty(material),"Successful preview left its own material dirty.");
            string badReport = Path.Combine(Path.GetDirectoryName(report),"forced-failure","validation.json");
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(badReport),"static-preview.png"));
            RenderTexture active = RenderTexture.active;
            int targetCount = Resources.FindObjectsOfTypeAll<RenderTexture>().Count(x => x.name=="MyHookStaticShaderSampleCapture");
            bool failed = false;
            try { StaticShaderSampleBuilder.Build(sample,output,badReport); }
            catch (IOException) { failed=true; }
            catch (UnauthorizedAccessException) { failed=true; }
            Check(failed,"Expected screenshot write failure did not occur.");
            material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            Check(RenderTexture.active==active && Resources.FindObjectsOfTypeAll<RenderTexture>().Count(x => x.name=="MyHookStaticShaderSampleCapture")==targetCount,"RenderTexture state leaked.");
            Check(material.GetFloat("_PreviewTime")==-1,"Preview time was not restored after exception.");
            Check(!EditorUtility.IsDirty(material),"Failed preview left its own material dirty.");
            Check(SceneManager.GetActiveScene()==previous && !previous.isDirty,"Active user scene changed.");
            string manifest = File.ReadAllText(sample);
            var parsed = JsonUtility.FromJson<Manifest>(manifest);
            string badSample = Path.Combine(Path.GetDirectoryName(sample),"bad-shader-sample.json");
            File.WriteAllText(badSample,manifest.Replace(parsed.shaderSha256,new string('0',64)));
            failed=false;
            try { StaticShaderSampleBuilder.Build(badSample,output,report); }
            catch (InvalidDataException error) { failed=error.Message.Contains("Shader bytes changed"); }
            finally { File.Delete(badSample); }
            Check(failed,"Changed Shader hash was accepted.");
            Scene saved = EditorSceneManager.OpenScene(output + "/_Preview/StaticShaderPreview.unity",OpenSceneMode.Additive);
            try
            {
                var objects=saved.GetRootGameObjects();
                var renderer=objects.SelectMany(x => x.GetComponentsInChildren<MeshRenderer>()).Single();
                var camera=objects.SelectMany(x => x.GetComponentsInChildren<Camera>()).Single();
                Check(renderer.gameObject.layer>=8 && camera.cullingMask==(1<<renderer.gameObject.layer),"Saved scene differs from validated layer isolation.");
            }
            finally { EditorSceneManager.CloseScene(saved,true); SceneManager.SetActiveScene(previous); }
            material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            foreach (var slot in parsed.textures.Where(x => x.kind=="serialized_texture" && x.srgbTexture))
            {
                var png=new Texture2D(2,2,TextureFormat.RGBA32,false,true);
                try
                {
                    Check(png.LoadImage(File.ReadAllBytes(output + "/" + slot.file)),"Could not read source PNG.");
                    var bound=(Texture2D)material.GetTexture(slot.property);
                    Check(bound.format==TextureFormat.RGBAFloat && bound.mipmapCount==1,"Gamma sampler adapter format/mips differ.");
                    Check(bound.filterMode==(FilterMode)slot.filterMode && bound.wrapModeU==(TextureWrapMode)slot.wrapU && bound.wrapModeV==(TextureWrapMode)slot.wrapV,"Sampler settings differ.");
                    Color32[] raw=png.GetPixels32();
                    Color[] actual=bound.GetPixels();
                    Check(raw.Length==actual.Length,"Texture size differs.");
                    for(int i=0;i<raw.Length;i++) Check(Mathf.Abs(actual[i].r-Srgb(raw[i].r/255f))<0.000001f
                        && Mathf.Abs(actual[i].g-Srgb(raw[i].g/255f))<0.000001f
                        && Mathf.Abs(actual[i].b-Srgb(raw[i].b/255f))<0.000001f
                        && Mathf.Abs(actual[i].a-raw[i].a/255f)<0.000001f,"Linear texel or alpha differs from source PNG.");
                }
                finally { UnityEngine.Object.DestroyImmediate(png); }
            }
            File.WriteAllText(Path.ChangeExtension(report,"safety.json"),"{\"status\":\"passed\",\"checks\":[\"reject_unrelated_dirty_asset\",\"reset_shader_defaults\",\"exception_cleanup\",\"restore_time_and_scene\",\"reject_changed_shader\",\"reload_saved_scene_isolation\",\"linear_texels_alpha_sampler\"]}");
        }
        finally
        {
            if (defaults!=null) UnityEngine.Object.DestroyImmediate(defaults);
            if (AssetDatabase.LoadAssetAtPath<Material>(sentinelPath) != null) AssetDatabase.DeleteAsset(sentinelPath);
            AssetDatabase.DeleteAsset(output + "/RegressionScene.unity");
        }
    }

    [Serializable] private sealed class Manifest { public string shaderSha256; public Slot[] textures; }
    [Serializable] private sealed class Slot
    {
        public string kind; public string file; public string property;
        public bool srgbTexture; public int wrapU; public int wrapV; public int filterMode;
    }
    private static float Srgb(float value) { return value<=0.04045f ? value/12.92f : Mathf.Pow((value+0.055f)/1.055f,2.4f); }
    private static void Check(bool condition,string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
