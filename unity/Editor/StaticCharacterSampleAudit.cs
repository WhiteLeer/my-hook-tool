using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Object=UnityEngine.Object;

// Transient geometry control: never replace the source materials in saved assets.
public static class StaticCharacterSampleAudit
{
    public static object Audit(string prefabPath,string reportPath,string planPath,JObject overrides)
    {
        if(EditorApplication.isPlaying||EditorApplication.isCompiling)throw new InvalidOperationException("Editor must be idle.");
        var previous=SceneManager.GetActiveScene();
        ulong occupied=SceneCullingMasks.DefaultSceneCullingMask;
        for(int i=0;i<SceneManager.sceneCount;i++)occupied|=EditorSceneManager.GetSceneCullingMask(SceneManager.GetSceneAt(i));
        ulong mask=Enumerable.Range(0,64).Select(i=>1UL<<i).First(x=>(occupied&x)==0);
        var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
        var neutral=new Material(Shader.Find("Unlit/Color")){color=Color.gray};
        var trials=new System.Collections.Generic.List<Material>();
        try
        {
            SceneManager.SetActiveScene(scene);EditorSceneManager.SetSceneCullingMask(scene,mask);
            var actor=(GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath),scene);
            var clock=actor.GetComponent<StaticCharacterMotionPreview>();clock.animate=false;
            var renderers=actor.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var materials=renderers.Select(r=>r.sharedMaterials.Select(m=>
            {
                if(overrides==null||!overrides.HasValues)return m;
                var trial=new Material(m);trials.Add(trial);
                foreach(var property in overrides.Properties())
                {
                    if(!trial.HasProperty(property.Name))continue;
                    var v=property.Value;
                    if(v is JArray)trial.SetVector(property.Name,new Vector4((float)v[0],(float)v[1],(float)v[2],(float)v[3]));
                    else trial.SetFloat(property.Name,(float)v);
                }
                return trial;
            }).ToArray()).ToArray();
            var camera=new GameObject("Geometry control camera").AddComponent<Camera>();
            camera.enabled=false;camera.orthographic=true;camera.orthographicSize=1.2f;
            camera.transform.position=new Vector3(0,.9f,4);camera.transform.LookAt(new Vector3(0,.9f,0));
            camera.overrideSceneCullingMask=mask;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.035f,.045f,.07f,1);
            camera.nearClipPlane=.01f;camera.farClipPlane=100;camera.allowHDR=false;camera.allowMSAA=false;
            var frames=new JArray();
            var nonTransform=new JArray(AnimationUtility.GetCurveBindings(clock.clip).Where(b=>b.type!=typeof(Transform)).Select(b=>new JObject{
                ["path"]=b.path,["type"]=b.type.FullName,["property"]=b.propertyName,["value0"]=AnimationUtility.GetEditorCurve(clock.clip,b).Evaluate(0),["value1"]=AnimationUtility.GetEditorCurve(clock.clip,b).Evaluate(1)}));
            var plan=JObject.Parse(File.ReadAllText(planPath));
            foreach(float time in new[]{-1f,0f,1f,2f,4f})
            {
                foreach(JObject trs in (JArray)plan["sourceTransforms"])
                {
                    var t=(string)trs["path"]==""?actor.transform:actor.transform.Find((string)trs["path"]);
                    JToken p=trs["position"],q=trs["rotation"],s=trs["scale"];
                    t.localPosition=new Vector3((float)p[0],(float)p[1],(float)p[2]);
                    t.localRotation=new Quaternion((float)q[0],(float)q[1],(float)q[2],(float)q[3]);
                    t.localScale=new Vector3((float)s[0],(float)s[1],(float)s[2]);
                }
                if(time>=0)clock.Sample(time);var checks=new JArray();
                foreach(var renderer in renderers)
                {
                    if(!renderer.enabled)continue;
                    var baked=new Mesh();renderer.BakeMesh(baked);var block=new MaterialPropertyBlock();renderer.GetPropertyBlock(block);
                    var skinErrors=new JArray();
                    for(int i=0;i<renderer.bones.Length;i++)
                    {
                        var skin=renderer.transform.worldToLocalMatrix*renderer.bones[i].localToWorldMatrix*renderer.sharedMesh.bindposes[i];
                        float error=0;for(int j=0;j<16;j++)error=Mathf.Max(error,Mathf.Abs(skin[j]-Matrix4x4.identity[j]));
                        skinErrors.Add(new JObject{["bone"]=renderer.bones[i].name,["bindIdentityError"]=error,["localPosition"]=renderer.bones[i].localPosition.ToString("F5")});
                    }
                    checks.Add(new JObject{["name"]=renderer.name,["meshBounds"]=renderer.sharedMesh.bounds.ToString("F5"),["skinErrors"]=skinErrors,
                        ["meshAsset"]=AssetDatabase.GetAssetPath(renderer.sharedMesh),["propertyBlockEmpty"]=block.isEmpty,["blockShowPart"]=block.GetFloat("_ShowPartID"),
                        ["vertexColorFormat"]=renderer.sharedMesh.GetVertexAttributeFormat(UnityEngine.Rendering.VertexAttribute.Color).ToString(),
                        ["vertexColorGreens"]=new JArray(renderer.sharedMesh.colors32.Select(c=>(int)c.g).Distinct()),
                        ["materialScalars"]=new JArray(renderer.sharedMaterials.Select(m=>new JObject{["name"]=m.name,
                            ["showPart"]=m.HasProperty("_ShowPartID")?m.GetFloat("_ShowPartID"):-999,["hideParts"]=m.HasProperty("_HideCharaParts")?m.GetFloat("_HideCharaParts"):-999})),
                        ["bakedBounds"]=baked.bounds.ToString("F5"),["worldBounds"]=renderer.bounds.ToString("F5"),
                        ["rendererScale"]=renderer.transform.lossyScale.ToString("F5"),
                        ["firstBoneScale"]=renderer.bones[0].lossyScale.ToString("F5")});
                    Object.DestroyImmediate(baked);
                }
                var frame=new JObject{["time"]=time,["renderers"]=checks};
                foreach(bool control in new[]{false,true})
                {
                    for(int i=0;i<renderers.Length;i++)renderers[i].sharedMaterials=control?materials[i].Select(_=>neutral).ToArray():materials[i];
                    string image=Path.Combine(Path.GetDirectoryName(reportPath),(control?"geometry-control-t":"source-shader-t")+time+".png");
                    Capture(camera,image);frame[control?"geometryControl":"sourceShader"]=image;
                }
                frames.Add(frame);
            }
            File.WriteAllText(reportPath,new JObject{["prefab"]=prefabPath,["frames"]=frames,["nonTransformCurves"]=nonTransform,["controlIsNotReconstruction"]=true,["trialOverrides"]=overrides}.ToString());
            return new {report=reportPath};
        }
        finally
        {
            Object.DestroyImmediate(neutral);
            foreach(var m in trials)Object.DestroyImmediate(m);
            EditorSceneManager.CloseScene(scene,true);
            if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);
        }
    }
    private static void Capture(Camera camera,string path)
    {
        var rt=RenderTexture.GetTemporary(512,512,24,RenderTextureFormat.ARGB32);
        var previous=RenderTexture.active;var old=camera.targetTexture;Texture2D image=null;
        try
        {
            camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
            image=new Texture2D(512,512,TextureFormat.RGBA32,false);image.ReadPixels(new Rect(0,0,512,512),0,0);image.Apply();
            File.WriteAllBytes(path,image.EncodeToPNG());
        }
        finally{camera.targetTexture=old;RenderTexture.active=previous;if(image!=null)Object.DestroyImmediate(image);RenderTexture.ReleaseTemporary(rt);}
    }
}
