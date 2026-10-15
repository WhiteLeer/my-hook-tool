using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

[McpForUnityTool("build_static_particle_sample", Description="Build and render an audited particle-only sample without modifying shaders or the active scene.")]
public static class StaticParticleSampleMcpTool
{
    public class Parameters
    {
        [ToolParameter("build or checks", Required=false)] public string action {get;set;}
        [ToolParameter("Absolute audited preview plan path", Required=true)] public string plan {get;set;}
        [ToolParameter("New Assets-relative output folder", Required=true)] public string output {get;set;}
        [ToolParameter("Absolute external report path", Required=true)] public string report {get;set;}
    }

    public static object HandleCommand(JObject parameters)
    {
        try
        {
            if((string)parameters["action"]=="curve_contracts")
                return new SuccessResponse("Actual Unity curve serialization.",StaticParticleSampleBuilder.CurveContracts());
            if ((string)parameters["action"] == "checks")
                return new SuccessResponse("Source-plan corruption checks passed.", StaticParticleSampleBuilder.SafetyChecks((string)parameters["plan"]));
            return new SuccessResponse("Audited particle preview built.", StaticParticleSampleBuilder.Build(
                (string)parameters["plan"], (string)parameters["output"], (string)parameters["report"]));
        }
        catch (Exception e) { return new ErrorResponse(e.ToString()); }
    }
}
