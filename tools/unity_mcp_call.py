"""Use an existing local Unity MCP endpoint without changing Codex or editor settings."""

import argparse
import asyncio
import json

def require_success(result):
    data = result.model_dump()
    if data.get("isError"):
        raise RuntimeError("MCP tool returned an error")
    for block in data.get("content", []):
        if block.get("type") != "text":
            continue
        try:
            payload = json.loads(block.get("text", ""))
        except (ValueError, TypeError):
            continue
        if isinstance(payload, dict) and payload.get("success") is False:
            raise RuntimeError("MCP operation failed: " + str(payload.get("error", payload.get("message", "unknown error"))))


async def main(args):
    from mcp import ClientSession
    from mcp.client.streamable_http import streamable_http_client

    async with streamable_http_client(args.url) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            if args.instance:
                require_success(await session.call_tool("set_active_instance", {"instance": args.instance}))
            if args.list_tools:
                result = await session.list_tools()
                tools = result.tools
                if args.filter:
                    tools = [x for x in tools if args.filter.lower() in x.name.lower()]
                print(json.dumps([x.model_dump() for x in tools], ensure_ascii=False))
            elif args.list_resources:
                print((await session.list_resources()).model_dump_json())
            elif args.resource:
                print((await session.read_resource(args.resource)).model_dump_json())
            else:
                result = await session.call_tool(args.tool, json.loads(args.arguments))
                # Image payloads are intentionally not printed as base64 blobs.
                data = result.model_dump()
                for block in data.get("content", []):
                    if block.get("type") in ("image", "audio"):
                        block.pop("data", None)
                print(json.dumps(data, ensure_ascii=False))
                require_success(result)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True)
    parser.add_argument("--list-tools", action="store_true")
    parser.add_argument("--list-resources", action="store_true")
    parser.add_argument("--filter")
    parser.add_argument("--instance")
    parser.add_argument("--resource")
    parser.add_argument("--tool")
    parser.add_argument("--arguments", default="{}")
    args = parser.parse_args()
    if not (args.list_tools or args.list_resources or args.resource or args.tool):
        parser.error("Specify a read operation or a tool")
    asyncio.run(main(args))
