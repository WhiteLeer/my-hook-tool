"""Build a frozen Unity sample from verified programs and one Draw's exact inputs."""

import argparse
import json
import re
import shutil
import struct
import subprocess
from pathlib import Path


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def freeze_block(body, data):
    declarations = []
    pattern = re.compile(r"\s*(float|uint|int)([1-4]?)\s+(\w+)(?:\[(\d+)\])?\s*:\s*packoffset\(c(\d+)(?:\.([xyzw]))?\);\s*")
    for line in body.splitlines():
        if not line.strip():
            continue
        match = pattern.fullmatch(line)
        if not match:
            raise ValueError("Unsupported captured cbuffer declaration: " + line)
        kind, width, name, array, slot, component = match.groups()
        width = int(width or 1)
        count = int(array or 1)
        offset = int(slot) * 16 + ("xyzw".index(component) * 4 if component else 0)
        values = []
        for element in range(count):
            start = offset + element * (16 if array else width * 4)
            if start + width * 4 > len(data):
                raise ValueError("Captured cbuffer is shorter than its declaration")
            code = {"float": "I", "uint": "I", "int": "i"}[kind]
            raw = struct.unpack_from("<" + code * width, data, start)
            if kind == "float":
                pieces = [f"0x{x:08x}u" for x in raw]
                expression = "asfloat(" + (pieces[0] if width == 1 else f"uint{width}(" + ", ".join(pieces) + ")") + ")"
            else:
                pieces = [str(x) + ("u" if kind == "uint" else "") for x in raw]
                expression = pieces[0] if width == 1 else f"{kind}{width}(" + ", ".join(pieces) + ")"
            values.append(expression)
        type_name = kind + (str(width) if width > 1 else "")
        suffix = f"[{count}]" if array else ""
        initializer = "{ " + ", ".join(values) + " }" if array else values[0]
        declarations.append(f"static const {type_name} {name}{suffix} = {initializer};")
    return "\n".join(declarations)


def verify_decoded_block(body, data, variables):
    declarations = [line.strip() for line in body.splitlines() if line.strip()]
    if len(declarations) != len(variables):
        raise ValueError("Decoded constant block member count differs from the raw program")
    checked = 0
    for declaration, variable in zip(declarations, variables):
        match = re.fullmatch(r"(float|uint|int)([1-4]?)\s+\w+(?:\[(\d+)\])?\s*:\s*packoffset\(c(\d+)(?:\.([xyzw]))?\);", declaration)
        if not match:
            raise ValueError("Unsupported declaration in constant evidence audit")
        kind, width, count, slot, component = match.groups()
        width, count = int(width or 1), int(count or 1)
        offset = int(slot) * 16 + ("xyzw".index(component) * 4 if component else 0)
        elements = variable.get("members", [variable])
        if len(elements) != count:
            raise ValueError("Decoded array length differs from the captured declaration")
        for index, element in enumerate(elements):
            values = element[{"float": "floatValues", "uint": "uintValues", "int": "intValues"}[kind]]
            decoded = struct.pack("<" + {"float": "f", "uint": "I", "int": "i"}[kind] * width, *values)
            start = offset + index * (16 if count > 1 else width * 4)
            if decoded != data[start:start + width * 4]:
                raise ValueError(f"Decoded cbuffer disagrees with raw bytes at offset {start}; check descriptor dynamic offsets")
            checked += width
    return checked


def stage_program(export, evidence, stage, cross, target):
    record = next(x for x in export["manifest"]["shaders"] if x["stage"] == stage)
    reflection = json.loads(subprocess.check_output(
        [str(cross), str(export["root"] / record["binary"]), "--reflect"], text=True,
    ))
    source = (export["root"] / record["reconstructedHlsl"]).read_text(encoding="utf-8")
    blocks = evidence["stages"]["vs" if stage == "vertex" else "ps"]["constantBuffers"]
    mappings = {}
    for block in reflection.get("ubos", []):
        matches = [x for x in blocks if x["metadata"]["bufferBacked"]
                   and x["metadata"]["bindSet"] == block["set"]
                   and x["metadata"]["bindSlot"] == block["binding"]]
        if len(matches) != 1:
            raise ValueError("No unique descriptor-backed cbuffer for " + block["name"])
        mappings[block["name"]] = matches[0]["metadata"]["index"]
    for block in reflection.get("push_constants", []):
        matches = [x for x in blocks if not x["metadata"]["bufferBacked"]
                   and x["metadata"]["byteSize"] > 0]
        if len(matches) != 1:
            raise ValueError("No unique push constant block")
        mappings[block["type"] + block["name"]] = matches[0]["metadata"]["index"]
    snapshots = {x["index"]: x for x in export["event"]["bindings"]
                 if x["stage"] == stage and x["typeName"] == "constantBuffer"}

    def replace_block(match):
        name, body = match.groups()
        if name not in mappings:
            raise ValueError("Unmapped HLSL cbuffer: " + name)
        binding = snapshots[mappings[name]]
        if "snapshot" not in binding:
            raise ValueError("Missing raw bytes for " + name)
        data = (export["root"] / binding["snapshot"]).read_bytes()
        decoded = next(x for x in blocks if x["metadata"]["index"] == mappings[name])
        export.setdefault("constantComponentsChecked", 0)
        export["constantComponentsChecked"] += verify_decoded_block(body, data, decoded["contents"]["variables"])
        return f"// Frozen captured block {name}; descriptor index {mappings[name]}.\n" + freeze_block(body, data)

    source = re.sub(r"cbuffer\s+(\w+)(?:\s*:\s*register\([^)]*\))?\s*\{(.*?)\};", replace_block, source, flags=re.S)
    definitions = []
    for constant in reflection.get("specialization_constants", []):
        values = [v for b in blocks for v in b["contents"]["variables"]
                  if v["name"] == "specID" + str(constant["id"])]
        if len(values) != 1 or constant["type"] != "uint":
            raise ValueError("Unverified specialization constant")
        definitions.append(f"#define SPIRV_CROSS_CONSTANT_ID_{constant['id']} {values[0]['uintValues'][0]}u")
    source = "\n".join(definitions) + "\n" + source
    if stage == "vertex" and export["raw"]["raster"]["depthNegativeOneToOne"]:
        source, count = re.subn(r"\bSPIRV_Cross_Output main\(",
                               "SPIRV_Cross_Output CapturedVertexProgram(", source)
        if count != 1:
            raise ValueError("Could not wrap the captured vertex entry point")
        source += '''
// VK_EXT_depth_clip_control uses [-w,w]; the tested Unity D3D target uses [0,w].
SPIRV_Cross_Output main(SPIRV_Cross_Input input)
{
    SPIRV_Cross_Output output = CapturedVertexProgram(input);
    output.gl_Position.z = (output.gl_Position.z + output.gl_Position.w) * 0.5f;
    return output;
}
'''
    texture_map = []
    for index, texture in enumerate(reflection.get("textures", [])):
        if texture["type"] != "sampler2D":
            raise ValueError("Current sample adapter requires 2D textures")
        old = texture["name"]
        new = f"_CapturedTexture{index}"
        source = re.sub(r"\b" + re.escape("_" + old + "_sampler") + r"\b", "sampler_CapturedTexture" + str(index), source)
        source = re.sub(r"\b" + re.escape(old) + r"\b", new, source)
        texture_map.append({**texture, "property": new})
    target.write_text(source, encoding="utf-8")
    return texture_map


def mesh_data(raw, directory):
    if raw["numInstances"] != 1 or raw["primitiveRestart"]:
        raise ValueError("Instancing / primitive restart needs a separate verified adapter")
    if raw["indexed"]:
        index = raw["indexBuffer"]
        data = (directory / index["file"]).read_bytes()
        width = index["stride"]
        format_code = {2: "H", 4: "I"}[width]
        indices = list(struct.unpack_from("<" + format_code * raw["numIndices"], data, raw["indexOffset"] * width))
        indices = [x + raw["baseVertex"] for x in indices]
    else:
        indices = list(range(raw["vertexOffset"], raw["vertexOffset"] + raw["numIndices"]))
    unique = list(dict.fromkeys(indices))
    remap = {x: i for i, x in enumerate(unique)}
    triangles = []
    if raw["topology"] == "Triangle Strip":
        for i in range(2, len(indices)):
            a, b, c = indices[i - 2:i + 1]
            if len({a, b, c}) != 3:
                continue
            if i % 2:
                a, b = b, a
            triangles.extend([remap[a], remap[b], remap[c]])
    elif raw["topology"] == "Triangle List":
        triangles = [remap[x] for x in indices]
    else:
        raise ValueError("Unsupported topology: " + raw["topology"])
    channels = []
    for attribute in raw["attributes"]:
        if not attribute["used"]:
            continue
        if attribute["generic"] or attribute["perInstance"]:
            raise ValueError("Generic / instance attributes are not implemented")
        buffer = raw["vertexBuffers"][attribute["vertexBuffer"]]
        data = (directory / buffer["file"]).read_bytes()
        format_name = attribute["format"]
        if format_name not in ("R32G32B32_FLOAT", "R32G32_FLOAT", "R8G8B8A8_UNORM"):
            raise ValueError("Unsupported attribute format: " + format_name)
        values = []
        for vertex in unique:
            offset = vertex * buffer["stride"] + attribute["offset"]
            if format_name == "R8G8B8A8_UNORM":
                value = [x / 255 for x in struct.unpack_from("<4B", data, offset)]
            else:
                width = 3 if format_name == "R32G32B32_FLOAT" else 2
                value = list(struct.unpack_from("<" + "f" * width, data, offset))
                value += [0] * (3 - width) + [1]
            values.append(value)
        channels.append({"values": [dict(zip("xyzw", value)) for value in values]})
    return {"channels": channels, "triangles": triangles}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export", type=Path, required=True)
    parser.add_argument("--spirv-cross", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--evidence", type=Path)
    parser.add_argument("--name", default="CapturedGeometry")
    parser.add_argument("--target-api", choices=["d3d11"], default="d3d11")
    parser.add_argument("--texture-origin", choices=["captured", "flip-y"], default="captured")
    args = parser.parse_args()
    root = args.export.resolve()
    manifest = read_json(root / "capture.json")
    if len(manifest["events"]) != 1:
        raise ValueError("Select exactly one Draw before reconstructing")
    raw = read_json(root / "raw/raw-draw.json")
    evidence = read_json(args.evidence or root / "evidence/draw-evidence.json")
    if raw["eventId"] != manifest["events"][0]["eventId"] or evidence["eventId"] != raw["eventId"]:
        raise ValueError("Draw evidence mismatch")
    args.output.mkdir(parents=True, exist_ok=True)
    export = {"root": root, "manifest": manifest, "event": manifest["events"][0], "raw": raw}
    stage_program(export, evidence, "vertex", args.spirv_cross, args.output / "CapturedVertex.hlsl")
    textures = stage_program(export, evidence, "pixel", args.spirv_cross, args.output / "CapturedFragment.hlsl")
    # Unity's sampler naming links the reconstructed sampler to the captured texture.
    texture_bindings = [x for x in export["event"]["bindings"] if x["stage"] == "pixel" and "textureDimension" in x]
    if len(textures) != 1 or len(texture_bindings) != 1:
        raise ValueError("Current sample adapter requires exactly one captured texture")
    binding = texture_bindings[0]
    views = [x for x in raw.get("textures", []) if x["stage"] == "pixel" and x["resource"] == binding["resource"]]
    if len(views) != 1 or views[0]["viewFormat"] not in ("R8G8B8A8_SRGB", "R8G8B8A8_UNORM"):
        raise ValueError("Missing verified RGBA8 texture view format")
    if binding["textureMips"] != 1:
        raise ValueError("Preserving multiple mip levels requires a DDS import adapter")
    if len(raw["samplers"]) != 1:
        raise ValueError("Missing exact sampler state")
    sampler = raw["samplers"][0]
    if sampler["min"] != "Linear" or sampler["mag"] != "Linear" or sampler["mipBias"] != 0 or sampler["unnormalized"]:
        raise ValueError("Unsupported sampler filtering")
    image = evidence["exports"][0]
    shutil.copyfile(root / "evidence" / image["file"], args.output / "CapturedTexture.png")
    blends = {"Zero": "Zero", "One": "One", "Src Alpha": "SrcAlpha", "1 - Src Alpha": "OneMinusSrcAlpha"}
    blend = raw["blend"][0]
    if blend["logic"] or blend["colorOperation"] != "Add" or blend["alphaOperation"] != "Add":
        raise ValueError("Unsupported blend operation")
    blend_line = "Blend Off" if not blend["enabled"] else "Blend " + ", ".join(
        " ".join(blends[blend[key]] for key in pair) for pair in (
            ("colorSource", "colorDestination"), ("alphaSource", "alphaDestination")))
    depth_functions = {"Greater Equal": "GEqual", "Less Equal": "LEqual", "Always": "Always"}
    stencil = raw["stencil"]["front"]
    if raw["stencilEnabled"] and (stencil != raw["stencil"]["back"] or stencil["function"] != "Always"
            or any(stencil[key] != "Keep" for key in ("pass", "fail", "depthFail"))):
        raise ValueError("Stencil needs a separate adapter")
    if raw["raster"]["cull"] != "None" or raw["depth"]["bounds"]:
        raise ValueError("Unsupported raster state")
    vertex_source = (args.output / "CapturedVertex.hlsl").read_text(encoding="utf-8")
    fragment_source = (args.output / "CapturedFragment.hlsl").read_text(encoding="utf-8")
    shader = f'''Shader "RenderDoc/Captured/{args.name}"
{{
    Properties
    {{
        _CapturedTexture0 ("Captured texture", 2D) = "white" {{}}
        [HideInInspector] _CapturedDepthComparison ("API depth adapter", Float) = 4
    }}
    SubShader
    {{
        Tags {{ "Queue"="Transparent" "RenderType"="Transparent" }}
        Pass
        {{
            Name "CapturedDraw"
            Cull Off
            {blend_line}
            ZWrite {"On" if raw["depth"]["write"] else "Off"}
            ZTest [_CapturedDepthComparison]
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex main
            #pragma fragment main
            #if defined(SHADER_STAGE_VERTEX)
{vertex_source}
            #else
{fragment_source}
            #endif
            ENDHLSL
        }}
    }}
}}
'''
    (args.output / "CapturedGeometry.shader").write_text(shader, encoding="utf-8")
    (args.output / "CapturedVertexProbe.compute").write_text('''#pragma kernel Probe
#include "CapturedVertex.hlsl"
StructuredBuffer<float4> CapturedInputs;
RWStructuredBuffer<float4> CapturedOutputs;
uint CapturedVertexCount;
[numthreads(64, 1, 1)]
void Probe(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= CapturedVertexCount) return;
    SPIRV_Cross_Input input;
    input._65 = CapturedInputs[id.x * 3];
    input._66 = CapturedInputs[id.x * 3 + 1];
    input._67 = CapturedInputs[id.x * 3 + 2];
    SPIRV_Cross_Output output = CapturedVertexProgram(input);
    CapturedOutputs[id.x * 3] = output.gl_Position;
    CapturedOutputs[id.x * 3 + 1] = output._68;
    CapturedOutputs[id.x * 3 + 2] = float4(output._69, 0, 0);
}
''', encoding="utf-8")
    sample = {"schema": "my-hook.unity-frozen-draw.v1", "eventId": raw["eventId"],
              "capture": manifest["capture"], "name": args.name, "shaderFolder": str(args.output.resolve()),
              "targetApi": args.target_api,
              "constantComponentsChecked": export["constantComponentsChecked"],
              "depthEnabled": raw["depth"]["enabled"], "depthFunction": raw["depth"]["function"],
              "referenceDirectory": str((root / "raw").resolve()),
              "viewportWidth": int(raw["viewports"][0]["width"]),
              "viewportHeight": int(raw["viewports"][0]["height"]),
              "textureFormat": binding["textureFormat"], "textureViewFormat": views[0]["viewFormat"],
              "sampler": sampler, "mesh": mesh_data(raw, root / "raw"),
              "knownLimits": ["Frozen Draw; not original Unity material identity.",
                              "Captured scene depth and pre-Draw background are needed for full image comparison."]}
    # Unity's HDR importer can change values in Gamma projects. Upload reference floats directly.
    import OpenEXR
    import numpy as np
    dds = (root / binding["snapshot"]).read_bytes()
    header_size = 148 if dds[84:88] == b"DX10" else 128
    width, height = binding["textureWidth"], binding["textureHeight"]
    if dds[:4] != b"DDS " or len(dds) - header_size != width * height * 4:
        raise ValueError("Captured RGBA8 DDS has an unexpected layout")
    # Decode before filtering, just as the captured sRGB image view does. Gamma Unity projects
    # override imported sRGB views, so the replay asset uses explicit linear float texels.
    texels = np.frombuffer(dds, dtype=np.uint8, offset=header_size).reshape(height, width, 4).astype(np.float32) / 255
    if views[0]["viewFormat"].endswith("_SRGB"):
        rgb = texels[:, :, :3]
        texels[:, :, :3] = np.where(rgb <= .04045, rgb / 12.92, ((rgb + .055) / 1.055) ** 2.4)
    if args.texture_origin == "flip-y":
        texels = texels[::-1].copy()
    texture_data = root / "captured-texture.rgba32f"
    texels.astype("<f4").tofile(texture_data)
    sample["textureDataPath"] = str(texture_data)
    sample["textureWidth"], sample["textureHeight"] = width, height
    sample["textureOriginAdapter"] = args.texture_origin
    for name in ("before", "after", "before-depth"):
        channels = OpenEXR.File(str(root / "raw" / (name + ".exr"))).channels()
        pixels = next(iter(channels.values())).pixels
        if pixels.ndim == 2:
            pixels = pixels[:, :, None]
        rgba = np.ones((pixels.shape[0], pixels.shape[1], 4), dtype="<f4")
        rgba[:, :, :pixels.shape[2]] = pixels
        rgba[::-1].copy().tofile(root / "raw" / (name + ".rgba32f"))
    (root / "unity-sample.json").write_text(json.dumps(sample), encoding="utf-8")
    print(json.dumps({"eventId": raw["eventId"], "vertices": len(sample["mesh"]["channels"][0]["values"]),
                      "triangles": len(sample["mesh"]["triangles"]) // 3, "shader": str(args.output / "CapturedGeometry.shader")}))


if __name__ == "__main__":
    main()
