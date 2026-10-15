"""Run one isolated character validation and retain verbose MCP failures outside Unity Assets."""
import asyncio
import json
import time
from pathlib import Path
from unity_mcp_call import require_success

ROOT=Path(__file__).resolve().parents[1]
C=ROOT/'artifacts/StaticShaderSamples/HSR-4.6/character-preview-source'


async def main():
    from mcp import ClientSession
    from mcp.client.streamable_http import streamable_http_client
    success=True
    async with streamable_http_client('http://127.0.0.1:8080/mcp') as (reader,writer,_):
        async with ClientSession(reader,writer) as session:
            await session.initialize()
            require_success(await session.call_tool('set_active_instance',{'instance':'unity-extraction-validation@4c617de124be1c72'}))
            result=await session.call_tool('execute_custom_tool',{'tool_name':'build_static_character_sample','parameters':{
                'plan':str(C/'character-plan.json'),'output':'Assets/RenderDocShaderSamples/HSR-4.6/HyacineCharacter',
                'report':str(C/'character-unity.json')}})
            (C/'character-build-mcp.json').write_text(result.model_dump_json(),encoding='utf-8')
            try:require_success(result)
            except RuntimeError as e:print(str(e)[:1100]);success=False
            if success:
                job=C/'character-unity.json.job.json';deadline=time.monotonic()+330
                while time.monotonic()<deadline:
                    try:data=json.loads(job.read_text(encoding='utf-8-sig'))
                    except (FileNotFoundError,json.JSONDecodeError):data={}
                    if data.get('status') in ('complete','failed'):
                        print(json.dumps(data,ensure_ascii=False));success=data['status']=='complete';break
                    await asyncio.sleep(1)
                else:print('Character job did not finish within 330 seconds; inspect the job file before retrying.');success=False
    return success


if __name__=='__main__':raise SystemExit(0 if asyncio.run(main()) else 1)
