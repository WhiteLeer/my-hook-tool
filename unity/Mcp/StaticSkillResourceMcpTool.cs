using System;
using System.IO;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;

[McpForUnityTool("build_static_skill_resources",Description="Build audited native particle meshes and original-program materials one at a time. Read the external job file.")]
public static class StaticSkillResourceMcpTool
{
    private static bool running;
    public static object HandleCommand(JObject parameters)
    {
        if(running)return new ErrorResponse("Resource job running.");
        var plan=JObject.Parse(File.ReadAllText((string)parameters["plan"]));
        string job=(string)plan["jobReport"];
        File.WriteAllText(job,new JObject{["status"]="queued"}.ToString());running=true;
        int index=-1;var results=new JArray();EditorApplication.CallbackFunction step=null;
        step=()=>
        {
            try
            {
                if(EditorApplication.isCompiling||EditorApplication.isPlaying)throw new InvalidOperationException("Editor must be idle.");
                if(index==-1){StaticSkillResourceBuilder.Meshes(plan);index=0;}
                else if(index<((JArray)plan["materials"]).Count)
                {
                    var material=plan["materials"][index++];string report=(string)material["report"];
                    StaticShaderSampleBuilder.Build((string)material["source"],(string)material["output"],report);
                    results.Add(new JObject{["output"]=material["output"],["report"]=report});
                }
                else
                {
                    EditorApplication.update-=step;running=false;
                    File.WriteAllText(job,new JObject{["status"]="complete",["results"]=results}.ToString());return;
                }
                File.WriteAllText(job,new JObject{["status"]="running",["completed"]=index,["total"]=((JArray)plan["materials"]).Count}.ToString());
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch(Exception error)
            {
                EditorApplication.update-=step;running=false;
                File.WriteAllText(job,new JObject{["status"]="failed",["completed"]=index,["error"]=error.ToString()}.ToString());
            }
        };
        EditorApplication.update+=step;EditorApplication.QueuePlayerLoopUpdate();
        return new SuccessResponse("Skill resources scheduled.",new{jobFile=job});
    }
}
