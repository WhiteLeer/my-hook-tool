using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public static class StaticParticleSampleBuilder
{
    private sealed class CurveHolder : ScriptableObject { public AnimationCurve curve; }

    private static WrapMode FromInfinity(int encoded) => encoded switch
    {
        0 => WrapMode.PingPong, 1 => WrapMode.Loop, 2 => WrapMode.ClampForever, 3 => WrapMode.Default,
        _ => throw new InvalidDataException("Unverified serialized Infinity encoding: " + encoded)
    };

    private static JObject SerializedCurve(AnimationCurve curve)
    {
        var holder=ScriptableObject.CreateInstance<CurveHolder>();
        try
        {
            holder.curve=curve;
            return (JObject)JObject.Parse(EditorJsonUtility.ToJson(holder))["MonoBehaviour"]["curve"];
        }
        finally { Object.DestroyImmediate(holder); }
    }

    public static object CurveContracts()
    {
        var result=new JArray();
        foreach(var mode in new[] {WrapMode.Default,WrapMode.Once,WrapMode.Loop,WrapMode.PingPong,WrapMode.ClampForever})
        {
            var holder=ScriptableObject.CreateInstance<CurveHolder>();
            try
            {
                holder.curve=AnimationCurve.Linear(0,0,1,1);
                holder.curve.preWrapMode=mode; holder.curve.postWrapMode=mode;
                result.Add(new JObject { ["api"]=(int)mode,["name"]=mode.ToString(),
                    ["serialized"]=JObject.Parse(EditorJsonUtility.ToJson(holder)) });
            }
            finally { Object.DestroyImmediate(holder); }
        }
        return result;
    }

    public static object Build(string planPath, string output, string reportPath)
    {
        using var gate = new Mutex(false, "Local\\MyHookGpuValidation");
        bool held;
        try { held = gate.WaitOne(0); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new InvalidOperationException("Another GPU validation is running.");
        bool fresh=!Directory.Exists(output);
        try { return BuildCore(planPath, output, reportPath); }
        catch
        {
            string normalized=output.Replace('\\','/').TrimEnd('/');
            if(fresh&&normalized.StartsWith("Assets/RenderDocShaderSamples/",StringComparison.Ordinal)&&!normalized.Split('/').Contains("..")&&Directory.Exists(normalized))
                AssetDatabase.DeleteAsset(normalized);
            throw;
        }
        finally { gate.ReleaseMutex(); }
    }

    private static object BuildCore(string planPath, string output, string reportPath)
    {
        if (EditorApplication.isPlaying || EditorApplication.isCompiling)
            throw new InvalidOperationException("The editor must be idle.");
        output = output.Replace('\\', '/').TrimEnd('/');
        if (!output.StartsWith("Assets/RenderDocShaderSamples/", StringComparison.Ordinal) || output.Split('/').Contains(".."))
            throw new InvalidDataException("Use a dedicated RenderDocShaderSamples output directory.");
        string safeName = (string)JObject.Parse(File.ReadAllText(planPath))["name"];
        if (safeName != Path.GetFileName(safeName) || safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Unsafe source prefab name.");
        if (Directory.Exists(output))
        {
            if (!File.Exists(reportPath)) throw new InvalidDataException("Existing output has no ownership report.");
            var old = JObject.Parse(File.ReadAllText(reportPath));
            if ((string)old["prefab"] != output + "/" + safeName + "_Particles.prefab" ||
                (string)old["scene"] != output + "/_Preview/StaticParticlePreview.unity" || old["ownedAssets"] is not JObject owned)
                throw new InvalidDataException("Output is not a previous unmodified build owned by this tool.");
            if (owned.Properties().Count() != 2 || owned[output + "/" + safeName + "_Particles.prefab"] == null ||
                owned[output + "/_Preview/StaticParticlePreview.unity"] == null)
                throw new InvalidDataException("Ownership report must contain both generated assets.");
            foreach (var entry in owned.Properties()) Verify(entry.Name, (string)entry.Value);
        }
        var dirty = Resources.FindObjectsOfTypeAll<Object>().Where(x => EditorUtility.IsPersistent(x) && EditorUtility.IsDirty(x))
            .Select(AssetDatabase.GetAssetPath).Distinct().Where(x => x.StartsWith("Assets/", StringComparison.Ordinal)).ToArray();
        if (dirty.Length != 0) throw new InvalidOperationException("Unrelated dirty assets: " + string.Join(", ", dirty));
        var plan = JObject.Parse(File.ReadAllText(planPath));
        bool native = (string)plan["displayMode"] == "native";
        float sampleTime = (float?)plan["sampleTime"] ?? 2f;
        if ((string)plan["schema"] != "my-hook.static-particle-preview.v1") throw new InvalidDataException("Unknown plan schema.");
        Verify((string)plan["package"], (string)plan["packageSha256"]);
        Verify((string)plan["comparison"], (string)plan["comparisonSha256"]);
        ValidatePlanPackage(plan);
        var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
        var referenceAssets=new Dictionary<string,Object>(StringComparer.Ordinal);
        if(plan["nativeMeshes"] is JArray nativeMeshes)
            foreach(JObject mesh in nativeMeshes)
            {
                Verify((string)mesh["asset"],(string)mesh["assetSha256"]);
                var loadedMesh=AssetDatabase.LoadAssetAtPath<Mesh>((string)mesh["asset"]);
                if(loadedMesh==null)throw new InvalidDataException("Verified native mesh asset absent.");
                referenceAssets.Add((string)mesh["key"],loadedMesh);
            }
        var dependencyHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JObject item in (JArray)plan["materials"])
        {
            string asset = (string)item["asset"], samplePath = (string)item["sample"], shaderPath = (string)item["shaderAsset"];
            Verify(asset, (string)item["assetSha256"]);
            Verify(samplePath, (string)item["sampleSha256"]);
            var sample = JObject.Parse(File.ReadAllText(samplePath));
            Verify(shaderPath, (string)sample["shaderSha256"] ?? (string)item["shaderSha256"]);
            if ((string)item["key"] != (string)sample["sourceMaterialFile"] + ":" + (long)sample["sourceMaterialPathId"])
                throw new InvalidDataException("Material identity mismatch.");
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(asset);
            if (mat == null || mat.shader == null || AssetDatabase.GetAssetPath(mat.shader) != shaderPath)
                throw new InvalidDataException("Verified material/Shader is not loaded.");
            if (ShaderUtil.ShaderHasError(mat.shader)) throw new InvalidDataException("Verified Shader has errors.");
            JObject authored = (JObject)item["sourceData"];
            foreach (JToken f in (JArray)authored["Floats"])
                if (mat.GetFloat((string)f["Name"]) != (float)f["Value"]) throw new InvalidDataException("Material float differs from source: " + f["Name"]);
            foreach (JToken c in (JArray)authored["Colors"])
            {
                var actual = mat.GetVector((string)c["Name"]);
                var wanted = ColorOf(c["Value"]);
                if (actual.x != wanted.r || actual.y != wanted.g || actual.z != wanted.b || actual.w != wanted.a)
                    throw new InvalidDataException("Material vector differs from source: " + c["Name"]);
            }
            foreach (JToken t in (JArray)authored["Textures"])
            {
                string name=(string)t["Name"];
                if((long)t["PathID"]==0)
                {
                    var slot=((JArray)sample["textures"]).Single(x=>(string)x["property"]==name);
                    if((string)slot["kind"]!="serialized_null_shader_default"||!(bool)slot["sourceNullPointer"])
                        throw new InvalidDataException("Null source texture was not preserved as a Shader-declared default.");
                }
                else if(mat.GetTexture(name)==null)throw new InvalidDataException("Nonempty source texture is missing: "+name);
                if(mat.GetTextureScale(name)!=new Vector2((float)t["Scale"]["X"],(float)t["Scale"]["Y"])
                    ||mat.GetTextureOffset(name)!=new Vector2((float)t["Offset"]["X"],(float)t["Offset"]["Y"]))
                    throw new InvalidDataException("Material texture UV binding differs from source.");
            }
            materials.Add((string)item["key"], mat);
            foreach (string path in AssetDatabase.GetDependencies(asset).Concat(new[] {samplePath, shaderPath}).Distinct())
                if (File.Exists(path)) dependencyHashes[path] = Hash(path);
        }
        var previous = SceneManager.GetActiveScene();
        ulong sceneMask = UnusedSceneMask(default);
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        var mappings = new JArray();
        var frames = new JArray();
        JObject report = new JObject();
        try
        {
            EditorSceneManager.SetSceneCullingMask(scene, sceneMask);
            SceneManager.SetActiveScene(scene);
            int layer = Enumerable.Range(8,24).Reverse().FirstOrDefault(x => !Object.FindObjectsOfType<Renderer>(true).Any(r => r.gameObject.layer == x));
            if (layer < 8) throw new InvalidOperationException("No unused layer for isolated preview.");
            var objects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            var componentsByKey = new Dictionary<string, Object>(StringComparer.Ordinal);
            foreach (JObject node in ((JArray)plan["nodes"]).OrderBy(x => ((string)x["path"]).Count(c => c == '/')))
            {
                string path = (string)node["path"];
                var go = new GameObject((string)node["name"]) { layer = native?(int)node["gameObject"]["layer"]:layer };
                if(native)
                {
                    if((int)node["gameObject"]["tag"]!=0) throw new InvalidDataException("Non-default source Tag requires its original TagManager mapping.");
                    go.SetActive((bool)node["gameObject"]["active"]);
                }
                if (path.Contains('/')) go.transform.SetParent(objects[path.Substring(0,path.LastIndexOf('/'))].transform, false);
                JObject trs = (JObject)node["transform"];
                go.transform.localPosition = Vector3Of(trs["LocalPosition"], true);
                go.transform.localRotation = new Quaternion((float)trs["LocalRotation"]["X"], (float)trs["LocalRotation"]["Y"],
                    (float)trs["LocalRotation"]["Z"], (float)trs["LocalRotation"]["W"]);
                go.transform.localScale = Vector3Of(trs["LocalScale"], true);
                objects.Add(path, go);
                componentsByKey.Add((string)node["sourceFile"] + ":" + (long)node["pathId"], go);
                if(node["transformSource"] is JObject transformSource)
                    componentsByKey.Add((string)transformSource["sourceFile"]+":"+(long)transformSource["pathId"],go.transform);
                if (((JArray)node["components"]).Any(c => (string)c["type"] == "ParticleSystem")) go.AddComponent<ParticleSystem>();
                foreach (JObject c in (JArray)node["components"])
                    componentsByKey.Add((string)c["sourceFile"] + ":" + (long)c["pathId"],
                        (string)c["type"] == "ParticleSystem" ? (Object)go.GetComponent<ParticleSystem>() : go.GetComponent<ParticleSystemRenderer>());
            }
            foreach (JObject node in (JArray)plan["nodes"])
            foreach (JObject component in (JArray)node["components"])
            {
                Object target = componentsByKey[(string)component["sourceFile"] + ":" + (long)component["pathId"]];
                var mapped = new JArray();
                var unsupported = new JArray();
                var checks = new JArray();
                var so = new SerializedObject(target);
                Object ResolveReference(JObject pointer)
                {
                    string key=(long)pointer["m_FileID"]+":"+(long)pointer["m_PathID"];
                    var binding=component["sourceReferences"]?[key];
                    if(binding==null)throw new InvalidDataException("Source reference lacks independent class/identity proof: "+key);
                    string identity=(string)binding["sourceFile"]+":"+(long)binding["pathId"];
                    if(referenceAssets.TryGetValue(identity,out var asset))return asset;
                    if(componentsByKey.TryGetValue(identity,out var sceneObject))return sceneObject;
                    throw new InvalidDataException("Unresolved verified source reference: "+identity);
                }
                foreach (JProperty f in ((JObject)component["data"]).Properties())
                {
                    if (f.Name == "m_GameObject") { mapped.Add("m_GameObject: owner identity verified"); continue; }
                    if (target is ParticleSystemRenderer && (f.Name == "m_Materials" || f.Name == "m_VertexStreams"
                        || f.Name == "m_UseCustomVertexStreams" || f.Name == "m_EnableGPUInstancing")) continue;
                    Apply(so.FindProperty(f.Name), f.Value, f.Name, mapped, unsupported, checks,ResolveReference);
                }
                so.ApplyModifiedPropertiesWithoutUndo();
                if (target is ParticleSystemRenderer renderer)
                {
                    renderer.sharedMaterials = ((JArray)component["materialPointers"]).Select(p =>
                        (long)p["PathID"]==0?null:materials[(string)p["SourceCAB"] + ":" + (long)p["PathID"]]).ToArray();
                    JObject data = (JObject)component["data"];
                    if ((bool)data["m_UseCustomVertexStreams"])
                        renderer.SetActiveVertexStreams(((JArray)data["m_VertexStreams"]).Select(v => (ParticleSystemVertexStream)(int)v).ToList());
                    mapped.Add("m_Materials: exact sourceFile#PathID");
                    checks.Add(new JObject { ["sourcePath"] = "m_Materials", ["targetPath"] = "m_Materials",
                        ["wanted"] = new JArray(renderer.sharedMaterials.Select(AssetDatabase.GetAssetPath)), ["kind"] = "material_slots" });
                    if ((bool)data["m_UseCustomVertexStreams"])
                        checks.Add(new JObject { ["sourcePath"] = "m_VertexStreams", ["wanted"] = data["m_VertexStreams"].DeepClone(), ["kind"] = "vertex_streams" });
                    if (native)
                    {
                        renderer.enableGPUInstancing = (bool)data["m_EnableGPUInstancing"];
                        mapped.Add("m_EnableGPUInstancing: native renderer");
                        checks.Add(new JObject { ["sourcePath"]="m_EnableGPUInstancing", ["targetPath"]="m_EnableGPUInstancing", ["wanted"]=(bool)data["m_EnableGPUInstancing"], ["kind"]="Boolean" });
                    }
                    else unsupported.Add("m_EnableGPUInstancing: not reproduced by the CPU UI BakeMesh display adapter");
                }
                mappings.Add(new JObject { ["node"] = (string)node["path"], ["type"] = (string)component["type"],
                    ["pathId"] = (long)component["pathId"], ["mapped"] = mapped, ["unsupported"] = unsupported,
                    ["checks"] = checks, ["sourceConvenienceFieldsExcluded"] = component["excludedAsConvenienceFields"].DeepClone() });
                VerifyChecks(target, checks);
            }
            var root = objects[(string)((JArray)plan["nodes"])[0]["path"]];
            Directory.CreateDirectory(output + "/_Preview");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            string prefabPath = output + "/" + (string)plan["name"] + "_Particles.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            foreach(var go in objects.Values) go.layer=layer;
            var camera = new GameObject("Particle Preview Camera").AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = (float?)plan["cameraSize"] ?? 180;
            camera.transform.position = new Vector3(0,0,(float?)plan["cameraZ"] ?? -1000);
            if(native&&(bool?)plan["cameraCenteredOnSourceRoot"]==true)camera.transform.position+=root.transform.position;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = (float?)plan["cameraFar"] ?? 2000;
            if(native)
                foreach(var material in materials.Values)
                    if(material.HasProperty("_FarClip") && material.GetFloat("_FarClip")!=camera.farClipPlane)
                        throw new InvalidDataException("Declared linear-depth fixture far clip differs from preview camera.");
            camera.cullingMask = 1 << layer;
            camera.overrideSceneCullingMask = sceneMask;
            camera.backgroundColor = new Color(0.035f,0.045f,0.07f,1);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            var systems = root.GetComponentsInChildren<ParticleSystem>(true);
            Action<float> sample;
            if (native)
            {
                var clock = root.AddComponent<StaticBattleEffectPreview>();
                clock.previewCamera=camera; clock.systems=systems; clock.previewSeed=(uint)plan["previewSeed"];
                clock.sampleTime=sampleTime; clock.cycleDuration=(float)plan["cycleDuration"];
                clock.animate=false; sample=clock.Sample;
            }
            else
            {
                var adapter=root.AddComponent<StaticUiParticlePreview>();
                adapter.previewCamera=camera; adapter.systems=systems; adapter.previewSeed=(uint)plan["previewSeed"];
                adapter.sampleTime=sampleTime; sample=adapter.Sample;
            }
            // Save authored particles before adding the explicit display-only adapter.
            string scenePath = output + "/_Preview/StaticParticlePreview.unity";
            // Persist a normal standalone scene; isolation is a validation-only condition.
            camera.overrideSceneCullingMask = 0;
            EditorSceneManager.SetSceneCullingMask(scene, SceneCullingMasks.DefaultSceneCullingMask);
            camera.enabled = true;
            if (native) root.GetComponent<StaticBattleEffectPreview>().animate=true;
            EditorSceneManager.SaveScene(scene, scenePath);
            if (native) root.GetComponent<StaticBattleEffectPreview>().animate=false;
            camera.enabled = false;
            EditorSceneManager.SetSceneCullingMask(scene, sceneMask);
            camera.overrideSceneCullingMask = sceneMask;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
            foreach (float time in ((JArray)plan["times"]).Values<float>())
            {
                sample(time);
                string image = Path.Combine(Path.GetDirectoryName(reportPath), "particle-t" + time.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + ".png");
                var counts = new JArray(systems.Select(ps => new JObject { ["name"] = ps.name, ["particles"] = ps.particleCount,
                    ["maxParticles"] = ps.main.maxParticles, ["rendererEnabled"] = ps.GetComponent<ParticleSystemRenderer>().enabled,
                    ["startSizeMin"] = ps.main.startSize.constantMin, ["startSizeMax"] = ps.main.startSize.constantMax }));
                JObject frame = Capture(camera, image);
                frame["time"] = time;
                frame["systems"] = counts;
                if (native)
                {
                    var states=new JArray();
                    foreach(var ps in systems)
                    {
                        var particles=new ParticleSystem.Particle[ps.main.maxParticles];
                        int count=ps.GetParticles(particles);
                        var samples=new JArray();
                        for(int i=0;i<count;i++)
                        {
                            var p=particles[i]; var size=p.GetCurrentSize3D(ps); var color=p.GetCurrentColor(ps);
                            samples.Add(new JObject { ["remainingLifetime"]=p.remainingLifetime,
                                ["size"]=new JArray(size.x,size.y,size.z), ["color"]=new JArray(color.r,color.g,color.b,color.a) });
                        }
                        states.Add(new JObject { ["name"]=ps.name,["particles"]=samples });
                    }
                    frame["sampledParticleState"]=states;
                }
                frames.Add(frame);
            }
            // One identical input must reproduce the same output without depending on editor timing.
            sample(sampleTime);
            string repeat = Path.Combine(Path.GetDirectoryName(reportPath), "particle-repeat.png");
            Capture(camera, repeat);
            bool deterministic = Hash(repeat) == Hash((string)frames.Single(f => (float)f["time"] == sampleTime)["image"]);
            var loaded = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var savedParticles = loaded.GetComponentsInChildren<ParticleSystem>(true);
                if (savedParticles.Length != systems.Length)
                    throw new InvalidDataException("Reloaded source prefab particle count differs.");
                report["savedMaxParticles"] = new JArray(savedParticles.Select(ps => ps.main.maxParticles));
                string sourceRoot = (string)((JArray)plan["nodes"])[0]["path"];
                foreach(JObject node in (JArray)plan["nodes"])
                {
                    string path=((string)node["path"]).Substring(sourceRoot.Length).TrimStart('/');
                    Transform t=path.Length==0?loaded.transform:loaded.transform.Find(path);
                    JObject trs=(JObject)node["transform"];
                    var q=new Quaternion((float)trs["LocalRotation"]["X"],(float)trs["LocalRotation"]["Y"],(float)trs["LocalRotation"]["Z"],(float)trs["LocalRotation"]["W"]);
                    if(t==null || !t.localPosition.Equals(Vector3Of(trs["LocalPosition"],true)) || !t.localScale.Equals(Vector3Of(trs["LocalScale"],true))
                        || !t.localRotation.Equals(q)) throw new InvalidDataException("Reloaded source TRS differs at "+path);
                    if(native && (t.gameObject.activeSelf!=(bool)node["gameObject"]["active"] || t.gameObject.layer!=(int)node["gameObject"]["layer"]))
                        throw new InvalidDataException("Reloaded source GameObject flags differ at "+path);
                }
                report["savedSourceTransformsReadbackPassed"]=true;
                foreach (JObject mapping in mappings)
                {
                    string relativePath = ((string)mapping["node"]).Substring(sourceRoot.Length).TrimStart('/');
                    Transform transform = relativePath.Length == 0 ? loaded.transform : loaded.transform.Find(relativePath);
                    Object saved = (string)mapping["type"] == "ParticleSystem" ? (Object)transform.GetComponent<ParticleSystem>() : transform.GetComponent<ParticleSystemRenderer>();
                    VerifyChecks(saved, (JArray)mapping["checks"]);
                }
                report["savedMappedFieldsReadbackPassed"] = true;
            }
            finally { PrefabUtility.UnloadPrefabContents(loaded); }
            // Reload the saved scene as well; transient-only success is not an acceptance signal.
            EditorSceneManager.CloseScene(scene, true);
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            EditorSceneManager.SetSceneCullingMask(scene, sceneMask);
            SceneManager.SetActiveScene(scene);
            Camera reloadedCamera;
            if (native)
            {
                var clock=scene.GetRootGameObjects().SelectMany(go=>go.GetComponentsInChildren<StaticBattleEffectPreview>(true)).Single();
                clock.animate=false; reloadedCamera=clock.previewCamera; clock.Sample(sampleTime);
            }
            else
            {
                var adapter=scene.GetRootGameObjects().SelectMany(go=>go.GetComponentsInChildren<StaticUiParticlePreview>(true)).Single();
                reloadedCamera=adapter.previewCamera; adapter.Sample(sampleTime);
            }
            reloadedCamera.enabled = false;
            reloadedCamera.overrideSceneCullingMask = sceneMask;
            string sceneImage = Path.Combine(Path.GetDirectoryName(reportPath), "particle-scene-reloaded.png");
            Capture(reloadedCamera, sceneImage);
            bool sceneReloadPassed = Hash(sceneImage) == Hash(repeat);
            var probe = new GameObject("Transient default-scene camera isolation probe").AddComponent<Camera>();
            probe.CopyFrom(reloadedCamera);
            probe.enabled = false;
            probe.overrideSceneCullingMask = SceneCullingMasks.DefaultSceneCullingMask;
            probe.transform.SetPositionAndRotation(reloadedCamera.transform.position, reloadedCamera.transform.rotation);
            string isolationImage = Path.Combine(Path.GetDirectoryName(reportPath), "particle-isolation-probe.png");
            bool isolated;
            try { isolated = (int)Capture(probe, isolationImage)["changedPixels"] == 0; }
            finally { Object.DestroyImmediate(probe.gameObject); }
            report["defaultSceneCameraExcludedPreviewGeometry"] = isolated;
            report["sceneReloadImage"] = sceneImage;
            report["sceneReloadMatches"] = sceneReloadPassed;
            foreach (var dependency in dependencyHashes) Verify(dependency.Key, dependency.Value);
            bool visible = frames.All(f => (int)f["magentaPixels"] == 0);
            if (native)
            {
                foreach(float t in ((JArray)plan["visibleTimes"]).Values<float>())
                    visible &= (int)frames.Single(f=>(float)f["time"]==t)["changedPixels"]>50;
                foreach(float t in ((JArray)plan["emptyTimes"]).Values<float>())
                    visible &= (int)frames.Single(f=>(float)f["time"]==t)["changedPixels"]==0;
            }
            else visible &= frames.All(f=>(int)f["changedPixels"]>50);
            report["status"] = visible && deterministic && sceneReloadPassed && isolated ? "visible_particle_adapter_not_game_reference_verified" : "preview_failed";
            report["sourcePlanSha256"] = Hash(planPath);
            report["scope"] = plan["scope"].DeepClone();
            report["omissions"] = plan["omissions"].DeepClone();
            report["mappings"] = mappings;
            report["frames"] = frames;
            report["deterministic"] = deterministic;
            report["prefab"] = prefabPath;
            report["scene"] = scenePath;
            report["shaderAndMaterialDependenciesUnchanged"] = true;
            report["ownedAssets"] = new JObject { [prefabPath] = Hash(prefabPath), [scenePath] = Hash(scenePath) };
            File.WriteAllText(reportPath, report.ToString());
            if (!visible || !deterministic || !sceneReloadPassed || !isolated) throw new InvalidOperationException("Particle preview failed; inspect " + reportPath);
            return new { scene = scenePath, prefab = prefabPath, report = reportPath, status = (string)report["status"] };
        }
        finally
        {
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
        }
    }

    private static void Apply(SerializedProperty property, JToken value, string path, JArray mapped, JArray unsupported, JArray checks,Func<JObject,Object> resolve=null)
    {
        if (property == null) { unsupported.Add(path); return; }
        if (property.propertyType == SerializedPropertyType.ObjectReference)
        {
            if (value is JObject pointer && (long?)pointer["m_PathID"] == 0)
            {
                property.objectReferenceValue = null; mapped.Add(path + ": null PPtr"); AddCheck(property, path, checks, JValue.CreateNull());
            }
            else if(value is JObject nonempty && resolve!=null)
            {
                property.objectReferenceValue=resolve(nonempty);mapped.Add(path+": independently verified PPtr");
                checks.Add(new JObject{["sourcePath"]=path,["targetPath"]=property.propertyPath,["kind"]="source_reference",["wanted"]=ReferenceIdentity(property.objectReferenceValue)});
            }
            else { throw new InvalidDataException("Unresolved nonempty source reference at " + path); }
            return;
        }
        if (property.propertyType == SerializedPropertyType.AnimationCurve && value is JObject curve)
        {
            if (((JArray)curve["m_Curve"]).Count == 0)
            {
                string parent = property.propertyPath.Substring(0, property.propertyPath.LastIndexOf('.'));
                var modeProperty = property.serializedObject.FindProperty(parent + ".minMaxState");
                int mode = modeProperty?.intValue ?? -1;
                bool used = property.name == "maxCurve" ? mode == 1 || mode == 2 : mode == 2;
                if (mode < 0 || used) throw new InvalidDataException("Empty active curve requires a verified adapter: " + path);
                unsupported.Add(path + ": empty unused curve (constant mode); Unity normalizes empty curves");
                return;
            }
            var keys = ((JArray)curve["m_Curve"]).Select(k => new Keyframe((float)k["time"], (float)k["value"], (float)k["inSlope"],
                (float)k["outSlope"], (float)k["inWeight"], (float)k["outWeight"]) { weightedMode = (WeightedMode)(int)k["weightedMode"] }).ToArray();
            property.animationCurveValue = new AnimationCurve(keys) { preWrapMode = FromInfinity((int)curve["m_PreInfinity"]), postWrapMode = FromInfinity((int)curve["m_PostInfinity"]) };
            mapped.Add(path + ": AnimationCurve keys/weights/wrap adapter");
            AddCheck(property, path, checks, new JObject { ["keys"] = new JArray(((JArray)curve["m_Curve"]).Select(k =>
                new JArray((float)k["time"],(float)k["value"],(float)k["inSlope"],(float)k["outSlope"],(float)k["inWeight"],(float)k["outWeight"],(int)k["weightedMode"]))),
                ["pre"] = (int)FromInfinity((int)curve["m_PreInfinity"]), ["post"] = (int)FromInfinity((int)curve["m_PostInfinity"]) });
            checks.Add(new JObject { ["kind"]="curve_infinity",["sourcePath"]=path,["targetPath"]=property.propertyPath,
                ["wanted"]=new JArray((int)curve["m_PreInfinity"],(int)curve["m_PostInfinity"]) });
            unsupported.Add(path + ".m_RotationOrder: scalar curve does not expose rotation order");
            return;
        }
        if (property.propertyType == SerializedPropertyType.Gradient && value is JObject gradient)
        {
            int nc = (int)gradient["m_NumColorKeys"], na = (int)gradient["m_NumAlphaKeys"];
            if (nc < 2 || nc > 8 || na < 2 || na > 8) throw new InvalidDataException("Unsupported gradient key count at " + path);
            var result = new Gradient { mode = (GradientMode)(int)gradient["m_Mode"] };
            result.SetKeys(Enumerable.Range(0,nc).Select(i => new GradientColorKey(ColorOf(gradient["key"+i]), (int)gradient["ctime"+i]/65535f)).ToArray(),
                Enumerable.Range(0,na).Select(i => new GradientAlphaKey((float)gradient["key"+i]["a"], (int)gradient["atime"+i]/65535f)).ToArray());
            property.gradientValue = result;
            mapped.Add(path + ": Gradient 16-bit time adapter");
            AddCheck(property, path, checks, new JObject {
                ["colors"] = new JArray(Enumerable.Range(0,nc).Select(i => new JArray((float)gradient["key"+i]["r"],(float)gradient["key"+i]["g"],(float)gradient["key"+i]["b"],(int)gradient["ctime"+i]/65535f))),
                ["alpha"] = new JArray(Enumerable.Range(0,na).Select(i => new JArray((float)gradient["key"+i]["a"],(int)gradient["atime"+i]/65535f))),
                ["mode"] = (int)gradient["m_Mode"] });
            return;
        }
        if (property.propertyType == SerializedPropertyType.Color && value is JObject color)
        {
            Color authored = ColorOf(color); property.colorValue = authored; mapped.Add(path);
            AddCheck(property, path, checks, new JArray(authored.r,authored.g,authored.b,authored.a)); return;
        }
        if (property.isArray && property.propertyType != SerializedPropertyType.String && value is JArray array)
        {
            property.arraySize = array.Count;
            for (int i = 0; i < array.Count; i++) Apply(property.GetArrayElementAtIndex(i), array[i], path + "[" + i + "]", mapped, unsupported, checks,resolve);
            mapped.Add(path + ": array length");
            checks.Add(new JObject { ["sourcePath"] = path, ["targetPath"] = property.propertyPath, ["wanted"] = array.Count, ["kind"] = "array_length" });
            return;
        }
        if (value is JObject obj)
        {
            foreach (var field in obj.Properties()) Apply(property.FindPropertyRelative(field.Name), field.Value, path + "." + field.Name, mapped, unsupported, checks,resolve);
            return;
        }
        switch (property.propertyType)
        {
            case SerializedPropertyType.Boolean: property.boolValue = (bool)value; break;
            case SerializedPropertyType.Integer:
            case SerializedPropertyType.Enum: property.longValue = (long)value; break;
            case SerializedPropertyType.Float: property.doubleValue = (double)value; break;
            case SerializedPropertyType.String: property.stringValue = (string)value; break;
            default: unsupported.Add(path + ": " + property.propertyType); return;
        }
        mapped.Add(path);
        JToken expected;
        switch (property.propertyType)
        {
            case SerializedPropertyType.Boolean: expected = new JValue((bool)value); break;
            case SerializedPropertyType.Integer:
            case SerializedPropertyType.Enum: expected = new JValue((long)value); break;
            case SerializedPropertyType.Float: expected = new JValue(property.type == "double" ? (double)value : (double)(float)value); break;
            default: expected = new JValue((string)value); break;
        }
        AddCheck(property, path, checks, expected);
    }

    private static void AddCheck(SerializedProperty p, string sourcePath, JArray checks, JToken expected) => checks.Add(new JObject {
        ["sourcePath"] = sourcePath, ["targetPath"] = p.propertyPath, ["wanted"] = expected, ["propertyType"] = p.propertyType.ToString() });

    private static void VerifyChecks(Object target, JArray checks)
    {
        var so = new SerializedObject(target);
        foreach (JObject check in checks)
        {
            var property = so.FindProperty((string)check["targetPath"]);
            if ((string)check["kind"] != "vertex_streams" && property == null)
                throw new InvalidDataException("Mapped property is missing: " + check["targetPath"]);
            if (check["propertyType"] != null && property.propertyType.ToString() != (string)check["propertyType"])
                throw new InvalidDataException("Mapped property type changed: " + check["targetPath"]);
            JToken actual;
            if((string)check["kind"]=="source_reference")actual=ReferenceIdentity(property.objectReferenceValue);
            else if ((string)check["kind"] == "material_slots") actual = new JArray(((ParticleSystemRenderer)target).sharedMaterials.Select(AssetDatabase.GetAssetPath));
            else if ((string)check["kind"] == "curve_infinity")
            {
                var encoded=SerializedCurve(property.animationCurveValue);
                actual=new JArray((int)encoded["m_PreInfinity"],(int)encoded["m_PostInfinity"]);
            }
            else if ((string)check["kind"] == "vertex_streams")
            {
                var streams = new List<ParticleSystemVertexStream>(); ((ParticleSystemRenderer)target).GetActiveVertexStreams(streams);
                actual = new JArray(streams.Select(s => (int)s));
            }
            else if ((string)check["kind"] == "array_length") actual = property == null ? JValue.CreateNull() : new JValue(property.arraySize);
            else actual = property == null ? JValue.CreateNull() : Snapshot(property);
            if (!JToken.DeepEquals(check["wanted"], actual))
                throw new InvalidDataException("Mapped field changed after apply/save: " + target.name + "/" + (string)check["sourcePath"]
                    + "; wanted=" + check["wanted"] + "; actual=" + actual);
        }
    }

    private static JToken Snapshot(SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case SerializedPropertyType.Boolean: return new JValue(p.boolValue);
            case SerializedPropertyType.Integer:
            case SerializedPropertyType.Enum: return new JValue(p.longValue);
            case SerializedPropertyType.Float: return new JValue(p.doubleValue);
            case SerializedPropertyType.String: return new JValue(p.stringValue);
            case SerializedPropertyType.ObjectReference: return p.objectReferenceValue == null ? JValue.CreateNull() : new JValue(AssetDatabase.GetAssetPath(p.objectReferenceValue));
            case SerializedPropertyType.Color:
                var color = p.colorValue; return new JArray(color.r,color.g,color.b,color.a);
            case SerializedPropertyType.AnimationCurve:
                var curve = p.animationCurveValue;
                return new JObject { ["keys"] = new JArray(curve.keys.Select(k => new JArray(k.time,k.value,k.inTangent,k.outTangent,k.inWeight,k.outWeight,(int)k.weightedMode))),
                    ["pre"] = (int)curve.preWrapMode, ["post"] = (int)curve.postWrapMode };
            case SerializedPropertyType.Gradient:
                var gradient = p.gradientValue;
                return new JObject { ["colors"] = new JArray(gradient.colorKeys.Select(k => new JArray(k.color.r,k.color.g,k.color.b,k.time))),
                    ["alpha"] = new JArray(gradient.alphaKeys.Select(k => new JArray(k.alpha,k.time))), ["mode"] = (int)gradient.mode };
            default: throw new InvalidDataException("No roundtrip assertion for " + p.propertyType);
        }
    }

    private static JObject ReferenceIdentity(Object value)
    {
        if(value is Component component)return new JObject{["type"]=value.GetType().Name,["path"]=AnimationUtility.CalculateTransformPath(component.transform,component.transform.root)};
        if(value is GameObject go)return new JObject{["type"]="GameObject",["path"]=AnimationUtility.CalculateTransformPath(go.transform,go.transform.root)};
        return new JObject{["type"]=value==null?"null":value.GetType().Name,["asset"]=value==null?"":AssetDatabase.GetAssetPath(value)};
    }

    private static Color ColorOf(JToken token)
    {
        if (token["rgba"] != null)
        {
            uint packed = (uint)token["rgba"];
            return (Color)new Color32((byte)packed, (byte)(packed>>8), (byte)(packed>>16), (byte)(packed>>24));
        }
        return new Color((float)token["r"],(float)token["g"],(float)token["b"],(float)token["a"]);
    }

    private static Vector3 Vector3Of(JToken t, bool upper) => new Vector3((float)t[upper?"X":"x"],(float)t[upper?"Y":"y"],(float)t[upper?"Z":"z"]);

    private static ulong UnusedSceneMask(Scene own)
    {
        ulong occupied = SceneCullingMasks.DefaultSceneCullingMask;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene loaded = SceneManager.GetSceneAt(i);
            if (loaded != own) occupied |= EditorSceneManager.GetSceneCullingMask(loaded);
        }
        foreach (var camera in Object.FindObjectsOfType<Camera>(true))
            if (camera.gameObject.scene.IsValid() && camera.gameObject.scene != own)
                occupied |= camera.overrideSceneCullingMask;
        for (int bit = 0; bit < 64; bit++)
        {
            ulong candidate = 1ul << bit;
            if ((candidate & occupied) == 0) return candidate;
        }
        throw new InvalidOperationException("No scene-culling bit can isolate this preview from existing cameras.");
    }

    private static void ValidatePlanPackage(JObject plan)
    {
        using var archive = ZipFile.OpenRead((string)plan["package"]);
        JObject Read(string path)
        {
            using var reader = new StreamReader(archive.GetEntry(path)?.Open() ?? throw new InvalidDataException("Package entry missing: " + path));
            return JObject.Parse(reader.ReadToEnd());
        }
        JObject manifest = Read("manifest.json");
        var comparison = JObject.Parse(File.ReadAllText((string)plan["comparison"]));
        if ((bool)comparison["passed"] != true || (string)comparison["packageSha256"] != (string)plan["packageSha256"])
            throw new InvalidDataException("Package and comparison identity disagree.");
        var expectedComponents = ((JArray)comparison["components"]).ToDictionary(c => (string)c["sourceFile"] + ":" + (long)c["pathId"], StringComparer.Ordinal);
        var sourceNodes = ((JArray)manifest["Nodes"]).ToDictionary(n => (string)n["Path"], StringComparer.Ordinal);
        var particlePaths = sourceNodes.Values.Where(n => ((JArray)n["Components"]).Any(c => (string)c["Type"] == "ParticleSystem"))
            .Select(n => (string)n["Path"]).ToArray();
        var expectedNodes = sourceNodes.Keys.Where(path => particlePaths.Any(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal))).ToHashSet();
        var planNodes = (JArray)plan["nodes"];
        if (planNodes.Count != expectedNodes.Count || !expectedNodes.SetEquals(planNodes.Select(n => (string)n["path"])))
            throw new InvalidDataException("Plan hierarchy differs from the source particle subtree.");
        foreach (JObject node in planNodes)
        {
            JToken source = sourceNodes[(string)node["path"]];
            if ((string)node["sourceFile"] != (string)manifest["SourceCAB"] || (long)node["pathId"] != (long)source["PathID"]
                || (string)node["name"] != (string)source["Name"]) throw new InvalidDataException("Node source identity differs.");
            if((string)plan["displayMode"]=="native")
            {
                var state=((JArray)comparison["nodes"]).Single(n=>(string)n["path"]==(string)node["path"]);
                if(!JToken.DeepEquals(state,node["gameObject"])) throw new InvalidDataException("Native GameObject state differs from source evidence.");
            }
            JToken transform = ((JArray)source["Components"]).Single(c => (string)c["Type"] == "Transform" || (string)c["Type"] == "RectTransform");
            if (!JToken.DeepEquals(node["transform"], Read((string)transform["ParametersFile"])))
                throw new InvalidDataException("Source transform was altered in the plan.");
            foreach (JObject c in (JArray)node["components"])
            {
                string key = (string)c["sourceFile"] + ":" + (long)c["pathId"];
                if (!expectedComponents.TryGetValue(key, out var proven) || (string)proven["type"] != (string)c["type"]
                    || (string)proven["node"] != (string)node["path"] || !(bool)proven["ownerPPtrVerified"])
                    throw new InvalidDataException("Unproven component identity.");
                JToken original = ((JArray)source["Components"]).Single(item => (long)item["PathID"] == (long)c["pathId"]);
                if ((string)original["ParametersFile"] != (string)c["parameterFile"])
                    throw new InvalidDataException("Component parameter source differs.");
                var values = Read((string)c["parameterFile"]);
                var names = ((JArray)proven["nativeRootFields"]).Values<string>().ToHashSet();
                var native = new JObject(values.Properties().Where(p => names.Contains(p.Name)).Select(p => new JProperty(p.Name,p.Value.DeepClone())));
                if (!JToken.DeepEquals(native,c["data"])) throw new InvalidDataException("Source particle parameters were altered in the plan.");
                if ((string)c["type"] == "ParticleSystemRenderer" && !JToken.DeepEquals(c["materialPointers"],original["ParticleRenderer"]["MaterialPointers"]))
                    throw new InvalidDataException("Renderer material source PPtrs were altered.");
                expectedComponents.Remove(key);
            }
        }
        if (expectedComponents.Count != 0) throw new InvalidDataException("Compared components were omitted from the preview plan.");
        var sourceMaterials = ((JArray)manifest["Materials"]).ToDictionary(m => (string)m["Key"], StringComparer.Ordinal);
        foreach (JObject material in (JArray)plan["materials"])
        {
            string key = (string)material["key"];
            if (!sourceMaterials.TryGetValue(key, out var original) || !JToken.DeepEquals(original, material["sourceData"]))
                throw new InvalidDataException("Authored material data differs.");
            sourceMaterials.Remove(key);
        }
        if (sourceMaterials.Count != 0) throw new InvalidDataException("Source materials were omitted.");
    }

    public static object SafetyChecks(string planPath)
    {
        JObject source = JObject.Parse(File.ReadAllText(planPath));
        ValidatePlanPackage(source);
        int rejected = 0;
        void Reject(Action<JObject> mutate)
        {
            var candidate = (JObject)source.DeepClone();
            mutate(candidate);
            try { ValidatePlanPackage(candidate); }
            catch (InvalidDataException) { rejected++; return; }
            throw new InvalidOperationException("A corrupted plan passed preflight.");
        }
        Reject(p => p["nodes"][0]["transform"]["LocalScale"]["X"] = 7);
        Reject(p => p["nodes"][2]["components"][0]["data"]["InitialModule"]["maxNumParticles"] = 99);
        Reject(p => p["nodes"][2]["components"] = new JArray());
        Reject(p => p["nodes"][2]["components"][1]["materialPointers"][0]["PathID"] = 27);
        Reject(p => p["materials"][0]["sourceData"]["Floats"][0]["Value"] = 9);
        return new { status = "passed", sourceValid = true, corruptedPlansRejected = rejected };
    }

    private static JObject Capture(Camera camera, string path)
    {
        var rt = new RenderTexture(512,512,24,RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        var previousTarget = camera.targetTexture;
        Texture2D image = null;
        try
        {
            rt.Create(); camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
            image = new Texture2D(512,512,TextureFormat.RGBA32,false);
            image.ReadPixels(new Rect(0,0,512,512),0,0); image.Apply();
            File.WriteAllBytes(path,image.EncodeToPNG());
            var pixels = image.GetPixels32(); var background = pixels[0];
            return new JObject { ["image"] = path, ["changedPixels"] = pixels.Count(p => Math.Abs(p.r-background.r)>2 || Math.Abs(p.g-background.g)>2 || Math.Abs(p.b-background.b)>2),
                ["magentaPixels"] = pixels.Count(p => p.r>240 && p.b>240 && p.g<20) };
        }
        finally
        {
            RenderTexture.active = previous; camera.targetTexture = previousTarget;
            if (image != null) Object.DestroyImmediate(image);
            rt.Release(); Object.DestroyImmediate(rt);
        }
    }

    private static string Hash(string path)
    {
        using var sha = SHA256.Create(); using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static void Verify(string path, string hash)
    {
        if (Hash(path) != hash) throw new InvalidDataException("Dependency bytes changed: " + path);
    }
}
