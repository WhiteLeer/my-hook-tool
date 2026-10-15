"""Export one real Draw's evidence through the RenderDoc MCP stdio server."""

import argparse
import hashlib
import json
import queue
import shutil
import subprocess
import threading
from pathlib import Path


class RenderDocSession:
    def __init__(self, executable, log_path):
        self.log = log_path.open("w", encoding="utf-8")
        self.process = subprocess.Popen(
            [str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=self.log, text=True, encoding="utf-8", bufsize=1,
        )
        self.responses = queue.Queue()
        self.sequence = 0
        threading.Thread(target=self._read, daemon=True).start()
        try:
            self.request("initialize", {
                "protocolVersion": "2025-03-26", "capabilities": {},
                "clientInfo": {"name": "my-hook-tool-evidence", "version": "1"},
            })
        except Exception:
            self.process.kill()
            self.process.wait()
            self.log.close()
            raise
        self.notify("notifications/initialized", {})

    def _read(self):
        for line in self.process.stdout:
            try:
                self.responses.put(json.loads(line))
            except json.JSONDecodeError:
                self.log.write(line)
        self.responses.put({"error": {"message": "RenderDoc MCP process exited"}})

    def notify(self, method, params):
        self.process.stdin.write(json.dumps({
            "jsonrpc": "2.0", "method": method, "params": params,
        }) + "\n")
        self.process.stdin.flush()

    def request(self, method, params):
        self.sequence += 1
        request_id = self.sequence
        self.process.stdin.write(json.dumps({
            "jsonrpc": "2.0", "id": request_id, "method": method, "params": params,
        }) + "\n")
        self.process.stdin.flush()
        while True:
            response = self.responses.get(timeout=60)
            if "error" in response:
                raise RuntimeError(response["error"])
            if response.get("id") == request_id:
                return response["result"]

    def tool(self, name, arguments):
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        if result.get("isError"):
            raise RuntimeError(result.get("content"))
        if "structuredContent" in result:
            return result["structuredContent"]
        texts = [x["text"] for x in result.get("content", []) if x.get("type") == "text"]
        return json.loads("\n".join(texts))

    def close(self):
        try:
            self.tool("close_capture", {})
        finally:
            self.process.stdin.close()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
            self.log.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--capture", type=Path, required=True)
    parser.add_argument("--event", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--manifest", type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    report = {
        "schema": "my-hook.draw-evidence.v1", "capture": str(args.capture.resolve()),
        "eventId": args.event, "status": "collecting", "errors": [],
        "identityPolicy": "Capture-local evidence only; no Unity Material identity inferred.",
    }
    session = RenderDocSession(args.server.resolve(), output / "mcp.log")
    try:
        report["captureInfo"] = session.tool("open_capture", {"path": str(args.capture.resolve())})
        session.tool("goto_event", {"eventId": args.event})
        report["pipeline"] = session.tool("get_pipeline_state", {"eventId": args.event})
        report["bindings"] = session.tool("get_bindings", {"eventId": args.event})
        report["stages"] = {}
        for stage in ("vs", "ps"):
            reflection = session.tool("get_shader", {
                "eventId": args.event, "stage": stage, "mode": "reflect",
            })
            listing = session.tool("list_cbuffers", {"eventId": args.event, "stage": stage})
            buffers = []
            for block in listing["cbuffers"]:
                contents = session.tool("get_cbuffer_contents", {
                    "eventId": args.event, "stage": stage, "index": block["index"],
                })
                buffers.append({"metadata": block, "contents": contents})
            report["stages"][stage] = {"reflection": reflection, "constantBuffers": buffers}
        report["exports"] = []
        if args.manifest:
            manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
            event = next(x for x in manifest["events"] if x["eventId"] == args.event)
            images = {x["resource"] for x in event["bindings"] if "textureDimension" in x}
            for resource in sorted(images):
                result = session.tool("export_texture", {"resourceId": resource})
                exported = Path(result["path"])
                target = output / exported.name
                if exported.resolve() != target.resolve():
                    shutil.copyfile(exported, target)
                report["exports"].append({
                    "kind": "texture", "resourceId": resource, "file": target.name,
                    "sha256": hashlib.sha256(target.read_bytes()).hexdigest(),
                })
        for name, params in (
            ("export_render_target", {"index": 0}),
            ("export_mesh", {"eventId": args.event, "format": "json", "stage": "vs-out"}),
        ):
            try:
                report[name] = session.tool(name, params)
            except (RuntimeError, KeyError) as error:
                report["errors"].append({"tool": name, "message": str(error)})
        report["status"] = "partial" if report["errors"] else "collected"
    finally:
        try:
            session.close()
        finally:
            (output / "draw-evidence.json").write_text(
                json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8",
            )
    print(json.dumps({"status": report["status"], "eventId": args.event,
                      "output": str(output), "errors": report["errors"]}, ensure_ascii=False))
    return 2 if report["errors"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
