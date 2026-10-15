"""Import original-pass character materials through the existing Unity MCP boundary."""
import asyncio
import json
from pathlib import Path
from unity_mcp_call import require_success

ROOT=Path(__file__).resolve().parents[1]
C=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/character-preview-source'


async def main():
    from mcp import ClientSession
    from mcp.client.streamable_http import streamable_http_client
    async with streamable_http_client('http://127.0.0.1:8080/mcp') as (reader,writer,_):
        async with ClientSession(reader,writer) as session:
            await session.initialize()
            require_success(await session.call_tool('set_active_instance',{'instance':'unity-extraction-validation@4c617de124be1c72'}))
            materials={}
            for suffix,folder in [('Body_S','BodyS'),('Body_D','BodyD'),('Weapon','Weapon'),('Hair','Hair'),('Face','Face')]:
                name='Hyacine_'+('00' if suffix=='Face' else '01')+'_Mat_'+suffix
                sample=C/'reflection-samples'/name/'sample.json'
                output='Assets/RenderDocShaderSamples/HSR-4.6/HyacineCharacter/Materials/'+folder
                report=C/(folder+'-material-unity.json')
                result=await session.call_tool('execute_custom_tool',{'tool_name':'build_static_shader_sample',
                    'parameters':{'sample':str(sample),'output':output,'report':str(report)}})
                (C/(folder+'-material-mcp.json')).write_text(result.model_dump_json(),encoding='utf-8')
                require_success(result)
                data=json.loads(sample.read_text());key=data['sourceMaterialFile']+':'+str(data['sourceMaterialPathId'])
                materials[key]=dict(asset=output+'/'+name+'.mat',sample=str(sample),sourceName=name)
                print(name,'compiled material; mesh validation pending',flush=True)
            (C/'unity-material-bindings.json').write_text(json.dumps(materials,indent=2),encoding='utf-8')


if __name__=='__main__':asyncio.run(main())
