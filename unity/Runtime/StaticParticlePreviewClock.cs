using System.Collections.Generic;
using UnityEngine;

public static class StaticParticlePreviewClock
{
    public static void Sample(ParticleSystem[] systems,float time,float shaderTime,uint seed)
    {
        var children=new HashSet<ParticleSystem>();
        var properties=new MaterialPropertyBlock();
        // Clear the graph first. Clearing a child after its parent runs destroys newly spawned subparticles.
        for(int i=0;i<systems.Length;i++)
        {
            var ps=systems[i];if(ps==null)continue;
            ps.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);
            if(ps.useAutoRandomSeed){ps.useAutoRandomSeed=false;ps.randomSeed=seed+(uint)i;}
            var sub=ps.subEmitters;
            if(sub.enabled)for(int n=0;n<sub.subEmittersCount;n++)children.Add(sub.GetSubEmitterSystem(n));
            var renderer=ps.GetComponent<ParticleSystemRenderer>();
            if(renderer!=null)
            {
                renderer.GetPropertyBlock(properties);properties.SetFloat("_PreviewTime",shaderTime);renderer.SetPropertyBlock(properties);properties.Clear();
            }
        }
        foreach(var ps in systems)
            if(ps!=null&&ps.gameObject.activeInHierarchy&&!children.Contains(ps))ps.Simulate(time,false,true,true);
    }
}
