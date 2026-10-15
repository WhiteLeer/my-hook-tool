using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Object=UnityEngine.Object;

// Separate editor ticks let native GPU skinning advance; no BakeMesh or material substitution here.
public static class StaticCharacterLiveValidation
{
    private static bool running;
    public static object Start(string scenePath,string reportPath,bool autoLoop=false)
    {
        if(running||EditorApplication.isPlaying||EditorApplication.isCompiling)throw new InvalidOperationException("Editor must be idle; only one live validation at a time.");
        var previous=SceneManager.GetActiveScene();
        if(Enumerable.Range(0,SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Any(s=>s.path==scenePath))throw new InvalidOperationException("Preview is already open; refusing to modify user's instance.");
        ulong occupied=SceneCullingMasks.DefaultSceneCullingMask;
        for(int i=0;i<SceneManager.sceneCount;i++)occupied|=EditorSceneManager.GetSceneCullingMask(SceneManager.GetSceneAt(i));
        ulong mask=Enumerable.Range(0,64).Select(i=>1UL<<i).First(x=>(occupied&x)==0);
        var scene=EditorSceneManager.OpenScene(scenePath,OpenSceneMode.Additive);
        SceneManager.SetActiveScene(scene);EditorSceneManager.SetSceneCullingMask(scene,mask);
        var roots=scene.GetRootGameObjects();
        var clock=roots.Select(g=>g.GetComponent<StaticCharacterMotionPreview>()).Single(c=>c!=null);
        clock.animate=autoLoop;
        var camera=roots.Select(g=>g.GetComponent<Camera>()).Single(c=>c!=null);
        camera.enabled=false;camera.overrideSceneCullingMask=mask;
        int phase=0,ticks=0;var start=EditorApplication.timeSinceStartup;var frames=new JArray();
        float[] times={0,1,2,4};
        File.WriteAllText(reportPath,new JObject{["status"]="running",["scene"]=scenePath}.ToString());running=true;
        EditorApplication.CallbackFunction update=null;
        update=()=>
        {
            try
            {
                if(EditorApplication.timeSinceStartup-start>40)throw new TimeoutException("Native scene validation exceeded 40 seconds.");
                if(autoLoop)
                {
                    double[] schedule={0.1,1.0,clock.clip.length+0.1,clock.clip.length+1.0};
                    if(EditorApplication.timeSinceStartup-start<schedule[phase]){EditorApplication.QueuePlayerLoopUpdate();return;}
                }
                if(ticks==0&&!autoLoop)clock.Sample(times[phase]);
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                if(++ticks<4)return;
                string suffix=autoLoop?"-frame"+phase:"-t"+times[phase];
                string path=Path.Combine(Path.GetDirectoryName(reportPath),Path.GetFileNameWithoutExtension(reportPath)+suffix+".png");
                Capture(camera,path);frames.Add(new JObject{["time"]=clock.CurrentSampleTime,["image"]=path,["sha256"]=Hash(path)});
                ticks=0;
                if(++phase<times.Length)return;
                bool changed=frames.Select(f=>(string)f["sha256"]).Distinct().Count()>1;
                bool wrapped=frames.Zip(frames.Skip(1),(a,b)=>(float)b["time"]<(float)a["time"]).Any(x=>x);
                File.WriteAllText(reportPath,new JObject{["status"]=changed&&(!autoLoop||wrapped)?"native_smr_motion_observed":"failed_motion_or_loop",["frames"]=frames,
                    ["noCpuBake"]=true,["autoLoop"]=autoLoop,["loopWrapObserved"]=wrapped,["elapsedSeconds"]=EditorApplication.timeSinceStartup-start}.ToString());
                Finish();
            }
            catch(Exception e)
            {
                File.WriteAllText(reportPath,new JObject{["status"]="failed",["error"]=e.ToString(),["frames"]=frames}.ToString());Finish();
            }
        };
        void Finish()
        {
            EditorApplication.update-=update;running=false;
            if(scene.IsValid()&&scene.isLoaded)EditorSceneManager.CloseScene(scene,true);
            if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);
        }
        EditorApplication.update+=update;
        return new {status="running",report=reportPath};
    }
    private static string Hash(string path)
    {
        using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-","").ToLowerInvariant();
    }
    private static void Capture(Camera camera,string path)
    {
        var rt=RenderTexture.GetTemporary(640,640,24,RenderTextureFormat.ARGB32);
        var previous=RenderTexture.active;Texture2D image=null;
        try
        {
            camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;
            image=new Texture2D(640,640,TextureFormat.RGBA32,false);image.ReadPixels(new Rect(0,0,640,640),0,0);image.Apply();File.WriteAllBytes(path,image.EncodeToPNG());
        }
        finally{camera.targetTexture=null;RenderTexture.active=previous;if(image!=null)Object.DestroyImmediate(image);RenderTexture.ReleaseTemporary(rt);}
    }
}
