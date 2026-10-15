using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEditor;

public static class StaticSkillResourceBuilder
{
    private static string Hash(string path)
    {
        using var sha=SHA256.Create();using var stream=File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
    }
    private static Vector3 V3(JToken v)=>new Vector3((float)v[0],(float)v[1],(float)v[2]);
    public static void Meshes(JObject plan)
    {
        var results=new JArray();
        foreach(JObject source in (JArray)plan["meshes"])
        {
            if(Hash((string)source["sourceFile"])!=(string)source["sourceFileSha256"])throw new InvalidDataException("Native mesh source changed.");
            string path=(string)source["asset"];
            if(!path.StartsWith("Assets/RenderDocShaderSamples/HSR-4.6/HyacineSkill01/Model/EffectMeshes/")||path.Split('/').Contains(".."))throw new InvalidDataException("Unsafe output.");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var data=(JObject)source["data"];
            var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);bool create=mesh==null;
            if(create)mesh=new Mesh();mesh.Clear(false);mesh.name=(string)data["name"];
            mesh.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices=((JArray)data["vertices"]).Select(V3).ToArray();
            if(((JArray)data["normals"]).Count>0)mesh.normals=((JArray)data["normals"]).Select(V3).ToArray();
            if(((JArray)data["tangents"]).Count>0)mesh.tangents=((JArray)data["tangents"]).Select(v=>new Vector4((float)v[0],(float)v[1],(float)v[2],(float)v[3])).ToArray();
            if(((JArray)data["colors"]).Count>0)
            {
                if((int)data["colorFormat"]!=2)throw new InvalidDataException("Unsupported native vertex-color storage.");
                mesh.colors32=((JArray)data["colors"]).Select(v=>new Color32(checked((byte)(int)v[0]),checked((byte)(int)v[1]),checked((byte)(int)v[2]),v.Count()>3?checked((byte)(int)v[3]):(byte)255)).ToArray();
            }
            for(int i=0;i<8;i++)
            {
                var uv=(JArray)data["uvs"][i];if(uv.Count==0)continue;int dim=uv[0].Count();
                if(dim==2)mesh.SetUVs(i,uv.Select(v=>new Vector2((float)v[0],(float)v[1])).ToList());
                else if(dim==3)mesh.SetUVs(i,uv.Select(V3).ToList());
                else if(dim==4)mesh.SetUVs(i,uv.Select(v=>new Vector4((float)v[0],(float)v[1],(float)v[2],(float)v[3])).ToList());
                else throw new InvalidDataException("Unsupported native UV width.");
            }
            var indices=(JArray)data["triangles"];mesh.subMeshCount=indices.Count;
            for(int i=0;i<indices.Count;i++)mesh.SetIndices(((JArray)indices[i]).Values<int>().ToArray(),MeshTopology.Triangles,i,false);
            mesh.RecalculateBounds();
            if(create)AssetDatabase.CreateAsset(mesh,path);
            EditorUtility.SetDirty(mesh);AssetDatabase.SaveAssetIfDirty(mesh);
            results.Add(new JObject{["key"]=source["key"],["asset"]=path,["assetSha256"]=Hash(path),["sourceObjectSha256"]=source["sourceObjectSha256"],["vertexCount"]=mesh.vertexCount});
        }
        File.WriteAllText((string)plan["meshReport"],results.ToString());
    }
}
