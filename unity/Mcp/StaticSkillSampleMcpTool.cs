using System;
using System.IO;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using UnityEditor;

[McpForUnityTool("build_static_skill_sample", Description="Schedule a bounded source-backed skill preview; preserve the user's open scene. Read the external job file.")]
public static class StaticSkillSampleMcpTool
{
    private static bool running;
    public static object HandleCommand(JObject p)
    {
        if (running) return new ErrorResponse("A skill preview build is running.");
        string plan = (string)p["plan"], output = (string)p["output"], report = (string)p["report"], job = report + ".job.json";
        File.WriteAllText(job, new JObject { ["status"] = "queued" }.ToString()); running = true;
        EditorApplication.CallbackFunction build = null;
        build = () =>
        {
            EditorApplication.update -= build;
            try
            {
                File.WriteAllText(job, new JObject { ["status"] = "running" }.ToString());
                var result = StaticSkillSampleBuilder.Build(plan, output, report);
                File.WriteAllText(job, new JObject { ["status"] = "complete", ["result"] = JToken.FromObject(result) }.ToString());
            }
            catch (Exception e) { File.WriteAllText(job, new JObject { ["status"] = "failed", ["error"] = e.ToString() }.ToString()); }
            finally { running = false; }
        };
        EditorApplication.update += build; EditorApplication.QueuePlayerLoopUpdate();
        return new SuccessResponse("Skill preview scheduled.", new { jobFile = job });
    }
}
