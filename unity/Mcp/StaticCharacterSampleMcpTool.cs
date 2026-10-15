using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using System.IO;
using UnityEditor;

[McpForUnityTool("build_static_character_sample",Description="Schedule isolated character validation; read the returned job file for completion. Preserve user's active scene.")]
public static class StaticCharacterSampleMcpTool
{
    private static bool running;
    public class Parameters
    {
        [ToolParameter("Absolute audited plan",Required=true)]public string plan{get;set;}
        [ToolParameter("Versioned Assets-relative destination",Required=true)]public string output{get;set;}
        [ToolParameter("External validation report",Required=true)]public string report{get;set;}
    }
    public static object HandleCommand(JObject p)
    {
        try
        {
            if(running)throw new InvalidOperationException("Character build already running.");
            string plan=(string)p["plan"],output=(string)p["output"],report=(string)p["report"],job=report+".job.json";
            File.WriteAllText(job,new JObject{["status"]="queued"}.ToString());running=true;
            EditorApplication.CallbackFunction build=null;
            build=()=>
            {
                EditorApplication.update-=build;
                try
                {
                    File.WriteAllText(job,new JObject{["status"]="running"}.ToString());
                    var result=StaticCharacterSampleBuilder.Build(plan,output,report);
                    File.WriteAllText(job,new JObject{["status"]="complete",["result"]=JToken.FromObject(result)}.ToString());
                }
                catch(Exception e){File.WriteAllText(job,new JObject{["status"]="failed",["error"]=e.ToString()}.ToString());}
                finally{running=false;}
            };
            EditorApplication.update+=build;EditorApplication.QueuePlayerLoopUpdate();
            return new SuccessResponse("Character build scheduled.",new {status="queued",jobFile=job});
        }
        catch(Exception e){return new ErrorResponse(e.ToString());}
    }
}

[McpForUnityTool("audit_static_character_sample",Description="Offscreen transient source-shader versus neutral geometry control; no persistent asset edits.")]
public static class StaticCharacterSampleAuditMcpTool
{
    public static object HandleCommand(JObject p)
    {
        try{return new SuccessResponse("Character geometry control recorded.",StaticCharacterSampleAudit.Audit((string)p["prefab"],(string)p["report"],(string)p["plan"],p["overrides"] as JObject));}
        catch(Exception e){return new ErrorResponse(e.ToString());}
    }
}

[McpForUnityTool("validate_native_character_motion",Description="Asynchronously verify native SkinnedMeshRenderer motion across separate editor updates, without CPU bake.")]
public static class StaticCharacterLiveValidationMcpTool
{
    public static object HandleCommand(JObject p)
    {
        try{return new SuccessResponse("Native character validation scheduled.",StaticCharacterLiveValidation.Start((string)p["scene"],(string)p["report"],(bool?)p["autoLoop"]??false));}
        catch(Exception e){return new ErrorResponse(e.ToString());}
    }
}
