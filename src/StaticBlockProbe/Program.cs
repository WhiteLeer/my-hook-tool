using AnimeStudio;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length < 2) throw new ArgumentException("StaticBlockProbe <input file/folder> <output> [--extract-all]");
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
bool extract = args.Contains("--extract-all");
bool resources = args.Contains("--resource-streams");
bool details = args.Contains("--character-details");
int selectOption=Array.IndexOf(args,"--select-containers");
var selected=selectOption<0?null:args[selectOption+1].Split(',').ToHashSet(StringComparer.Ordinal);
int limitOption = Array.IndexOf(args,"--max-container-mib");
long containerLimit = (limitOption >= 0 ? int.Parse(args[limitOption+1]) : 256)*1024L*1024L;
if (containerLimit <= 0 || containerLimit > 1024L*1024*1024) throw new ArgumentException("Container budget must be 1..1024 MiB.");
Directory.CreateDirectory(output);
string[] inputs = File.Exists(input) ? new[] {input} : Directory.GetFiles(input,"*.block").Order().ToArray();
if (inputs.Length == 0 || inputs.Length > 4096) throw new InvalidDataException("Select 1..4096 explicit blocks.");
TypeFlags.SetOnly(Array.Empty<ClassIDType>());
if(details) TypeFlags.SetOnly(new[] {ClassIDType.GameObject,ClassIDType.Transform,ClassIDType.Animator,ClassIDType.SkinnedMeshRenderer,
    ClassIDType.Mesh,ClassIDType.Material,ClassIDType.Texture2D,ClassIDType.AnimationClip,ClassIDType.Avatar,ClassIDType.AnimatorOverrideController});
Logger.Default = new ConsoleLogger();
Logger.Flags = LoggerEvent.Warning | LoggerEvent.Error;
using var writer = new StreamWriter(Path.Combine(output,"inventory.ndjson"),false,new UTF8Encoding(false));
foreach (string path in inputs)
{
    if (new FileInfo(path).Length > 256L*1024*1024) throw new InvalidDataException("Input exceeds bounded probe size.");
    var manager = new AssetsManager { Game = GameManager.GetGame("SR"), SkipProcess = !details, ResolveDependencies = false };
    try
    {
        manager.LoadFiles(path);
        var files = new List<object>();
        foreach (var file in manager.assetsFileList)
        {
            var rows = new List<object>();
            foreach (var obj in file.m_Objects)
            {
                string? name = null, error = null;
                List<object>? components = null;
                string hash = Convert.ToHexString(obj.serializedType.m_OldTypeHash ?? Array.Empty<byte>()).ToLowerInvariant();
                if (obj.classID == 1 && hash == "86e34eb8ee7642b089fb98c9d81dd9b0")
                {
                    try
                    {
                        if (file.header.m_Endianess != 0 || obj.byteSize > 256*1024 || obj.byteSize < 15)
                            throw new InvalidDataException("Unsupported GameObject encoding or size.");
                        file.reader.Position = obj.byteStart;
                        byte[] raw = file.reader.ReadBytes(checked((int)obj.byteSize));
                        int count = BinaryPrimitives.ReadInt32LittleEndian(raw);
                        if (count < 0 || count > 4096 || 4L+12L*count+8 > raw.Length) throw new InvalidDataException("Invalid bounded component count.");
                        components = new List<object>();
                        for (int i = 0; i < count; i++) components.Add(new {
                            fileId = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4+i*12)),
                            pathId = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(8+i*12)) });
                        int nameOffset = 4 + count*12 + 4;
                        int size = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(nameOffset));
                        if (size < 0 || size > 65536 || nameOffset+4L+size > raw.Length) throw new InvalidDataException("Invalid bounded GameObject name.");
                        name = new UTF8Encoding(false,true).GetString(raw,nameOffset+4,size);
                        int end = (nameOffset+4+size+3)&~3;
                        // The validated release schema ends in Tag (uint16) and IsActive (bool).
                        if (end+3 != raw.Length || raw[end+2]>1) throw new InvalidDataException("GameObject schema did not consume object.");
                    }
                    catch (Exception e) { error = e.Message; name = null; components = null; }
                }
                else if(obj.classID==74)
                {
                    try
                    {
                        file.reader.Position=obj.byteStart;
                        int length=file.reader.ReadInt32();
                        if(length<0||length>65536||length+4>obj.byteSize)throw new InvalidDataException("Bounded NamedObject string failed");
                        name=new UTF8Encoding(false,true).GetString(file.reader.ReadBytes(length));
                    }
                    catch(Exception e){error=e.Message;name=null;}
                }
                rows.Add(new {pathId=obj.m_PathID,classId=obj.classID,byteSize=obj.byteSize,byteOffset=obj.byteStart,typeHash=hash,name,components,error});
            }
            string? containerHash = null;
            if (extract && (selected==null || selected.Contains(file.fileName)))
            {
                string name = Path.GetFileName(file.fileName);
                if (name != file.fileName || file.reader.BaseStream.Length > containerLimit) throw new InvalidDataException($"Unsafe or oversized container: {file.fileName}, bytes={file.reader.BaseStream.Length}.");
                Directory.CreateDirectory(Path.Combine(output,"containers"));
                string target = Path.Combine(output,"containers",name);
                file.reader.Position = 0;
                containerHash = Convert.ToHexString(SHA256.HashData(file.reader.BaseStream)).ToLowerInvariant();
                if (File.Exists(target))
                {
                    using var existing = File.OpenRead(target);
                    if (Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant()!=containerHash) throw new InvalidDataException("Conflicting container identity.");
                }
                else { file.reader.Position=0; using var destination=File.Create(target); file.reader.BaseStream.CopyTo(destination); }
            }
            files.Add(new {sourceFile=file.fileName,blockOffset=file.offset,unityVersion=file.unityVersion,
                serializedVersion=(int)file.header.m_Version,containerHash,containerBytes=file.reader.BaseStream.Length,
                externals=file.m_Externals.Select(e => e.pathName),objects=rows});
        }
        using var source = File.OpenRead(path);
        var result = new {sourceBlock=path,sha256=Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(),
            inputBytes=source.Length,files,asAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(AssetsManager).Assembly.Location))).ToLowerInvariant()};
        writer.WriteLine(JsonSerializer.Serialize(result)); writer.Flush();
        Console.WriteLine(JsonSerializer.Serialize(new {block=Path.GetFileName(path),files=manager.assetsFileList.Count,
            particles=manager.assetsFileList.Sum(f => f.m_Objects.Count(o => o.classID==198)),
            objects=manager.assetsFileList.Sum(f => f.m_Objects.Count)}));
        if (files.Count == 0) throw new InvalidDataException("AS produced no serialized containers.");
        if(args.Contains("--animation-audit"))
        {
            var audits=new List<object>();
            foreach(var file in manager.assetsFileList)
            foreach(var info in file.m_Objects.Where(o=>o.classID==74))
            {
                var r=new ObjectReader(file.reader,file,info,manager.Game);r.Reset();
                string name=r.ReadAlignedString();
                bool legacy=r.ReadBoolean(),compressed=r.ReadBoolean(),hq=r.ReadBoolean();r.AlignStream();
                for(int i=0;i<7;i++) if(r.ReadInt32()!=0) throw new InvalidDataException("Bounded audit expects baked-only arrays.");
                float rate=r.ReadSingle();int wrap=r.ReadInt32();var bounds=new AABB(r);
                uint muscleBytes=r.ReadUInt32();long muscleStart=r.Position;
                _=new HumanPose(r);
                for(int i=0;i<4;i++) _=r.ReadXForm();
                _=r.ReadVector3();var clip=new Clip(r);
                float start=r.ReadSingle(),stop=r.ReadSingle();
                for(int i=0;i<4;i++) _=r.ReadSingle();
                long indexOffset=r.Position;
                var candidates=new List<object>();
                foreach(int width in new[]{4,2})
                {
                    try
                    {
                        r.Position=indexOffset;int count=r.ReadInt32();
                        if(count<0||count>16384||count*width>r.BytesLeft()) throw new InvalidDataException("Index budget");
                        int[] indices=new int[count];for(int i=0;i<count;i++) indices[i]=width==2?r.ReadInt16():r.ReadInt32();
                        int deltas=r.ReadInt32();if(deltas<0||deltas>16384||deltas*8>r.BytesLeft())throw new InvalidDataException("Delta range");
                        for(int i=0;i<deltas*2;i++) if(!float.IsFinite(r.ReadSingle()))throw new InvalidDataException("Nonfinite delta");
                        int poses=r.ReadInt32();if(poses<0||poses>16384||poses*4>r.BytesLeft())throw new InvalidDataException("Pose range");
                        for(int i=0;i<poses;i++) if(!float.IsFinite(r.ReadSingle()))throw new InvalidDataException("Nonfinite pose");
                        for(int i=0;i<11;i++) if(r.ReadByte()>1)throw new InvalidDataException("Muscle boolean");
                        r.AlignStream();long muscleEnd=r.Position;
                        byte[] acl=r.ReadUInt8Array();int aclBindings=r.ReadInt32();
                        if(aclBindings<0||aclBindings>16384)throw new InvalidDataException("ACL binding budget");
                        for(int i=0;i<aclBindings;i++) _=new GenericBinding(r);
                        _=r.ReadSingle();_=r.ReadSingle();var bindings=new AnimationClipBindingConstant(r);
                        for(int i=0;i<2;i++) if(r.ReadByte()>1)throw new InvalidDataException("Clip boolean");
                        r.AlignStream();int events=r.ReadInt32();if(events<0||events>1024)throw new InvalidDataException("Event budget");
                        for(int i=0;i<events;i++)_=new AnimationEvent(r);r.AlignStream();
                        candidates.Add(new{width,status=r.BytesLeft()==0?"exact_object_consumption":"trailing_bytes",count,indices,deltas,poses,
                            muscleDeclared=muscleBytes,muscleConsumed=muscleEnd-muscleStart,bindings=bindings.genericBindings.Count,
                            aclBytes=acl.Length,remaining=r.BytesLeft(),indexOffset=indexOffset-info.byteStart});
                    }
                    catch(Exception e){candidates.Add(new{width,status="rejected",error=e.Message,offset=r.Position-info.byteStart});}
                }
                audits.Add(new{name,pathId=info.m_PathID,typeHash=Convert.ToHexString(info.serializedType.m_OldTypeHash),
                    byteSize=info.byteSize,rate,start,stop,aclCurveCount=clip.m_ACLClip.CurveCount,candidates});
            }
            File.WriteAllText(Path.Combine(output,"animation-layout-audit.json"),JsonSerializer.Serialize(audits,new JsonSerializerOptions{WriteIndented=true}));
        }
        if(details)
        {
            object Pointer<T>(PPtr<T>? p) where T:AnimeStudio.Object => p==null?new {missing=true}:(object)new {
                fileId=p.m_FileID,pathId=p.m_PathID,sourceFile=p.SourceFileName,resolved=p.TryGet(out _)};
            var rows=manager.assetsFileList.SelectMany(f=>f.Objects).OfType<SkinnedMeshRenderer>().Select(r=>new {
                sourceFile=r.assetsFile.fileName,pathId=r.m_PathID,gameObject=Pointer(r.m_GameObject),
                mesh=Pointer(r.m_Mesh),bones=r.m_Bones.Select(p=>Pointer(p)),rootBone=Pointer(r.m_RootBone),
                materials=r.m_Materials.Select(p=>Pointer(p)),r.m_BlendShapeWeights});
            File.WriteAllText(Path.Combine(output,"character-details.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions {WriteIndented=true}));
        }
        if (resources)
        {
            using var reader = new FileReader(path);
            using var stream = new OffsetStream(reader.BaseStream,0);
            foreach (long offset in stream.GetOffsets(path))
            {
                using var sub = new FileReader(Path.Combine(Path.GetDirectoryName(path)!,offset.ToString("X8")),stream,true);
                var bundle = new BundleFile(sub,manager.Game);
                foreach (var entry in bundle.fileList)
                {
                    using var content = entry.stream;
                    if (!entry.fileName.EndsWith(".resS",StringComparison.Ordinal)) continue;
                    if(selected!=null && !selected.Contains(entry.fileName) && !selected.Contains(entry.fileName[..^5])) continue;
                    if (entry.fileName!=Path.GetFileName(entry.fileName)||content.Length>containerLimit) throw new InvalidDataException("Unsafe resource stream");
                    string target=Path.Combine(output,"containers",entry.fileName);
                    using var destination=File.Create(target);content.Position=0;content.CopyTo(destination);
                }
            }
        }
    }
    finally { manager.Clear(); }
}
