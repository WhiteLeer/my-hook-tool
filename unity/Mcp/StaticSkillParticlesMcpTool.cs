using System;
using System.IO;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;

[McpForUnityTool("build_static_skill_particles",Description="Build and roundtrip-check each source-backed particle subtree independently. Read the external report.")]
public static class StaticSkillParticlesMcpTool
{
    private static bool running;
    public static object HandleCommand(JObject p)
    {
        if(running)return new ErrorResponse("Particle job running.");
        var jobs=JArray.Parse(File.ReadAllText((string)p["jobs"]));string report=(string)p["report"];
        int index=0;var results=new JArray();running=true;
        File.WriteAllText(report,new JObject{["status"]="queued"}.ToString());
        EditorApplication.CallbackFunction step=null;
        step=()=>
        {
            if(index>=jobs.Count)
            {
                EditorApplication.update-=step;running=false;
                File.WriteAllText(report,new JObject{["status"]="complete",["results"]=results}.ToString());return;
            }
            var job=jobs[index++];
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName((string)job["report"]));
                var result=StaticParticleSampleBuilder.Build((string)job["plan"],(string)job["output"],(string)job["report"]);
                results.Add(new JObject{["status"]="complete",["job"]=job,["result"]=JToken.FromObject(result)});
            }
            catch(Exception error){results.Add(new JObject{["status"]="failed",["job"]=job,["error"]=error.ToString()});}
            File.WriteAllText(report,new JObject{["status"]="running",["completed"]=index,["total"]=jobs.Count,["results"]=results}.ToString());
            EditorApplication.QueuePlayerLoopUpdate();
        };
        EditorApplication.update+=step;EditorApplication.QueuePlayerLoopUpdate();
        return new SuccessResponse("Independent particle validation scheduled.",new{jobFile=report});
    }
}
