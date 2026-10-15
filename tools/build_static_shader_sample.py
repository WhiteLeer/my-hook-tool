"""Adapt a verified static program pair, exact material reference and named bindings to Unity."""

import argparse
import hashlib
import json
import re
import shutil
import struct
import subprocess
import tempfile
from pathlib import Path

from extract_apk_shader_program import verify_metadata
from hsr_static_container import load_container


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def unique(items, label):
    if len(items) != 1:
        raise ValueError(f"Expected exactly one {label}; got {len(items)}")
    return items[0]


def reflect(cross, path):
    # File output avoids intermittent truncated Windows pipe output on large reflection JSON.
    with tempfile.TemporaryDirectory(prefix='shader-reflection-') as directory:
        target=Path(directory)/'reflection.json'
        subprocess.run([str(cross),str(path),'--reflect','--output',str(target)],check=True,capture_output=True)
        return json.loads(target.read_text(encoding='utf-8-sig'))


def keyword_names(record, names):
    result = []
    inverse = {v: k for k, v in names.items()}
    for key in ("combined", "global", "local"):
        for index in record["keywordIndices"][key]:
            if index not in inverse:
                raise ValueError("Keyword index does not resolve to an exact name")
            result.append(inverse[index])
    return sorted(set(result))


def adapt(source, reflection, parameters, names, properties, stage, evidence, environment=None):
    environment = environment or {}
    by_index = {v: k for k, v in names.items()}
    cbuffers = {}
    for block in parameters["constantBuffers"]:
        binding = unique([x for x in parameters["constantBufferBindings"]
                          if x["nameIndex"] == block["nameIndex"]], "cbuffer binding")
        # HSR's serialized Vulkan binding packet carries set/binding in these
        # fields. Every used block must also match its full reflected byte size.
        key = ((binding["index"] >> 16) & 255, binding["index"] & 65535)
        if key in cbuffers:
            raise ValueError("Duplicate descriptor in the serialized binding contract")
        cbuffers[key] = (block, binding)
    replacements = {}
    matrix_names = set()
    extras = set()
    spans = list(re.finditer(r"cbuffer\s+(\w+)\s*:\s*register\([^)]*\)\s*\{(.*?)\};", source, re.S))
    stripped = re.sub(r"cbuffer\s+\w+\s*:\s*register\([^)]*\)\s*\{.*?\};", "", source, flags=re.S)
    for span in spans:
        reflected = unique([x for x in reflection["ubos"] if x["name"] == span[1]], "reflected cbuffer")
        block, binding = cbuffers.get((reflected["set"], reflected["binding"]), (None, None))
        if block is None or reflected["block_size"] != block["size"]:
            raise ValueError("No exact descriptor and size match for " + span[1])
        for line in span[2].splitlines():
            if not line.strip():
                continue
            declaration = re.fullmatch(r"\s*(float|int|uint)([1-4]?)\s+(\w+)(\[\d+\])?\s*:\s*packoffset\(c(\d+)(?:\.([xyzw]))?\);\s*", line)
            if not declaration:
                raise ValueError("Unsupported cbuffer declaration: " + line)
            kind, width, identifier, array, slot, component = declaration.groups()
            if not re.search(r"\b" + identifier + r"\b", stripped):
                continue
            offset = int(slot) * 16 + ("xyzw".index(component) * 4 if component else 0)
            parameter = unique([x for x in block["vectors"] + block["matrices"] if x["index"] == offset],
                               "named parameter at byte offset " + str(offset))
            name = by_index[parameter["nameIndex"]]
            if array:
                if parameter not in block["matrices"] or parameter["type"] != 0 or parameter["rows"] != 4 or parameter["arraySize"] != 0 or kind != "float" or width != "4":
                    raise ValueError("Matrix type/shape disagrees with the reflected declaration")
                if name not in ("unity_ObjectToWorld", "unity_WorldToObject", "unity_MatrixVP", "unity_MatrixMVP", "unity_MatrixV", "unity_MatrixInvP", "unity_MatrixInvV", "_NonJitteredViewProjMatrix", "_ES_GlobalRotMatrix") or array != "[4]":
                    raise ValueError("Unsupported engine matrix: " + name)
                replacement = "_Recovered" + name + "Columns"
                matrix_names.add(name)
            else:
                expected_kind = {0: "float", 1: "int", 2: "bool"}.get(parameter["type"])
                compatible_kind=expected_kind==kind or (expected_kind=='int' and kind=='uint')
                if parameter not in block["vectors"] or not compatible_kind or parameter["dimension"] != int(width or 1) or parameter["arraySize"] != 0:
                    raise ValueError("Vector type/shape disagrees with reflection: "+name+" declaration="+line.strip()+" metadata="+str(parameter))
                texture_transform = name.endswith("_ST") and name[:-3] in properties and properties[name[:-3]]["type"] == "Texture"
                engine_global = name in ("_ProjectionParams", "_ZBufferParams", "_WorldSpaceCameraPos", "unity_WorldTransformParams", "_ScaledScreenParams")
                if name not in properties and not texture_transform and name not in ("_Time", "_ClipRect", "_UI_CLIP_RECT") and name not in environment and not engine_global:
                    raise ValueError("Used parameter has no material or explicit UI adapter: " + name)
                replacement = "_RecoveredTime" if name == "_Time" else name
                if name=='_ScaledScreenParams':replacement='_ScreenParams'
                if kind == "int" and properties.get(name, {}).get("type") == "Float":
                    replacement = f"int({name})"
                if kind == 'uint' and properties.get(name,{}).get('type')=='Float':
                    replacement=f'asuint(int({name}))'
                if name in ("_Time", "_ClipRect", "_UI_CLIP_RECT"):
                    extras.add(name)
                if name in environment:
                    contract = environment[name]
                    if contract['type'] != ('Vector'+width if width in ('2','3') else 'Vector' if width == '4' else 'Float') or not contract.get('reason'):
                        raise ValueError('Environment contract disagrees with reflected source type: '+name)
                    extras.add(name)
            replacements[identifier] = replacement
            evidence.append({"stage": stage, "generatedVariable": identifier, "sourceName": name,
                             "set": reflected["set"], "binding": reflected["binding"],
                             "serializedBindingPacket": binding["index"], "byteOffset": offset,
                             "blockSize": block["size"], "adapter": replacement})
    source = stripped
    for identifier, replacement in replacements.items():
        source = re.sub(r"\b" + identifier + r"\b", replacement, source)
    textures = reflection.get("separate_images", [])
    samplers = reflection.get("separate_samplers", [])
    for texture in textures:
        param = unique([x for x in parameters["textures"]
                        if ((x["index"] >> 16) & 255, x["index"] & 65535)
                        == (texture["set"], texture["binding"])], "texture descriptor")
        name = by_index[param["nameIndex"]]
        is_scene_volume=texture['type']=='texture3D' and param['dimension']==3 and name not in properties and environment.get(name,{}).get('type')=='Texture3D'
        if not is_scene_volume and (texture["type"] != "texture2D" or param["dimension"] != 2):
            raise ValueError("This adapter requires explicit 2D texture descriptors")
        inline=None
        if param['samplerIndex']==-1:
            inline=unique(parameters.get('samplers',[]),'serialized inline sampler')
            packet=inline['bindPoint']
            sampler=unique([x for x in samplers if (x['set'],x['binding'])==((packet>>16)&255,packet&65535)],'inline sampler descriptor')
            contract=environment.get('$inlineSampler'+str(inline['sampler']))
            if not contract or contract.get('value')!='PointClamp' or (inline['sampler']!=84 and not is_scene_volume):
                raise ValueError('Inline sampler state requires an explicit preview contract')
            sampler_name='sampler_PointClamp'
        else:
            sampler = unique([x for x in samplers
                              if ((param["samplerIndex"] >> 16) & 255, param["samplerIndex"] & 65535)
                              == (x["set"], x["binding"])], "paired sampler descriptor")
            sampler_name='sampler'+name
        source = re.sub(r"\b" + texture["name"] + r"\b", name, source)
        source = re.sub(r"\b" + sampler["name"] + r"\b", sampler_name, source)
        evidence.append({"stage": stage, "sourceName": name, "set": texture["set"],
                         "binding": texture["binding"], "samplerBinding": sampler["binding"],
                         "inlineSamplerRaw":inline['sampler'] if inline else None,
                         "samplerAdapter":sampler_name if inline else None})
    source = re.sub(r"Texture[23]D<float4>\s+\w+\s*:\s*register\([^)]*\);", "", source)
    source = re.sub(r"SamplerState\s+\w+\s*:\s*register\([^)]*\);", "", source)
    return source, matrix_names, extras


def generate(args):
    environment = read(args.environment)['parameters'] if args.environment else {}
    provenance = verify_metadata(args.shader_report, args.containers)
    report = read(args.shader_report)
    material = unique([x for x in report["materials"] if x["name"] == args.material_name], "material")
    import UnityPy
    material_env = load_container(args.containers / material["source"]["fileName"])
    material_object = unique([x for x in material_env.objects if x.path_id == material["pathId"]
                              and x.type.name == "Material"], "material serialized object")
    independent = material_object.read_typetree()
    pointer = independent["m_Shader"]
    if pointer["m_FileID"] != material["shader"]["fileId"] or pointer["m_PathID"] != material["shader"]["pathId"]:
        raise ValueError("Independent material Shader PPtr disagrees with AS")
    if pointer["m_FileID"] <= 0:
        raise ValueError("This sample requires an external Shader reference")
    external = material_object.assets_file.externals[pointer["m_FileID"] - 1].path
    if Path(external.replace("\\", "/")).name != material["shader"]["sourceFile"]:
        raise ValueError("Independent material external table disagrees with AS")
    independent_floats = dict(independent["m_SavedProperties"]["m_Floats"])
    if set(independent_floats) != set(material["floats"]) or any(
            struct.pack("<f", v) != struct.pack("<f", material["floats"][k]) for k, v in independent_floats.items()):
        raise ValueError("Independent material Float bytes disagree with AS")
    independent_colors = dict(independent["m_SavedProperties"]["m_Colors"])
    if set(independent_colors) != set(material["colors"]) or any(
            struct.pack("<4f", *(value[x] for x in "rgba")) != struct.pack("<4f", *material["colors"][key])
            for key, value in independent_colors.items()):
        raise ValueError("Independent material Color bytes disagree with AS")
    shader = unique([x for x in report["shaders"] if x["pathId"] == material["shader"]["pathId"]
                     and x["source"]["fileName"] == material["shader"]["sourceFile"]], "Shader PPtr target")
    extraction = read(args.programs / "program-extraction.json")
    if extraction.get("metadataReportSha256") != provenance["reportSha256"]:
        raise ValueError("Program extraction and current metadata report are not the same export")
    if (extraction["sourceFile"], extraction["pathId"]) != (shader["source"]["fileName"], shader["pathId"]):
        raise ValueError("Decoded program source identity disagrees with material Shader PPtr")
    if hashlib.sha256((args.containers / extraction["sourceFile"]).read_bytes()).hexdigest() != extraction["containerSha256"]:
        raise ValueError("Shader container changed since extraction")
    program = unique([x for x in extraction["programs"] if x["platform"] == "Vulkan"
                      and x["index"] == args.program_index], "extracted Vulkan program")
    decoded_report = read(args.decoded / "snippet-decode.json")
    if decoded_report["sourceSha256"].lower() != program["sha256"].lower():
        raise ValueError("Decoded snippets came from a different static program")
    passes=shader["serialized"]["subShaders"][0]["passes"]
    pass_info = unique([p for p in passes if p['state']['tags'].get('LIGHTMODE')==args.pass_lightmode], 'explicit pass') if args.pass_lightmode else passes[0]
    exact_state=bool(args.pass_lightmode or getattr(args,'exact_pass_state',False))
    pass_label=args.pass_lightmode or ('Pass0' if exact_state else '')
    properties = {p["name"]: p for p in shader["serialized"]["properties"]}
    names = pass_info["nameIndices"]
    actual_keywords = sorted(set(x for x in re.split(r"[\s,]+", material["shaderKeywords"]) if x))
    stage_records = {}
    projected_keywords = {}
    for stage in ("vertex", "fragment"):
        supported=[x for x in pass_info['stages'][stage]['subPrograms'] if x['gpuProgramType']=='SPIRV']
        universe=set(k for x in supported for k in keyword_names(x,names))
        selected_keywords=sorted(set(actual_keywords)&universe) if exact_state else actual_keywords
        projected_keywords[stage]=selected_keywords
        candidates = [x for x in pass_info["stages"][stage]["subPrograms"] if x["gpuProgramType"] == "SPIRV"
                      and keyword_names(x, names) == selected_keywords]
        indices = {x["blobIndex"] for x in candidates}
        if not candidates or len(indices) != 1:
            raise ValueError("Material keywords do not identify the selected static program")
        index = next(iter(indices))
        if stage == "vertex" and index != args.program_index:
            raise ValueError("Selected vertex program differs from exact keyword selection")
        if stage == "fragment":
            platform = unique([x for x in extraction["platforms"] if x["platform"] == "Vulkan"], "Vulkan table")
            if platform["compressedLengths"][index] or platform["decompressedLengths"][index]:
                raise ValueError("Separate fragment code requires another program-pair adapter")
        stage_records[stage] = candidates[0]
    if not exact_state and stage_records["vertex"]["channels"]["sourceMap"] != 25:
        raise ValueError("This bounded vertex adapter supports POSITION/COLOR/TEXCOORD0 (sourceMap25) only")
    args.output.mkdir(parents=True, exist_ok=True)
    stages = {}
    raw_paths = []
    for record in decoded_report["snippets"]:
        snippet = args.decoded / record["file"]
        if hashlib.sha256(snippet.read_bytes()).hexdigest() != record["spirvSha256"].lower():
            raise ValueError("Decoded SPIR-V changed since its provenance report")
        reflection = reflect(args.cross, snippet)
        stage = unique(reflection["entryPoints"], "entry point")["mode"]
        if stage not in ("vert", "frag") or stage in stages:
            raise ValueError("Unsupported or duplicate shader stage")
        source = subprocess.check_output([str(args.cross), str(snippet), "--hlsl", "--shader-model", "50"], text=True)
        stages[stage] = (reflection, source)
        raw_path = args.programs / ("RecoveredVertex.hlsl" if stage == "vert" else "RecoveredFragment.hlsl")
        raw_path.write_text(source, encoding="utf-8")
        raw_paths.append(str(raw_path))
    if set(stages) != {"vert", "frag"}:
        raise ValueError("Both real program stages are required")
    evidence = []
    adapted = {}
    matrices, extras = set(), set()
    for stage, key in (("vert", "vertex"), ("frag", "fragment")):
        reflection, source = stages[stage]
        # This APK Vulkan format stores both entry points and their binding table
        # in the vertex program; the selected fragment table entry has zero code.
        adapted[stage], stage_matrices, stage_extras = adapt(source, reflection,
            stage_records["vertex"]["parameters"], names, properties, stage, evidence, environment)
        matrices |= stage_matrices
        extras |= stage_extras
    # The source map and reflected locations provide the input contract; do not
    # match attributes by generated variable spelling or address.
    vertex_inputs = stages["vert"][0]["inputs"]
    if exact_state:
        channels=stage_records['vertex']['channels'].get('bindings')
        if not channels:raise ValueError('Missing serialized source/target vertex channels; guessing is forbidden')
        channel_map={0:('position','POSITION'),1:('normal','NORMAL'),2:('tangent','TANGENT'),3:('color','COLOR'),
                     **{i+4:('uv'+str(i),'TEXCOORD'+str(i)) for i in range(8)}}
        semantics={}
        for c in channels:
            if c['source'] not in channel_map:raise ValueError('Unsupported source vertex channel')
            if c['target']<13:raise ValueError('Only the explicitly inspected Vulkan generic-attribute bank is supported')
            location=c['target']-13
            inp=unique([i for i in vertex_inputs if i['location']==location], 'reflected target channel')
            if inp['type'] not in ('vec2','vec3','vec4'):raise ValueError('Unsupported vertex shape')
            n,semantic=channel_map[c['source']]
            semantics[location]=(n,'float'+inp['type'][-1],semantic)
        if set(semantics)!=set(x['location'] for x in vertex_inputs):raise ValueError('Not all vertex inputs have exact source channel bindings')
    else:
        uv_input = unique([x for x in vertex_inputs if x["location"] == 2], "UV0 input")
        if uv_input["type"] not in ("vec2", "vec4"):
            raise ValueError("Unsupported UV0 attribute shape")
        uv_kind = {"vec2": "float2", "vec4": "float4"}[uv_input["type"]]
        semantics = {0: ("position", "float4", "POSITION"), 1: ("color", "float4", "COLOR"),2: ("uv", uv_kind, "TEXCOORD0")}
    vertex_declarations=' '.join(f'{kind} {name} : {semantic};' for name,kind,semantic in semantics.values())
    assignments = []
    for item in vertex_inputs:
        name, kind, semantic = semantics[item["location"]]
        if item["type"] != {"float4": "vec4", "float3":"vec3", "float2": "vec2"}[kind]:
            raise ValueError("Vertex format differs from the sourceMap contract")
        assignments.append(f"    i.{item['name']} = input.{name};")
    varyings = {x["location"]: x for x in stages["vert"][0]["outputs"]}
    fragment_assignments = []
    for item in stages["frag"][0]["inputs"]:
        other = varyings[item["location"]]
        if item["type"] != other["type"]:
            raise ValueError("Stage varying types disagree")
        fragment_assignments.append(f"    i.{item['name']} = input.{other['name']};")
    fragment_parameters=''
    if 'gl_FrontFacing : SV_IsFrontFace' in stages['frag'][1]:
        fragment_parameters=', bool frontFacing : SV_IsFrontFace'
        fragment_assignments.append('    i.gl_FrontFacing = frontFacing;')
    if re.search(r'gl_FragCoord\s*:\s*SV_Position',stages['frag'][1]):
        fragment_assignments.append('    i.gl_FragCoord = input.gl_Position;')
    result_field = unique(stages["frag"][0]["outputs"], "fragment output")["name"]
    property_lines, uniforms = [], []
    for name, prop in properties.items():
        kind, value = prop["type"], prop["defaultValue"]
        description = prop["description"].replace('"', "'")
        flags = "[HideInInspector] " if "HideInInspector" in prop["flags"] else ""
        if kind == "Texture":
            property_lines.append(f'{flags}{name}("{description}", 2D) = "{prop["defaultTexture"]["name"] or "white"}" {{}}')
            uniforms += [f"Texture2D<float4> {name};", f"SamplerState sampler{name};", f"float4 {name}_ST;"]
        elif kind in ("Color", "Vector"):
            property_lines.append(f'{flags}{name}("{description}", {kind}) = ({",".join(str(x) for x in value)})')
            uniforms.append(f"float4 {name};")
        else:
            unity_type = f"Range({value[1]},{value[2]})" if kind == "Range" else "Float"
            property_lines.append(f'{flags}{name}("{description}", {unity_type}) = {value[0]}')
            uniforms.append(f"float {name};")
    for name in sorted(extras - {"_Time"}):
        if name in environment:
            contract=environment[name]
            kind=contract['type']
            default='('+','.join(map(str,list(contract['value'])+[0]*(4-len(contract['value']))))+')' if kind.startswith('Vector') else str(contract['value'])
        else:
            kind, default = ("Vector", "(-10000,-10000,10000,10000)") if name == "_ClipRect" else ("Float", "0")
        property_lines.append(f'[HideInInspector] {name}("Explicit preview adapter: {name}", {"Vector" if kind.startswith("Vector") else kind}) = {default}')
        uniforms.append(("float"+(kind[6:] or '4')+' ' if kind.startswith('Vector') else "float ") + name + ";")
    if any(x.get('inlineSamplerRaw') is not None for x in evidence):uniforms.append('SamplerState sampler_PointClamp;')
    if "_Time" in extras:
        property_lines.append('[HideInInspector] _PreviewTime("Preview time (-1 = engine time)", Float) = -1')
        uniforms += ["float _PreviewTime;", "static float4 _RecoveredTime;"]
    matrix_declarations, matrix_assignments = [], []
    for name in sorted(matrices):
        identifier = "_Recovered" + name + "Columns"
        engine = {"unity_ObjectToWorld":"unity_ObjectToWorld", "unity_WorldToObject":"unity_WorldToObject", "unity_MatrixVP":"UNITY_MATRIX_VP",
                  "unity_MatrixMVP":"UNITY_MATRIX_MVP", "unity_MatrixV":"UNITY_MATRIX_V", "_NonJitteredViewProjMatrix":"UNITY_MATRIX_VP",
                  "unity_MatrixInvP":"unity_CameraInvProjection", "unity_MatrixInvV":"unity_CameraToWorld",
                  "_ES_GlobalRotMatrix":"float4x4(1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1)"}[name]
        matrix_declarations.append(f"static float4 {identifier}[4];")
        matrix_assignments.append(f"    float4x4 matrix_{name} = transpose({engine});")
        matrix_assignments += [f"    {identifier}[{i}] = matrix_{name}[{i}];" for i in range(4)]
    if "_Time" in extras:
        matrix_assignments += ["    float previewTime = _PreviewTime < 0 ? _Time.y : _PreviewTime;",
                               "    _RecoveredTime = float4(previewTime / 20.0, previewTime, previewTime * 2.0, previewTime * 3.0);"]
    time_fragment = "    float previewTime = _PreviewTime < 0 ? _Time.y : _PreviewTime;\n    _RecoveredTime = float4(previewTime / 20.0, previewTime, previewTime * 2.0, previewTime * 3.0);" if "_Time" in extras else ""
    shader_name = "Recovered/HSR-4.6/" + shader["name"]
    naming_keywords=projected_keywords['vertex'] if exact_state else actual_keywords
    if exact_state:shader_name+='/'+pass_label
    if naming_keywords:
        shader_name += "/" + "+".join(naming_keywords)
    state_adapter = 'Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }'
    alpha_blend = ''
    engine_textures = []
    for name in sorted({x['sourceName'] for x in evidence if 'samplerBinding' in x} - set(properties)):
        if name not in environment or environment[name].get('type') not in ('Texture','Texture3D'):
            raise ValueError('No explicit scene texture contract: '+name)
        default=environment[name]['value']
        if default not in ('white','black'):
            raise ValueError('Only constant empty-scene depth fixtures supported')
        dimension='3D' if environment[name]['type']=='Texture3D' else '2D'
        property_lines.append(f'[HideInInspector] {name}("Isolated scene fixture: {name}",{dimension})="{default}" {{}}')
        uniforms += [f'Texture{dimension}<float4> {name};',f'SamplerState sampler{name};']
        engine_textures.append(name)
    if args.environment and not exact_state:
        # No stencil data is exported in this AS report; never borrow UI state.
        state_adapter = '// Stencil is not recovered; isolated preview uses no stencil test.'
        target_state=pass_info['state']['blendTargets'][0]
        if target_state['srcBlendAlpha']['name'] != '_AlphaSrcBlend' or target_state['destBlendAlpha']['name'] != '_DstBlend':
            raise ValueError('Separate alpha blend state needs another explicit adapter')
        alpha_blend=', [_AlphaSrcBlend] [_DstBlend]'
    queue='Transparent';render_type='Transparent'
    directives='Cull [_Cull]\n            ZTest [_AlwaysOnTop]\n            ZWrite [_ZWrite]\n            Blend [_SrcBlend] [_DstBlend]'+alpha_blend+'\n            '+state_adapter
    if exact_state:
        state=pass_info['state'];blend=state['blendTargets'][0]
        queue=state['tags'].get('QUEUE','Geometry');render_type=state['tags'].get('RenderType','Opaque')
        def state_value(x,enum=None):
            if x['name'] not in ('','<noninit>'):
                if x['name'] not in properties:raise ValueError('Render-state property is not declared')
                return '['+x['name']+']'
            value=int(x['value']);return enum[value] if enum else str(value)
        blend_enums=['Zero','One','DstColor','SrcColor','OneMinusDstColor','SrcAlpha','OneMinusSrcColor','DstAlpha','OneMinusDstAlpha','SrcAlphaSaturate','OneMinusSrcAlpha']
        directives='\n            '.join([
            'Cull '+state_value(state['culling'],['Off','Front','Back']),
            'ZTest '+state_value(state['zTest'],['Disabled','Never','Less','Equal','LEqual','Greater','NotEqual','GEqual','Always']),
            'ZWrite '+state_value(state['zWrite'],['Off','On']),
            'Blend '+state_value(blend['srcBlend'],blend_enums)+' '+state_value(blend['destBlend'],blend_enums)+', '+state_value(blend['srcBlendAlpha'],blend_enums)+' '+state_value(blend['destBlendAlpha'],blend_enums),
            'ColorMask '+state_value(blend['colorMask'],{0:'0',15:'RGBA',14:'RGB',8:'R',4:'G',2:'B',1:'A'}),
            '// Stencil and original pipeline scheduling are not reproduced in the isolated reflection-pass fixture.'])
    source = f'''// Generated from current APK SPIR-V. Original arithmetic remains in the two namespaces.
Shader "{shader_name}"
{{
    Properties
    {{
        {chr(10).join(property_lines)}
    }}
    SubShader
    {{
        Tags {{ "Queue"="{queue}" "RenderType"="{render_type}" }}
        Pass
        {{
            Name "RecoveredForward"
            // No pipeline-specific LightMode: Built-in uses Always, URP uses SRPDefaultUnlit.
            {directives}
            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex RecoveredVertex
            #pragma fragment RecoveredFragment
            #include "UnityCG.cginc"
            {chr(10).join(uniforms)}
            {chr(10).join(matrix_declarations)}
            namespace VertexProgram {{
            {adapted['vert']}
            }}
            namespace FragmentProgram {{
            {adapted['frag']}
            }}
            struct VertexInput {{ {vertex_declarations} }};
            VertexProgram::SPIRV_Cross_Output RecoveredVertex(VertexInput input)
            {{
                VertexProgram::SPIRV_Cross_Input i;
{chr(10).join(matrix_assignments)}
{chr(10).join(assignments)}
                VertexProgram::SPIRV_Cross_Output result = VertexProgram::main(i);
                // Original Vulkan program flips Y. Unity's target projection already uses its own API convention.
                result.gl_Position.y = -result.gl_Position.y;
                return result;
            }}
            float4 RecoveredFragment(VertexProgram::SPIRV_Cross_Output input{fragment_parameters}) : SV_Target0
            {{
                FragmentProgram::SPIRV_Cross_Input i;
{time_fragment}
{chr(10).join(fragment_assignments)}
                return FragmentProgram::main(i).{result_field};
            }}
            ENDHLSL
        }}
    }}
    Fallback Off
}}
'''
    source = source.replace("\n\n    i.", "\n    i.") if not time_fragment else source
    stem = shader["name"].split("/")[-1]
    if exact_state:stem+='_'+pass_label
    if naming_keywords:
        stem += "_" + "_".join(x.lstrip("_") for x in naming_keywords)
    target = args.output / (stem + ".shader")
    shared_shader = None
    if args.reuse_shader:
        if args.reuse_shader.read_text(encoding="utf-8") != source:
            raise ValueError("Requested shared Shader source is not identical")
        assets = next((x for x in args.reuse_shader.resolve().parents if x.name == "Assets"), None)
        if assets is None:
            raise ValueError("Shared Shader must be a Unity asset")
        shared_shader = "Assets/" + args.reuse_shader.resolve().relative_to(assets).as_posix()
    else:
        target.write_text(source, encoding="utf-8")
    from PIL import Image
    import numpy as np
    textures = []
    independent_textures = dict(independent["m_SavedProperties"]["m_TexEnvs"])
    used_textures = sorted({x["sourceName"] for x in evidence if "samplerBinding" in x} | set(independent_textures))
    for name in used_textures:
        if name in engine_textures:
            if environment[name]['type']=='Texture3D':
                textures.append(dict(property=name,kind='scene_fixture_constant_3d',file=name.lstrip('_')+'_EmptyScene.asset',
                                     constantColor=[0,0,0,1],scale=[1,1],offset=[0,0]))
            else:
                textures.append({'property':name,'kind':'scene_fixture_default','defaultName':environment[name]['value'],
                                 'scale':[1,1],'offset':[0,0]})
            continue
        candidates = [x for x in material["textures"] if x["property"] == name]
        if not candidates:
            if name in independent_textures:
                raise ValueError("AS omitted an independently observed texture slot")
            default = properties[name]["defaultTexture"]["name"]
            if default not in ("white", "black", "gray", "grey"):
                raise ValueError("Unresolved nonstandard Shader texture default")
            textures.append({"property": name, "kind": "shader_declared_default", "defaultName": default,
                             "scale": [1, 1], "offset": [0, 0]})
            continue
        texture = unique(candidates, "material texture slot")
        ref = texture["texture"]
        native = independent_textures[name]
        if ref['pathId']==0 and native['m_Texture']['m_PathID']==0 and ref['fileId']==native['m_Texture']['m_FileID']:
            default=properties[name]['defaultTexture']['name']
            if default not in ('white','black','gray','grey','bump'):raise ValueError('Unresolved null-slot Shader default: '+default)
            textures.append(dict(property=name,kind='serialized_null_shader_default',defaultName=default,
                                 sourceNullPointer=True,scale=texture['scale'],offset=texture['offset']))
            continue
        if not ref["sourceFile"] or ref["pathId"] == 0 or native["m_Texture"]["m_FileID"] != ref["fileId"] or native["m_Texture"]["m_PathID"] != ref["pathId"]:
            raise ValueError("Texture slot is null or disagrees with independent PPtr")
        file_id = native["m_Texture"]["m_FileID"]
        if file_id <= 0 or Path(material_object.assets_file.externals[file_id-1].path).name != ref["sourceFile"]:
            raise ValueError("Texture external source disagrees with independent table")
        env = load_container(args.containers / ref["sourceFile"])
        tex_object = unique([x for x in env.objects if x.path_id == ref["pathId"] and x.type.name == "Texture2D"], "texture PPtr target")
        tex = tex_object.read_typetree()
        if not (0 < tex["m_Width"] <= 4096 and 0 < tex["m_Height"] <= 4096) or tex["m_ColorSpace"] not in (0, 1) or tex["m_MipCount"] != 1:
            raise ValueError("Texture metadata or source mip chain requires another adapter")
        if tex['m_TextureFormat']==17:
            if tex['m_ColorSpace']!=0:raise ValueError('Half-float source must remain linear')
            raw=bytes(tex.get('image data',b''))
            stream=tex.get('m_StreamData',{})
            if stream.get('size'):
                resource=args.containers/Path(stream['path']).name
                with resource.open('rb') as f:
                    f.seek(stream['offset']);raw=f.read(stream['size'])
            if len(raw)!=tex['m_Width']*tex['m_Height']*8:raise ValueError('RGBAHalf resource size differs')
            if not np.isfinite(np.frombuffer(raw,dtype='<f2')).all():raise ValueError('Nonfinite LUT source')
            file_name=tex['m_Name']+'.bytes'
            if file_name!=Path(file_name).name:raise ValueError('Unsafe half texture name')
            (args.output/file_name).write_bytes(raw)
            textures.append({'property':name,'kind':'serialized_half_texture','file':file_name,
                'sourceFile':ref['sourceFile'],'pathId':ref['pathId'],'scale':texture['scale'],'offset':texture['offset'],
                'width':tex['m_Width'],'height':tex['m_Height'],'srgbTexture':False,
                'wrapU':tex['m_TextureSettings']['m_WrapU'],'wrapV':tex['m_TextureSettings']['m_WrapV'],
                'filterMode':tex['m_TextureSettings']['m_FilterMode'],'rawSha256':hashlib.sha256(raw).hexdigest()})
            continue
        for field, original in (("scale", "m_Scale"), ("offset", "m_Offset")):
            if struct.pack("<2f", *texture[field]) != struct.pack("<2f", native[original]["x"], native[original]["y"]):
                raise ValueError("Texture transform differs from independent bytes")
        file_name = tex["m_Name"] + ".png"
        if file_name != Path(file_name).name:
            raise ValueError("Texture name is not a safe filename")
        png = args.texture if name == "_MainTex" and args.texture else args.textures / file_name if args.textures else None
        if args.textures:
            identity_png=args.textures/ref['sourceFile']/str(ref['pathId'])/file_name
            if identity_png.exists():
                png=identity_png
                file_name=name.lstrip('_')+'_'+file_name
        if png is None or not png.exists():
            raise ValueError("Missing exported texture: " + file_name)
        decoded_texture = np.asarray(tex_object.read().image.convert("RGBA"), dtype=np.int16)
        supplied_texture = np.asarray(Image.open(png).convert("RGBA"), dtype=np.int16)
        if decoded_texture.shape != supplied_texture.shape:
            raise ValueError("Texture export dimensions disagree with serialized source")
        pixel_delta = np.abs(decoded_texture - supplied_texture)
        if int(pixel_delta.max()) > 1:
            raise ValueError("Texture export disagrees with independent decode")
        shutil.copyfile(png, args.output / file_name)
        textures.append({"property": name, "kind": "serialized_texture", "file": file_name,
            "sourceFile": ref["sourceFile"], "pathId": ref["pathId"], "scale": texture["scale"], "offset": texture["offset"],
            "srgbTexture": tex["m_ColorSpace"] == 1, "textureColorSpace": tex["m_ColorSpace"],
            "wrapU": tex["m_TextureSettings"]["m_WrapU"], "wrapV": tex["m_TextureSettings"]["m_WrapV"],
            "filterMode": tex["m_TextureSettings"]["m_FilterMode"],
            "decodeEvidence": {"maxComponentDifference": int(pixel_delta.max()), "differingComponents": int(np.count_nonzero(pixel_delta)),
                "serializedObjectSha256": hashlib.sha256(tex_object.get_raw_data()).hexdigest(), "pngSha256": hashlib.sha256(png.read_bytes()).hexdigest()}})
    sample = {"schema": "my-hook.unity-static-shader-sample.v2", "packageVersion": "4.6.0",
              "name": material["name"], "shaderName": shader_name,
              "sourceShaderName": shader["name"], "shaderPath": target.name,
              "sharedShaderAssetPath": shared_shader, "keywords": actual_keywords,
              "shaderSha256": hashlib.sha256((args.reuse_shader if shared_shader else target).read_bytes()).hexdigest(),
              "hasTimeAdapter": "_Time" in extras, "textures": textures,
              "vertexMetadataBlobIndex": stage_records["vertex"]["blobIndex"],
              "metadataReportSha256": provenance["reportSha256"],
              "fragmentMetadataBlobIndex": stage_records["fragment"]["blobIndex"],
              "sourceMaterialFile": material["source"]["fileName"], "sourceMaterialPathId": material["pathId"],
              "sourceShaderFile": shader["source"]["fileName"], "sourceShaderPathId": shader["pathId"],
              "floats": [{"name": k, "value": v} for k, v in material["floats"].items()],
              "hasMaterialState": True,
              "sourcePassLightMode":args.pass_lightmode,
              "sourcePassIndex":passes.index(pass_info),
              "exactSerializedPassState":exact_state,
              "passKeywordProjection":projected_keywords,
              "sourceVertexChannels":stage_records['vertex']['channels'],
              "renderQueue": independent['m_CustomRenderQueue'],
              "enableInstancing": independent['m_EnableInstancingVariants'],
              "doubleSidedGI": independent['m_DoubleSidedGI'],
              "lightmapFlags": independent['m_LightmapFlags'],
              "disabledShaderPasses": independent['disabledShaderPasses'],
              "tags": [{'name':k,'value':v} for k,v in independent['stringTagMap']],
              "colors": [{"name": k, "value": v} for k, v in material["colors"].items()],
              "bindingEvidence": evidence, "rawHlsl": raw_paths,
              "environmentContract": read(args.environment) if args.environment else None,
              "limitations": ["UI quad is a preview fixture, not the recovered particle emitter.",
                               "UI clipping is disabled with explicit neutral adapter values.",
                               "No same-version RDC reference comparison has been performed.",
                               "One extra platform-record word remains unidentified; extraction is experimental."]}
    if args.environment:
        sample['limitations'] = ['Exact static material keywords and compiled arithmetic; runtime environment constants are NOT recovered.',
            'Empty-scene linear01 depth is a declared white fixture, not game depth or depth-intersection validation.',
            'No stencil or original render-pipeline/postprocess reconstruction; no same-version RDC comparison.',
            'One extra platform-record word remains unidentified. Historical schema semantics remain experimental.']
    if exact_state:
        sample['limitations']=['Original '+pass_label+' pass is rehosted, NOT the original main deferred pipeline.',
            'Environment, inline sampler 84 preview override and neutral scene resources are explicit fixtures, not recovered runtime values.',
            'Vulkan generic vertex register bank 13 is an inspected local-sample ABI adapter, not universal support.',
            'Stencil, original postprocessing, shadow integration and same-version game pixel equivalence are NOT verified.']
    (args.output / "sample.json").write_text(json.dumps(sample, indent=2), encoding="utf-8")
    print(json.dumps({"shader": shader["name"], "material": material["name"],
                      "bindings": len(evidence), "output": str(args.output)}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--shader-report", type=Path, required=True)
    parser.add_argument("--containers", type=Path, required=True)
    parser.add_argument("--programs", type=Path, required=True)
    parser.add_argument("--decoded", type=Path, required=True)
    parser.add_argument("--cross", type=Path, required=True)
    parser.add_argument("--material-name", required=True)
    parser.add_argument("--program-index", type=int, default=0)
    parser.add_argument("--texture", type=Path)
    parser.add_argument("--textures", type=Path)
    parser.add_argument("--reuse-shader", type=Path)
    parser.add_argument("--environment", type=Path, help="Explicit isolated-preview environment; never source material values")
    parser.add_argument("--pass-lightmode",help="Explicit isolated pass; not a reconstruction of its original scheduling")
    parser.add_argument('--exact-pass-state',action='store_true',help='Rehost pass zero using exact channel/state metadata, without asserting game pass scheduling')
    parser.add_argument("--output", type=Path, required=True)
    generate(parser.parse_args())
