using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Smolv;

if (args.Length != 2) throw new ArgumentException("StaticShaderDecode <program.bin> <output-directory>");
byte[] program = File.ReadAllBytes(args[0]);
Directory.CreateDirectory(args[1]);
int headerEnd = program.Length;
var records = new List<object>();
for (int index = 0; index < 6 && 4 + index * 8 < headerEnd; index++)
{
    int position = 4 + index * 8;
    if (position + 8 > program.Length) throw new InvalidDataException("Truncated snippet table");
    int offset = BinaryPrimitives.ReadInt32LittleEndian(program.AsSpan(position, 4));
    int length = BinaryPrimitives.ReadInt32LittleEndian(program.AsSpan(position + 4, 4));
    if (length == 0) continue;
    if (offset < position + 8 || length < 24 || length > program.Length || offset > program.Length - length)
        throw new InvalidDataException("Snippet range exceeds program");
    headerEnd = Math.Min(headerEnd, offset);
    byte[] encoded = program.AsSpan(offset, length).ToArray();
    int decodedSize = SmolvDecoder.GetDecodedBufferSize(encoded);
    if (decodedSize <= 0 || decodedSize > 16 * 1024 * 1024)
        throw new InvalidDataException("Invalid or oversized SMOL-V header");
    byte[] decoded = SmolvDecoder.Decode(encoded) ?? throw new InvalidDataException("SMOL-V decode failed");
    if (decoded.Length != decodedSize || BinaryPrimitives.ReadUInt32LittleEndian(decoded) != 0x07230203)
        throw new InvalidDataException("Decoded program is not SPIR-V");
    string path = Path.Combine(args[1], $"Snippet{index}.spv");
    File.WriteAllBytes(path, decoded);
    records.Add(new { snippetIndex = index, offset, length, decodedSize,
        encodedSha256 = Convert.ToHexString(SHA256.HashData(encoded)),
        spirvSha256 = Convert.ToHexString(SHA256.HashData(decoded)), file = Path.GetFileName(path) });
}
if (records.Count == 0) throw new InvalidDataException("No decodable snippets");
var report = new { schema = "my-hook.static-smolv-decode.v1", source = Path.GetFullPath(args[0]),
    sourceSha256 = Convert.ToHexString(SHA256.HashData(program)), snippets = records };
File.WriteAllText(Path.Combine(args[1], "snippet-decode.json"), JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Decoded {records.Count} real SPIR-V snippets.");
