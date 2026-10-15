using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

[McpForUnityTool("build_static_shader_sample", Description="Build and render a static Shader sample in an additive scene; preserve the active user scene.")]
public static class StaticShaderSampleMcpTool
{
    public class Parameters
    {
        [ToolParameter("build or audit", Required=false)] public string action {get;set;}
        [ToolParameter("Absolute sample.json path", Required=false)] public string sample {get;set;}
        [ToolParameter("Destination Assets-relative folder", Required=true)] public string output {get;set;}
        [ToolParameter("Absolute validation report path", Required=false)] public string report {get;set;}
    }

    public static object HandleCommand(JObject parameters)
    {
        try
        {
            if ((string)parameters["action"] == "audit")
                return new SuccessResponse("Sample assets audited.",StaticShaderSampleBuilder.Audit((string)parameters["output"]));
            StaticShaderSampleBuilder.Build((string)parameters["sample"], (string)parameters["output"], (string)parameters["report"]);
            return new SuccessResponse("Sample built; active user scene preserved.", new {report=(string)parameters["report"]});
        }
        catch (Exception error)
        {
            return new ErrorResponse(error.Message);
        }
    }
}
