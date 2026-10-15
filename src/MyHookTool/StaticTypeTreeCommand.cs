using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace MyHookTool;

internal static class StaticTypeTreeCommand
{
    private const ulong SectionAlloc = 0x2;
    private const ulong SectionExecInstr = 0x4;

    private static readonly string[] DefaultAnchors =
    [
        "Source and Destination Types do not match",
        "Failure to create component of type",
        "ALLOC_TYPETREE",
        "ALLOC_TYPETREE_MAIN",
        "ALLOC_TYPETREE_THREAD",
        "TypeTree",
        "TypeTreeShareableData"
    ];

    public static int Run(string[] args)
    {
        var options = ParseOptions(args);
        var positionals = Positionals(options);
        if (positionals.Count != 1)
            throw new ArgumentException("typetree-static 需要一个 ELF/so 路径");

        var inputPath = Path.GetFullPath(positionals[0]);
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("找不到 ELF/so", inputPath);

        var outputPath = OptionValue(options, "output") is { } output
            ? Path.GetFullPath(output)
            : Path.ChangeExtension(inputPath, ".typetree-static.json");
        var windowBytes = ParseBoundedInt(OptionValue(options, "window-bytes"), 0x400, 0x40, 0x4000);
        var maxReferences = ParseBoundedInt(OptionValue(options, "max-references"), 256, 1, 4096);
        var identityTarget = ParseOptionalHex(OptionValue(options, "identity-target"));
        var anchorValues = OptionValues(options, "anchor");
        if (anchorValues.Count == 0)
            anchorValues.AddRange(DefaultAnchors);
        if (anchorValues.Any(string.IsNullOrEmpty))
            throw new ArgumentException("--anchor 不能为空");

        var bytes = File.ReadAllBytes(inputPath);
        var elf = ElfFile.Read(bytes);
        var report = BuildReport(inputPath, bytes, elf, windowBytes, maxReferences, anchorValues,
            identityTarget);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, report.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        }));

        var summary = report["summary"]!.AsObject();
        Console.WriteLine($"静态 TypeTree 候选报告: {outputPath}");
        Console.WriteLine($"ELF: {elf.ClassName}, {elf.MachineName}; sections {elf.Sections.Count}");
        Console.WriteLine($"锚点: {summary["anchorCount"]}, 引用: {summary["referenceCount"]}, 调用窗口: {summary["callWindowCount"]}");
        Console.WriteLine("注意: 这些是静态候选证据，不代表已确认的 TypeTree 生成入口，也未调用客体函数。");
        return 0;
    }

    private static JsonObject BuildReport(string inputPath, byte[] bytes, ElfFile elf,
        int windowBytes, int maxReferences, IReadOnlyList<string> anchorValues,
        ulong? identityTargetRva)
    {
        var anchors = new JsonArray();
        var anchorAddresses = new Dictionary<ulong, string>();
        foreach (var value in anchorValues)
        {
            var matches = new JsonArray();
            foreach (var section in elf.Sections.Where(section => section.IsAllocated && section.IsReadable))
            {
                foreach (var fileOffset in FindAll(bytes, section.FileOffset, section.FileSize,
                    Encoding.UTF8.GetBytes(value)))
                {
                    var virtualAddress = section.VirtualAddress + (ulong)(fileOffset - section.FileOffset);
                    matches.Add(new JsonObject
                    {
                        ["section"] = section.Name,
                        ["fileOffset"] = Hex(fileOffset),
                        ["virtualAddress"] = Hex(virtualAddress)
                    });
                    anchorAddresses[virtualAddress] = value;
                }
            }

            anchors.Add(new JsonObject
            {
                ["value"] = value,
                ["matchCount"] = matches.Count,
                ["matches"] = matches
            });
        }

        var references = new JsonArray();
        foreach (var code in elf.Sections.Where(section => section.IsExecutable && section.FileSize >= 4))
        {
            for (var offset = 0UL; offset + 4 <= code.FileSize; offset += 4)
            {
                var fileOffset = code.FileOffset + offset;
                var pc = code.VirtualAddress + offset;
                var instruction = ReadUInt32(bytes, fileOffset);
                if (TryDecodeAdrTarget(instruction, pc, out var adrTarget)
                    && anchorAddresses.TryGetValue(adrTarget, out var adrAnchor))
                {
                    AddReference(references, code, fileOffset, pc, "adr", adrAnchor, adrTarget,
                        bytes, elf, windowBytes, maxReferences);
                }

                if (!TryDecodeAdrpTarget(instruction, pc, out var page) || offset + 8 > code.FileSize)
                    continue;

                var next = ReadUInt32(bytes, fileOffset + 4);
                var paired = false;
                if (IsAddImmediate(next, instruction))
                {
                    var target = page + AddImmediate(next);
                    if (anchorAddresses.TryGetValue(target, out var addAnchor))
                    {
                        AddReference(references, code, fileOffset, pc, "adrp-add", addAnchor, target,
                            bytes, elf, windowBytes, maxReferences);
                    }
                    paired = true;
                }
                else if (IsLdrUnsignedImmediate(next, instruction))
                {
                    var pointerAddress = page + (ulong)(((next >> 10) & 0xfff) << 3);
                    if (!elf.TryReadVirtual(bytes, pointerAddress, 8, out var pointerBytes))
                        continue;
                    var pointer = BinaryPrimitives.ReadUInt64LittleEndian(pointerBytes);
                    if (anchorAddresses.TryGetValue(pointer, out var ldrAnchor))
                    {
                        AddReference(references, code, fileOffset, pc, "adrp-ldr", ldrAnchor, pointer,
                            bytes, elf, windowBytes, maxReferences, pointerAddress);
                    }
                    paired = true;
                }

                if (!paired)
                {
                    // Android builds sometimes keep the ADRP live across a
                    // store or stack setup instruction. Search a small,
                    // bounded lookahead rather than assuming an adjacent pair.
                    var register = instruction & 0x1f;
                    for (var lookahead = 2UL; lookahead <= 16 && offset + lookahead * 4 + 4 <= code.FileSize; lookahead++)
                    {
                        var candidate = ReadUInt32(bytes, fileOffset + lookahead * 4);
                        if (IsAddImmediateFromRegister(candidate, register))
                        {
                            var candidateTarget = page + AddImmediate(candidate);
                            if (anchorAddresses.TryGetValue(candidateTarget, out var candidateAnchor))
                            {
                                AddReference(references, code, fileOffset, pc, "adrp-add-lookahead",
                                    candidateAnchor, candidateTarget, bytes, elf, windowBytes, maxReferences);
                            }
                            paired = true;
                            break;
                        }
                        if (IsLdrUnsignedImmediateFromRegister(candidate, register))
                        {
                            var pointerAddress = page + (ulong)(((candidate >> 10) & 0xfff) << 3);
                            if (elf.TryReadVirtual(bytes, pointerAddress, 8, out var pointerBytes))
                            {
                                var pointer = BinaryPrimitives.ReadUInt64LittleEndian(pointerBytes);
                                if (anchorAddresses.TryGetValue(pointer, out var candidateAnchor))
                                {
                                    AddReference(references, code, fileOffset, pc, "adrp-ldr-lookahead",
                                        candidateAnchor, pointer, bytes, elf, windowBytes, maxReferences, pointerAddress);
                                }
                            }
                            paired = true;
                            break;
                        }
                    }
                }

                if (!paired)
                {
                    // An ADRP can also address a string through a later
                    // register operation. Report page-level evidence without
                    // pretending that the exact string use was decoded.
                    var pageEnd = page + 0x1000;
                    foreach (var pair in anchorAddresses.Where(pair => pair.Key >= page && pair.Key < pageEnd))
                    {
                        AddPageReference(references, code, fileOffset, pc, pair.Value, pair.Key,
                            bytes, elf, windowBytes, maxReferences, page);
                    }
                }
            }
        }

        var sectionReport = new JsonArray();
        foreach (var section in elf.Sections)
        {
            sectionReport.Add(new JsonObject
            {
                ["name"] = section.Name,
                ["fileOffset"] = Hex(section.FileOffset),
                ["fileSize"] = Hex(section.FileSize),
                ["virtualAddress"] = Hex(section.VirtualAddress),
                ["flags"] = Hex(section.Flags),
                ["readable"] = section.IsReadable,
                ["executable"] = section.IsExecutable
            });
        }

        var identityCallsites = identityTargetRva is { } targetRva
            ? FindIdentityCallsites(bytes, elf, targetRva)
            : new JsonArray();

        return new JsonObject
        {
            ["schema"] = "my-hook.typetree-static.v1",
            ["source"] = new JsonObject
            {
                ["file"] = inputPath,
                ["size"] = bytes.LongLength,
                ["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                ["elfClass"] = elf.ClassName,
                ["machine"] = elf.MachineName
            },
            ["parameters"] = new JsonObject
            {
                ["windowBytes"] = windowBytes,
                ["maxReferences"] = maxReferences,
                ["anchors"] = new JsonArray(anchorValues.Select(value => JsonValue.Create(value)).ToArray())
            },
            ["summary"] = new JsonObject
            {
                ["anchorCount"] = anchorAddresses.Count,
                ["referenceCount"] = references.Count,
                ["callWindowCount"] = references.OfType<JsonObject>().Count(reference => reference["blTargets"] is JsonArray targets && targets.Count > 0)
            },
            ["sections"] = sectionReport,
            ["anchors"] = anchors,
            ["references"] = references,
            ["identityCallsites"] = identityCallsites,
            ["limitations"] = new JsonArray(
                "Only ELF section metadata and fixed-width ARM64 instruction decoding are used.",
                "BL targets are collected from a bounded window after an anchor reference; the window is not a proven function boundary.",
                "No candidate is labeled GetTypeTree, no unknown ABI is called, and no TypeTree field is decoded.",
                "identityCallsites only prove a return-value writeback pattern; they do not assign semantics to key64.")
        };
    }

    private static JsonArray FindIdentityCallsites(byte[] bytes, ElfFile elf, ulong target)
    {
        var result = new JsonArray();
        foreach (var code in elf.Sections.Where(section => section.IsExecutable && section.FileSize >= 4))
        {
            for (var offset = 0UL; offset + 4 <= code.FileSize; offset += 4)
            {
                var fileOffset = code.FileOffset + offset;
                var pc = code.VirtualAddress + offset;
                if (!TryDecodeBlTarget(ReadUInt32(bytes, fileOffset), pc, out var callTarget)
                    || callTarget != target)
                    continue;

                for (var lookahead = 1UL; lookahead <= 8 && offset + lookahead * 4 + 4 <= code.FileSize; lookahead++)
                {
                    var store = ReadUInt32(bytes, fileOffset + lookahead * 4);
                    if (!TryDecodeStoreW0(store, out var baseRegister, out var byteOffset))
                        continue;

                    var keyRegister = -1;
                    for (var lookbehind = 1UL; lookbehind <= 4 && offset >= lookbehind; lookbehind++)
                    {
                        if (TryDecodeMoveToX1(ReadUInt32(bytes, fileOffset - lookbehind * 4), out var source))
                        {
                            keyRegister = source;
                            break;
                        }
                    }

                    result.Add(new JsonObject
                    {
                        ["callPc"] = Hex(pc),
                        ["target"] = Hex(callTarget),
                        ["returnWriteback"] = new JsonObject
                        {
                            ["instructionPc"] = Hex(pc + lookahead * 4),
                            ["baseRegister"] = $"x{baseRegister}",
                            ["byteOffset"] = Hex(byteOffset),
                            ["returnRegister"] = "w0",
                            ["keyRegisterBeforeCall"] = keyRegister >= 0 ? $"x{keyRegister}" : null,
                            ["sameRegisterEvidence"] = keyRegister == baseRegister
                        },
                        ["evidence"] = "direct-arm64-return-writeback"
                    });
                    break;
                }
            }
        }
        return result;
    }

    private static bool TryDecodeStoreW0(uint instruction, out int baseRegister, out uint byteOffset)
    {
        baseRegister = 0;
        byteOffset = 0;
        if ((instruction & 0xffc00000U) != 0xb9000000U || (instruction & 0x1f) != 0)
            return false;
        baseRegister = (int)((instruction >> 5) & 0x1f);
        byteOffset = ((instruction >> 10) & 0xfff) * 4U;
        return true;
    }

    private static bool TryDecodeMoveToX1(uint instruction, out int sourceRegister)
    {
        sourceRegister = 0;
        if ((instruction & 0xffe0ffe0U) != 0xaa0003e0U || (instruction & 0x1f) != 1)
            return false;
        sourceRegister = (int)((instruction >> 16) & 0x1f);
        return true;
    }

    private static void AddReference(JsonArray references, ElfSection code, ulong fileOffset, ulong pc,
        string referenceKind, string anchor, ulong target, byte[] bytes, ElfFile elf, int windowBytes,
        int maxReferences, ulong pointerAddress = 0)
    {
        if (references.Count >= maxReferences)
            return;

        var windowEnd = Math.Min(code.FileOffset + code.FileSize, fileOffset + (ulong)windowBytes);
        var blTargets = new JsonArray();
        var seen = new HashSet<ulong>();
        for (var cursor = fileOffset; cursor + 4 <= windowEnd; cursor += 4)
        {
            var instruction = ReadUInt32(bytes, cursor);
            var instructionPc = pc + (cursor - fileOffset);
            if (!TryDecodeBlTarget(instruction, instructionPc, out var blTarget) || !seen.Add(blTarget))
                continue;

            blTargets.Add(new JsonObject
            {
                ["target"] = Hex(blTarget),
                ["inExecutableSection"] = elf.IsExecutableAddress(blTarget)
            });
        }

        var record = new JsonObject
        {
            ["anchor"] = anchor,
            ["kind"] = referenceKind,
            ["pc"] = Hex(pc),
            ["fileOffset"] = Hex(fileOffset),
            ["target"] = Hex(target),
            ["evidence"] = "candidate",
            ["window"] = new JsonObject
            {
                ["start"] = Hex(pc),
                ["end"] = Hex(pc + (windowEnd - fileOffset)),
                ["bytes"] = windowEnd - fileOffset
            },
            ["blTargets"] = blTargets
        };
        if (pointerAddress != 0)
            record["pointerAddress"] = Hex(pointerAddress);
        references.Add(record);
    }

    private static void AddPageReference(JsonArray references, ElfSection code, ulong fileOffset, ulong pc,
        string anchor, ulong target, byte[] bytes, ElfFile elf, int windowBytes, int maxReferences, ulong page)
    {
        if (references.Count >= maxReferences)
            return;
        var windowEnd = Math.Min(code.FileOffset + code.FileSize, fileOffset + (ulong)windowBytes);
        var blTargets = new JsonArray();
        var seen = new HashSet<ulong>();
        for (var cursor = fileOffset; cursor + 4 <= windowEnd; cursor += 4)
        {
            var instruction = ReadUInt32(bytes, cursor);
            var instructionPc = pc + (cursor - fileOffset);
            if (!TryDecodeBlTarget(instruction, instructionPc, out var blTarget) || !seen.Add(blTarget))
                continue;
            blTargets.Add(new JsonObject
            {
                ["target"] = Hex(blTarget),
                ["inExecutableSection"] = elf.IsExecutableAddress(blTarget)
            });
        }
        references.Add(new JsonObject
        {
            ["anchor"] = anchor,
            ["kind"] = "adrp-page",
            ["pc"] = Hex(pc),
            ["fileOffset"] = Hex(fileOffset),
            ["target"] = Hex(target),
            ["targetPage"] = Hex(page),
            ["anchorOffsetInPage"] = Hex(target - page),
            ["evidence"] = "candidate-page-only",
            ["window"] = new JsonObject
            {
                ["start"] = Hex(pc),
                ["end"] = Hex(pc + (windowEnd - fileOffset)),
                ["bytes"] = windowEnd - fileOffset
            },
            ["blTargets"] = blTargets
        });
    }

    private static bool TryDecodeBlTarget(uint instruction, ulong pc, out ulong target)
    {
        target = 0;
        if ((instruction & 0xfc000000U) != 0x94000000U)
            return false;
        var immediate = SignExtend(instruction & 0x03ffffffU, 26);
        target = unchecked((ulong)((long)pc + immediate * 4));
        return true;
    }

    private static bool TryDecodeAdrpTarget(uint instruction, ulong pc, out ulong target)
    {
        target = 0;
        if ((instruction & 0x9f000000U) != 0x90000000U)
            return false;
        var immediate = ((long)((instruction >> 5) & 0x7ffff) << 2) | ((instruction >> 29) & 0x3);
        target = unchecked((ulong)((long)(pc & ~0xfffUL) + SignExtend(immediate, 21) * 0x1000));
        return true;
    }

    private static bool TryDecodeAdrTarget(uint instruction, ulong pc, out ulong target)
    {
        target = 0;
        if ((instruction & 0x9f000000U) != 0x10000000U)
            return false;
        var immediate = ((long)((instruction >> 5) & 0x7ffff) << 2) | ((instruction >> 29) & 0x3);
        target = unchecked((ulong)((long)pc + SignExtend(immediate, 21)));
        return true;
    }

    private static long SignExtend(long value, int bits)
    {
        var shift = 64 - bits;
        return (value << shift) >> shift;
    }

    private static bool IsAddImmediate(uint instruction, uint previous)
    {
        return (instruction & 0x9f000000U) == 0x91000000U
            && ((instruction >> 5) & 0x1f) == (previous & 0x1f);
    }

    private static bool IsAddImmediateFromRegister(uint instruction, uint sourceRegister)
    {
        return (instruction & 0x9f000000U) == 0x91000000U
            && ((instruction >> 5) & 0x1f) == sourceRegister;
    }

    private static ulong AddImmediate(uint instruction)
    {
        var immediate = (ulong)((instruction >> 10) & 0xfff);
        return immediate << (((instruction >> 22) & 1) != 0 ? 12 : 0);
    }

    private static bool IsLdrUnsignedImmediate(uint instruction, uint previous)
    {
        return (instruction & 0xffc00000U) == 0xf9400000U
            && ((instruction >> 5) & 0x1f) == (previous & 0x1f);
    }

    private static bool IsLdrUnsignedImmediateFromRegister(uint instruction, uint sourceRegister)
    {
        return (instruction & 0xffc00000U) == 0xf9400000U
            && ((instruction >> 5) & 0x1f) == sourceRegister;
    }

    private static uint ReadUInt32(byte[] bytes, ulong offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(checked((int)offset), 4));

    private static IEnumerable<ulong> FindAll(byte[] bytes, ulong offset, ulong size, byte[] needle)
    {
        if (needle.Length == 0 || size < (ulong)needle.Length)
            yield break;
        var start = checked((int)offset);
        var end = checked((int)(offset + size - (ulong)needle.Length));
        for (var index = start; index <= end; index++)
        {
            if (!bytes.AsSpan(index, needle.Length).SequenceEqual(needle))
                continue;
            yield return (ulong)index;
        }
    }

    private static int ParseBoundedInt(string? value, int fallback, int minimum, int maximum)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && int.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
                value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? System.Globalization.NumberStyles.AllowHexSpecifier
                    : System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed >= minimum && parsed <= maximum)
            return parsed;
        return fallback;
    }

    private static ulong? ParseOptionalHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;
        return ulong.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"无效的十六进制地址: {value}");
    }

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["__positionals"] = []
        };
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var key = arg[2..];
                var value = "true";
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    value = args[++i];
                if (!result.TryGetValue(key, out var values))
                    result[key] = values = [];
                values.Add(value);
            }
            else
            {
                result["__positionals"].Add(arg);
            }
        }
        return result;
    }

    private static List<string> Positionals(Dictionary<string, List<string>> options) =>
        options.TryGetValue("__positionals", out var values) ? values : [];

    private static string? OptionValue(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values) && values.Count > 0 ? values[^1] : null;

    private static List<string> OptionValues(Dictionary<string, List<string>> options, string key) =>
        options.TryGetValue(key, out var values) ? [.. values] : [];

    private sealed class ElfFile
    {
        public required string ClassName { get; init; }
        public required string MachineName { get; init; }
        public required List<ElfSection> Sections { get; init; }

        public static ElfFile Read(byte[] bytes)
        {
            if (bytes.Length < 64 || bytes[0] != 0x7f || bytes[1] != (byte)'E'
                || bytes[2] != (byte)'L' || bytes[3] != (byte)'F')
                throw new InvalidDataException("输入不是 ELF 文件");
            if (bytes[4] != 2 || bytes[5] != 1)
                throw new InvalidDataException("仅支持 ELF64 little-endian");

            var machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18, 2));
            if (machine != 183)
                throw new InvalidDataException($"仅支持 AArch64 ELF，实际 machine={machine}");

            var sectionOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40, 8));
            var sectionEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(58, 2));
            var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(60, 2));
            var stringTableIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(62, 2));
            if (sectionEntrySize < 64 || sectionCount == 0
                || !RangeFits(bytes, sectionOffset, (ulong)sectionEntrySize * sectionCount))
                throw new InvalidDataException("ELF section table 不可读");

            var rawSections = new List<(uint Name, uint Type, ulong Flags, ulong Address, ulong Offset, ulong Size)>();
            for (var index = 0; index < sectionCount; index++)
            {
                var offset = checked((int)(sectionOffset + (ulong)index * sectionEntrySize));
                rawSections.Add((
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4)),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8, 8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 16, 8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 24, 8)),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 32, 8))));
            }

            if (stringTableIndex >= rawSections.Count)
                throw new InvalidDataException("ELF section name table 不可用");
            var names = rawSections[stringTableIndex];
            if (!RangeFits(bytes, names.Offset, names.Size))
                throw new InvalidDataException("ELF section name table 越界");

            string GetName(uint nameOffset)
            {
                if (nameOffset >= names.Size)
                    return string.Empty;
                var start = checked((int)(names.Offset + nameOffset));
                var end = start;
                var limit = checked((int)(names.Offset + names.Size));
                while (end < limit && bytes[end] != 0) end++;
                return Encoding.UTF8.GetString(bytes, start, end - start);
            }

            var sections = rawSections
                .Select(raw => new ElfSection
                {
                    Name = GetName(raw.Name),
                    FileOffset = raw.Offset,
                    FileSize = raw.Size,
                    VirtualAddress = raw.Address,
                    Flags = raw.Flags,
                    IsAllocated = (raw.Flags & SectionAlloc) != 0,
                    IsReadable = (raw.Flags & SectionAlloc) != 0,
                    IsExecutable = (raw.Flags & SectionExecInstr) != 0
                })
                .Where(section => section.FileSize == 0 || RangeFits(bytes, section.FileOffset, section.FileSize))
                .ToList();

            return new ElfFile
            {
                ClassName = "ELF64",
                MachineName = "AArch64",
                Sections = sections
            };
        }

        public bool IsExecutableAddress(ulong address) => Sections.Any(section =>
            section.IsExecutable && address >= section.VirtualAddress
            && address - section.VirtualAddress < section.FileSize);

        public bool TryReadVirtual(byte[] bytes, ulong address, int size, out ReadOnlySpan<byte> value)
        {
            foreach (var section in Sections)
            {
                if (address < section.VirtualAddress || address - section.VirtualAddress + (ulong)size > section.FileSize)
                    continue;
                var fileOffset = section.FileOffset + address - section.VirtualAddress;
                value = bytes.AsSpan(checked((int)fileOffset), size);
                return true;
            }
            value = default;
            return false;
        }

        private static bool RangeFits(byte[] bytes, ulong offset, ulong size) =>
            offset <= (ulong)bytes.Length && size <= (ulong)bytes.Length - offset;
    }

    private sealed class ElfSection
    {
        public required string Name { get; init; }
        public required ulong FileOffset { get; init; }
        public required ulong FileSize { get; init; }
        public required ulong VirtualAddress { get; init; }
        public required ulong Flags { get; init; }
        public required bool IsAllocated { get; init; }
        public required bool IsReadable { get; init; }
        public required bool IsExecutable { get; init; }
    }

    private static string Hex(ulong value) => $"0x{value:x}";
}
