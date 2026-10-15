using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Object=UnityEngine.Object;

public static class StaticCharacterSampleBuilder
{
    private static string Hash(string path)
    {
        using var sha=new SHA256CryptoServiceProvider();using var stream=File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
    }

    public static object Build(string planPath,string output,string reportPath)
    {
        if(EditorApplication.isPlaying||EditorApplication.isCompiling)throw new InvalidOperationException("Editor must be idle.");
        output=output.Replace('\\','/').TrimEnd('/');
        if(!output.StartsWith("Assets/RenderDocShaderSamples/HSR-4.6/",StringComparison.Ordinal)||output.Split('/').Contains(".."))
            throw new InvalidDataException("Use a new versioned sample directory.");
        var plan=JObject.Parse(File.ReadAllText(planPath));
        if(Hash((string)plan["sourcePackage"])!=(string)plan["sourcePackageSha256"]||Hash((string)plan["meshes"])!=(string)plan["meshesSha256"])
            throw new InvalidDataException("Source package/mesh evidence changed.");
        var sourceMeshes=JObject.Parse(File.ReadAllText((string)plan["meshes"]));
        string prefabPath=output+"/_Preview/CharacterPreview.prefab",scenePath=output+"/_Preview/CharacterPreview.unity";
        if(!File.Exists(prefabPath)&&!File.Exists(scenePath)&&new[]{"Model","Animations","_Preview"}.Any(p=>Directory.Exists(output+"/"+p)&&Directory.GetFiles(output+"/"+p,"*",SearchOption.AllDirectories).Length>0))
            throw new InvalidDataException("Unowned model/animation/preview files exist; choose an empty character output.");
        if(File.Exists(prefabPath)||File.Exists(scenePath))
        {
            if(!File.Exists(reportPath))throw new InvalidDataException("Existing preview has no ownership report.");
            var old=JObject.Parse(File.ReadAllText(reportPath));
            if((string)old["prefab"]!=prefabPath||(string)old["scene"]!=scenePath||Hash(prefabPath)!=(string)old["prefabSha256"]||Hash(scenePath)!=(string)old["sceneSha256"])
                throw new InvalidDataException("Existing preview changed; refusing overwrite.");
            if(!(old["ownedAssets"] is JArray owned))throw new InvalidDataException("Existing output has no complete ownership manifest.");
            var known=owned.ToDictionary(x=>(string)x["path"],x=>(string)x["sha256"]);
            foreach(var pair in known)if(!File.Exists(pair.Key)||Hash(pair.Key)!=pair.Value)throw new InvalidDataException("Owned asset changed: "+pair.Key);
            foreach(string file in Directory.GetFiles(output,"*",SearchOption.AllDirectories))
                if(!known.ContainsKey(file.Replace('\\','/')))throw new InvalidDataException("Unowned file in output: "+file);
        }
        var dirty=Resources.FindObjectsOfTypeAll<Object>().Where(x=>EditorUtility.IsPersistent(x)&&EditorUtility.IsDirty(x))
            .Select(AssetDatabase.GetAssetPath).Where(p=>p.StartsWith("Assets/")).Distinct().ToArray();
        if(dirty.Length!=0)throw new InvalidOperationException("Unrelated dirty assets: "+string.Join(",",dirty));
        Directory.CreateDirectory(output+"/Model");Directory.CreateDirectory(output+"/Animations");Directory.CreateDirectory(output+"/_Preview");
        Directory.CreateDirectory(output+"/Model/NativeMeshes");
        string modelPath=Copy((string)plan["model"],(string)plan["modelSha256"],output+"/Model");
        string clipPath=Copy((string)plan["clip"],(string)plan["clipSha256"],output+"/Animations");
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var importer=AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if(importer==null)throw new InvalidDataException("No model importer.");
        var settings=new SerializedObject(importer);
        var units=settings.FindProperty("m_UseFileUnits");
        if(units==null)throw new InvalidDataException("Convert Units serialized setting unavailable.");
        units.boolValue=false;settings.ApplyModifiedPropertiesWithoutUndo();
        importer.globalScale=1;importer.animationType=ModelImporterAnimationType.Generic;
        importer.importAnimation=false;importer.isReadable=true;importer.meshCompression=ModelImporterMeshCompression.Off;
        importer.weldVertices=false;importer.optimizeMeshPolygons=false;importer.optimizeMeshVertices=false;
        importer.SaveAndReimport();
        if(new SerializedObject(importer).FindProperty("m_UseFileUnits").boolValue)throw new InvalidDataException("Convert Units remains enabled.");
        var model=AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);var clip=AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
        if(model==null||clip==null||clip.legacy)throw new InvalidDataException("Expected rigged model and nonlegacy clip.");
        var previous=SceneManager.GetActiveScene();
        ulong occupied=SceneCullingMasks.DefaultSceneCullingMask;
        for(int i=0;i<SceneManager.sceneCount;i++)occupied|=EditorSceneManager.GetSceneCullingMask(SceneManager.GetSceneAt(i));
        foreach(var c in Object.FindObjectsOfType<Camera>(true))occupied|=c.overrideSceneCullingMask;
        ulong mask=Enumerable.Range(0,64).Select(i=>1UL<<i).First(x=>(occupied&x)==0);
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
        var frames=new JArray();var rendererReport=new JArray();var report=new JObject();
        try
        {
            SceneManager.SetActiveScene(scene);EditorSceneManager.SetSceneCullingMask(scene,mask);
            // FBX is an independent interchange export; its mirrored basis cannot be mixed with native .anim curves.
            var actor=new GameObject(model.name);
            foreach(JObject trs in (JArray)plan["sourceTransforms"])
            {
                string path=(string)trs["path"];
                Transform t=actor.transform;
                foreach(string part in path.Split(new[]{'/'},StringSplitOptions.RemoveEmptyEntries))
                {
                    var next=t.Find(part);if(next==null){next=new GameObject(part).transform;next.SetParent(t,false);}t=next;
                }
                JToken p=trs["position"],q=trs["rotation"],s=trs["scale"];
                t.localPosition=new Vector3((float)p[0],(float)p[1],(float)p[2]);
                t.localRotation=new Quaternion((float)q[0],(float)q[1],(float)q[2],(float)q[3]);
                t.localScale=new Vector3((float)s[0],(float)s[1],(float)s[2]);
            }
            foreach(JObject item in (JArray)plan["renderers"])
            {
                var renderer=actor.transform.Find((string)item["path"]).gameObject.AddComponent<SkinnedMeshRenderer>();
                renderer.bones=((JArray)item["bones"]).Values<string>().Select(p=>actor.transform.Find(p)).ToArray();
            }
            foreach(var animator in actor.GetComponentsInChildren<Animator>(true))animator.enabled=false;
            var renderers=actor.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var source=(JArray)plan["renderers"];
            var fixtureMaterials=new HashSet<Material>();
            if(renderers.Length!=source.Count)throw new InvalidDataException("Rigged renderer count differs from source.");
            foreach(JObject item in source)
            {
                string path=(string)item["path"];
                var renderer=(path.Length==0?actor.transform:actor.transform.Find(path))?.GetComponent<SkinnedMeshRenderer>();
                if(renderer==null)throw new InvalidDataException("Source renderer path unresolved: "+path);
                if(((JArray)item["materials"]).All(x=>x.Type==JTokenType.Null))
                {
                    foreach(JObject identity in (JArray)item["materialSources"])VerifyOmission(identity);
                    renderer.enabled=false;renderer.sharedMaterials=Array.Empty<Material>();
                    rendererReport.Add(new JObject { ["path"]=path,["status"]="source_stencil_draw_omitted_not_restored",["sourceRenderer"]=item["sourceIdentity"] });
                    continue;
                }
                JObject originalMesh=(JObject)sourceMeshes[(string)item["meshSource"]];
                string nativePath=output+"/Model/NativeMeshes/"+(string)originalMesh["name"]+".asset";
                var persistent=AssetDatabase.LoadAssetAtPath<Mesh>(nativePath);
                bool create=persistent==null;persistent=NativeMesh(originalMesh,persistent);
                if(create)AssetDatabase.CreateAsset(persistent,nativePath);
                EditorUtility.SetDirty(persistent);AssetDatabase.SaveAssetIfDirty(persistent);renderer.sharedMesh=persistent;
                if(renderer.bones.Length!=((JArray)item["bones"]).Count||renderer.sharedMesh.bindposes.Length!=renderer.bones.Length)
                    throw new InvalidDataException("Bone/bindpose counts differ: "+path);
                string[] bonePaths=renderer.bones.Select(t=>Relative(t,actor.transform)).ToArray();
                if(!bonePaths.SequenceEqual(((JArray)item["bones"]).Values<string>()))throw new InvalidDataException("Ordered bone references differ: "+path);
                float bindError=0;
                for(int i=0;i<renderer.bones.Length;i++)
                {
                    var matrix=renderer.transform.worldToLocalMatrix*renderer.bones[i].localToWorldMatrix*renderer.sharedMesh.bindposes[i];
                    for(int j=0;j<16;j++)bindError=Mathf.Max(bindError,Mathf.Abs(matrix[j]-Matrix4x4.identity[j]));
                }
                if(bindError>1e-4f)throw new InvalidDataException("Source rest skin matrices differ from identity: "+path+" "+bindError);
                if(renderer.sharedMesh.vertexCount!=(int)item["vertexCount"]||renderer.sharedMesh.subMeshCount!=((JArray)item["materials"]).Count)
                    throw new InvalidDataException("Vertex/submesh count differs: "+path+" actual="+renderer.sharedMesh.vertexCount+"/"+renderer.sharedMesh.subMeshCount+" source="+item["vertexCount"]+"/"+((JArray)item["materials"]).Count);
                var slots=new List<Material>();
                for(int slot=0;slot<((JArray)item["materials"]).Count;slot++)
                {
                    JToken asset=item["materials"][slot],identity=item["materialSources"][slot];
                    string sourceIdentity=(string)identity["sourceFile"]+":"+(string)identity["pathId"];
                    if(asset.Type==JTokenType.Null)
                    {
                        VerifyOmission((JObject)identity);
                        slots.Add(null);continue;
                    }
                    if(Hash((string)identity["sample"])!=(string)identity["sampleSha256"])throw new InvalidDataException("Material binding evidence changed.");
                    var sample=JObject.Parse(File.ReadAllText((string)identity["sample"]));
                    if((string)sample["sourceMaterialFile"]!=(string)identity["sourceFile"]||(long)sample["sourceMaterialPathId"]!=(long)identity["pathId"])
                        throw new InvalidDataException("Source material PPtr mismatch.");
                    var material=AssetDatabase.LoadAssetAtPath<Material>((string)asset);
                    if(!((string)asset).StartsWith(output+"/Materials/",StringComparison.Ordinal)||((string)asset).Split('/').Contains(".."))
                        throw new InvalidDataException("Material fixture writes must remain inside this owned character output.");
                    if(material==null||material.shader==null||ShaderUtil.ShaderHasError(material.shader))throw new InvalidDataException("Missing or invalid original-pass material.");
                    if(material.shader.name!=(string)sample["shaderName"]||Hash(AssetDatabase.GetAssetPath(material.shader))!=(string)sample["shaderSha256"])
                        throw new InvalidDataException("Material shader differs from audited source adapter.");
                    foreach(JObject value in (JArray)sample["floats"])
                        if(Mathf.Abs(material.GetFloat((string)value["name"])-(float)value["value"])>1e-5f)throw new InvalidDataException("Source material scalar changed: "+value["name"]);
                    foreach(JObject value in (JArray)sample["colors"])
                    {
                        var v=value["value"];var actual=material.GetVector((string)value["name"]);
                        if((actual-new Vector4((float)v[0],(float)v[1],(float)v[2],(float)v[3])).sqrMagnitude>1e-10f)throw new InvalidDataException("Source material vector changed: "+value["name"]);
                    }
                    if(fixtureMaterials.Add(material))
                    {
                        foreach(var property in ((JObject)plan["sceneFixture"]["vectors"]).Properties())
                        {
                            if(!material.HasProperty(property.Name))continue;
                            var v=property.Value;material.SetVector(property.Name,new Vector4((float)v[0],(float)v[1],(float)v[2],(float)v[3]));
                        }
                        EditorUtility.SetDirty(material);AssetDatabase.SaveAssetIfDirty(material);
                    }
                    slots.Add(material);
                }
                if(slots.Any(m=>m==null)&&slots.Any(m=>m!=null))
                {
                    var original=renderer.sharedMesh;
                    var kept=Enumerable.Range(0,slots.Count).Where(i=>slots[i]!=null).Select(i=>original.GetIndices(i)).ToArray();
                    string filteredPath=output+"/_Preview/"+renderer.name+"_ReflectionDraws.asset";
                    var saved=AssetDatabase.LoadAssetAtPath<Mesh>(filteredPath);
                    bool createFiltered=saved==null;saved=NativeMesh(originalMesh,saved);
                    saved.subMeshCount=kept.Length;for(int i=0;i<kept.Length;i++)saved.SetIndices(kept[i],MeshTopology.Triangles,i,false);
                    if(createFiltered)AssetDatabase.CreateAsset(saved,filteredPath);
                    EditorUtility.SetDirty(saved);AssetDatabase.SaveAssetIfDirty(saved);renderer.sharedMesh=saved;
                }
                renderer.sharedMaterials=slots.Where(m=>m!=null).ToArray();renderer.updateWhenOffscreen=true;
                // Source stencil-only slots are intentionally excluded, not filled with fallback colors.
                renderer.enabled=slots.Any(m=>m!=null);
                rendererReport.Add(new JObject { ["path"]=path,["vertices"]=renderer.sharedMesh.vertexCount,["bones"]=renderer.bones.Length,
                    ["materials"]=new JArray(slots.Select(AssetDatabase.GetAssetPath)),["sourceRenderer"]=item["sourceIdentity"],["restSkinMatrixMaxError"]=bindError });
            }
            var bindings=AnimationUtility.GetCurveBindings(clip);
            var missing=bindings.Where(b=>b.type==typeof(Transform)&&actor.transform.Find(b.path)==null&&b.path!="").Select(b=>b.path).Distinct().ToArray();
            if(missing.Length>0)throw new InvalidDataException("Unresolved transform curve paths: "+string.Join(",",missing.Take(12)));
            var clock=actor.AddComponent<StaticCharacterMotionPreview>();clock.model=actor;clock.clip=clip;clock.animate=false;clock.sampleTime=1;
            clock.Sample(0);
            var unsupported=bindings.Where(b=>b.type!=typeof(Transform)&&
                (b.type!=typeof(SkinnedMeshRenderer)||!b.propertyName.StartsWith("blendShape.")||actor.transform.Find(b.path)?.GetComponent<SkinnedMeshRenderer>()?.sharedMesh?.GetBlendShapeIndex(b.propertyName.Substring(11))<0)).ToArray();
            var transforms=actor.GetComponentsInChildren<Transform>(true);
            var before=transforms.ToDictionary(t=>Relative(t,actor.transform),t=>t.localRotation);
            var bounds=renderers.Where(r=>r.enabled).Select(r=>r.bounds).Aggregate((a,b)=>{a.Encapsulate(b);return a;});
            var camera=new GameObject("Character preview camera").AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;
            camera.orthographicSize=bounds.size.y*0.65f;camera.transform.position=bounds.center+Vector3.forward*Mathf.Max(3,bounds.size.y*2);
            camera.transform.LookAt(bounds.center);camera.nearClipPlane=0.01f;camera.farClipPlane=100;camera.overrideSceneCullingMask=mask;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.045f,.07f,1);camera.allowHDR=false;camera.allowMSAA=false;
            foreach(float time in new[]{0f,1f,2f,4f})
            {
                clock.Sample(time);
                var changed=transforms.Where(t=>Quaternion.Angle(before[Relative(t,actor.transform)],t.localRotation)>0.01f).Select(t=>Relative(t,actor.transform)).ToArray();
                JObject frame=Capture(camera,Path.Combine(Path.GetDirectoryName(reportPath),"character-t"+time+".png"));
                frame["time"]=time;frame["changedBones"]=new JArray(changed);frames.Add(frame);
            }
            bool majorMotion=frames.Skip(1).Any(f=>((JArray)f["changedBones"]).Values<string>().Any(p=>p.EndsWith("Spine1_M")||p.EndsWith("Chest_M")||p.EndsWith("UpperArm_L")));
            bool visible=frames.All(f=>(int)f["changedPixels"]>100&&(int)f["magentaPixels"]==0);
            bool visibleMotion=frames.Select(f=>Hash((string)f["image"])).Distinct().Count()>1;
            if(!majorMotion)throw new InvalidDataException("No verified torso/limb rotation change; do not accept secondary-only animation.");
            if(!visibleMotion)throw new InvalidDataException("All sampled images are identical; visible motion not verified.");
            clock.Sample(1);var repeated=Capture(camera,Path.Combine(Path.GetDirectoryName(reportPath),"character-repeat-t1.png"));
            string reference=Hash((string)frames[1]["image"]);
            if(Hash((string)repeated["image"])!=reference)throw new InvalidDataException("Repeated time-1 render differs.");
            clock.animate=true;PrefabUtility.SaveAsPrefabAsset(actor,prefabPath);
            camera.enabled=true;camera.overrideSceneCullingMask=0;EditorSceneManager.SetSceneCullingMask(scene,SceneCullingMasks.DefaultSceneCullingMask);
            EditorSceneManager.SaveScene(scene,scenePath);
            EditorSceneManager.CloseScene(scene,true);scene=EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Additive);
            SceneManager.SetActiveScene(scene);EditorSceneManager.SetSceneCullingMask(scene,mask);
            actor=scene.GetRootGameObjects().Single(g=>g.GetComponent<StaticCharacterMotionPreview>()!=null);
            clock=actor.GetComponent<StaticCharacterMotionPreview>();clock.animate=false;clock.Sample(1);
            camera=scene.GetRootGameObjects().Select(g=>g.GetComponent<Camera>()).Single(c=>c!=null);
            camera.enabled=false;camera.overrideSceneCullingMask=mask;
            var reloaded=Capture(camera,Path.Combine(Path.GetDirectoryName(reportPath),"character-reloaded-t1.png"));
            if(Hash((string)reloaded["image"])!=reference)throw new InvalidDataException("Saved scene render differs after reload.");
            foreach(JObject item in source)
            {
                var r=actor.transform.Find((string)item["path"]).GetComponent<SkinnedMeshRenderer>();
                if(!r.enabled)continue;
                if(!r.bones.Select(t=>Relative(t,actor.transform)).SequenceEqual(((JArray)item["bones"]).Values<string>())||r.sharedMesh.vertexCount!=(int)item["vertexCount"])
                    throw new InvalidDataException("Saved rig differs after reload.");
                if(!r.sharedMaterials.Select(AssetDatabase.GetAssetPath).SequenceEqual(((JArray)item["materials"]).Where(t=>t.Type!=JTokenType.Null).Values<string>()))
                    throw new InvalidDataException("Saved material references differ after reload.");
            }
            report["status"]=visible?"animated_original_reflection_pass_fixture_not_game_equivalence":"visual_validation_failed";
            report["model"]=modelPath;report["clip"]=clipPath;report["clipLength"]=clip.length;report["sourcePlanSha256"]=Hash(planPath);
            report["rendererChecks"]=rendererReport;report["transformCurveBindings"]=bindings.Count(b=>b.type==typeof(Transform));
            report["unresolvedTransformPaths"]=new JArray(missing);report["majorBodyMotion"]=majorMotion;report["frames"]=frames;
            report["visibleMotion"]=visibleMotion;report["captureGeometry"]= "Deterministic CPU-baked current pose; original materials/shaders. Scene retains native SkinnedMeshRenderers.";
            report["omissions"]=plan["omissions"];report["prefab"]=prefabPath;report["scene"]=scenePath;
            report["sceneFixture"]=plan["sceneFixture"];
            report["deterministicRepeatSha256"]=reference;report["savedReloadSameImage"]=true;
            report["unsupportedNonTransformCurves"]=new JArray(unsupported.Select(b=>new JObject{["path"]=b.path,["type"]=b.type.FullName,["property"]=b.propertyName}));
            report["prefabSha256"]=Hash(prefabPath);report["sceneSha256"]=Hash(scenePath);
            report["ownedAssets"]=new JArray(Directory.GetFiles(output,"*",SearchOption.AllDirectories).Select(p=>new JObject{["path"]=p.Replace('\\','/'),["sha256"]=Hash(p)}));
            File.WriteAllText(reportPath,report.ToString());
            if(!visible)throw new InvalidDataException("Animated render not visible; inspect report.");
            return new {scene=scenePath,prefab=prefabPath,status=(string)report["status"]};
        }
        finally
        {
            if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);
        }
    }

    private static void VerifyOmission(JObject identity)
    {
        string key=(string)identity["sourceFile"]+":"+(string)identity["pathId"];
        if(key!="CAB-a62ffe0c3fe223f92914adeee4c8f648:3727209815991833530"&&key!="CAB-b109a864445d54dfe27e7af888d4a106:3082673868023183515")
            throw new InvalidDataException("Unapproved material omission: "+key);
    }

    private static string Relative(Transform t,Transform root)
    {
        if(t==root)return "";var names=new List<string>();
        while(t!=root&&t!=null){names.Add(t.name);t=t.parent;}
        if(t==null)throw new InvalidDataException("Bone outside actor root.");names.Reverse();return string.Join("/",names);
    }
    private static Mesh NativeMesh(JObject data,Mesh existing=null)
    {
        var mesh=existing??new Mesh();mesh.Clear(false);
        mesh.name=(string)data["name"];mesh.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;
        Vector3 V3(JToken v)=>new Vector3((float)v[0],(float)v[1],(float)v[2]);
        Vector4 V4(JToken v)=>new Vector4((float)v[0],(float)v[1],(float)v[2],(float)v[3]);
        mesh.vertices=((JArray)data["vertices"]).Select(V3).ToArray();
        if(((JArray)data["normals"]).Count>0)mesh.normals=((JArray)data["normals"]).Select(V3).ToArray();
        if(((JArray)data["tangents"]).Count>0)mesh.tangents=((JArray)data["tangents"]).Select(V4).ToArray();
        if(((JArray)data["colors"]).Count>0)
        {
            if((int)data["colorFormat"]!=2)throw new InvalidDataException("Unsupported source vertex-color storage; no range-based guess allowed.");
            mesh.colors32=((JArray)data["colors"]).Select(v=>new Color32(checked((byte)(int)v[0]),checked((byte)(int)v[1]),checked((byte)(int)v[2]),v.Count()>3?checked((byte)(int)v[3]):(byte)255)).ToArray();
        }
        for(int i=0;i<8;i++)
        {
            var uv=(JArray)data["uvs"][i];if(uv.Count==0)continue;
            int dimension=uv[0].Count();
            if(dimension==2)mesh.SetUVs(i,uv.Select(v=>new Vector2((float)v[0],(float)v[1])).ToList());
            else if(dimension==3)mesh.SetUVs(i,uv.Select(V3).ToList());
            else if(dimension==4)mesh.SetUVs(i,uv.Select(V4).ToList());
            else throw new InvalidDataException("Unsupported source UV width.");
        }
        mesh.bindposes=((JArray)data["bindposes"]).Select(v=>{var m=new Matrix4x4();for(int r=0;r<4;r++)for(int c=0;c<4;c++)m[r,c]=(float)v[r*4+c];return m;}).ToArray();
        var weights=(JArray)data["weights"];var indices=(JArray)data["indices"];
        if(weights.Count!=mesh.vertexCount||indices.Count!=mesh.vertexCount)throw new InvalidDataException("Source skin arrays mismatch.");
        float Weight(JToken w,int i)=>i<w.Count()?(float)w[i]:0;
        int Index(JToken w,int i)=>i<w.Count()?(int)w[i]:0;
        if(weights.Any(w=>w.Count()<1||w.Count()>4||Mathf.Abs(w.Values<float>().Sum()-1)>0.0001f))throw new InvalidDataException("Unsupported source influence count or normalization.");
        mesh.boneWeights=weights.Select((w,i)=>new BoneWeight{weight0=Weight(w,0),weight1=Weight(w,1),weight2=Weight(w,2),weight3=Weight(w,3),
            boneIndex0=Index(indices[i],0),boneIndex1=Index(indices[i],1),boneIndex2=Index(indices[i],2),boneIndex3=Index(indices[i],3)}).ToArray();
        var triangles=(JArray)data["triangles"];mesh.subMeshCount=triangles.Count;
        for(int i=0;i<triangles.Count;i++)mesh.SetIndices(((JArray)triangles[i]).Values<int>().ToArray(),MeshTopology.Triangles,i,false);
        foreach(JObject frame in (JArray)data["shapes"])
        {
            var p=new Vector3[mesh.vertexCount];var n=new Vector3[mesh.vertexCount];var t=new Vector3[mesh.vertexCount];
            foreach(JObject d in (JArray)frame["deltas"]){int i=(int)d["index"];p[i]=V3(d["position"]);n[i]=V3(d["normal"]);t[i]=V3(d["tangent"]);}
            mesh.AddBlendShapeFrame((string)frame["name"],(float)frame["weight"],p,n,t);
        }
        mesh.RecalculateBounds();return mesh;
    }
    private static string Copy(string source,string expected,string folder)
    {
        if(Hash(source)!=expected)throw new InvalidDataException("Changed source export: "+source);
        string target=folder+"/"+Path.GetFileName(source);
        if(!File.Exists(target)||Hash(target)!=expected)File.Copy(source,target,true);return target;
    }
    private static JObject Capture(Camera camera,string path)
    {
        var rt=new RenderTexture(640,640,24,RenderTextureFormat.ARGB32);var active=RenderTexture.active;var old=camera.targetTexture;
        Texture2D image=null;
        var snapshots=new List<(SkinnedMeshRenderer source,GameObject draw,Mesh mesh)>();
        try
        {
            // Camera.Render does not advance Unity's GPU skinning frame. Snapshot the sampled pose explicitly.
            foreach(var renderer in camera.gameObject.scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<SkinnedMeshRenderer>(true)).Where(r=>r.enabled&&r.gameObject.activeInHierarchy))
            {
                var mesh=new Mesh();var draw=new GameObject("Transient pose snapshot");
                snapshots.Add((renderer,draw,mesh));renderer.BakeMesh(mesh);draw.transform.SetParent(renderer.transform,false);
                draw.AddComponent<MeshFilter>().sharedMesh=mesh;draw.AddComponent<MeshRenderer>().sharedMaterials=renderer.sharedMaterials;
                renderer.enabled=false;
            }
            rt.Create();camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
            image=new Texture2D(640,640,TextureFormat.RGBA32,false);image.ReadPixels(new Rect(0,0,640,640),0,0);image.Apply();
            File.WriteAllBytes(path,image.EncodeToPNG());var pixels=image.GetPixels32();var bg=pixels[0];
            return new JObject{["image"]=path,["changedPixels"]=pixels.Count(p=>Math.Abs(p.r-bg.r)>2||Math.Abs(p.g-bg.g)>2||Math.Abs(p.b-bg.b)>2),
                ["magentaPixels"]=pixels.Count(p=>p.r>240&&p.b>240&&p.g<20)};
        }
        finally
        {
            foreach(var snapshot in snapshots){snapshot.source.enabled=true;Object.DestroyImmediate(snapshot.draw);Object.DestroyImmediate(snapshot.mesh);}
            camera.targetTexture=old;RenderTexture.active=active;if(image!=null)Object.DestroyImmediate(image);rt.Release();Object.DestroyImmediate(rt);
        }
    }
}
