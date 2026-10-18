using System.Buffers.Binary;
using System.Security.Cryptography;
using Sia.Asset;
using Sia.Math;

namespace Sia.Engine.Rendering.Pbr;

public sealed partial class PbrLightmapSetData
{
    public void Save(Stream stream,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        Span<byte> header=stackalloc byte[200]; "SIALMSET"u8.CopyTo(header);
        ProductId.Value.TryWriteBytes(header.Slice(8,16),bigEndian:true,out _);
        _=Convert.FromHexString(SceneIdentity,header.Slice(24,32),out _,out _);
        _=Convert.FromHexString(BakeIdentity,header.Slice(56,32),out _,out _);
        BinaryPrimitives.WriteInt32LittleEndian(header[88..],_products.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[92..],_receivers.Length); BakeSettings.Encode(header[96..]);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header); stream.Write(header);
        Span<byte> row=stackalloc byte[152];
        foreach (var product in _products) {
            cancellationToken.ThrowIfCancellationRequested();
            product.Id.Value.TryWriteBytes(row[..16],bigEndian:true,out _);
            _=Convert.FromHexString(product.ContentHash,row.Slice(16,32),out _,out _);
            _=Convert.FromHexString(product.BakeIdentity,row.Slice(48,32),out _,out _);
            BinaryPrimitives.WriteInt32LittleEndian(row[80..],product.EncodedBytes); hash.AppendData(row[..84]); stream.Write(row[..84]);
        }
        foreach (var entry in _receivers) {
            cancellationToken.ThrowIfCancellationRequested(); entry.Receiver.Encode(row[..120]);
            BinaryPrimitives.WriteInt32LittleEndian(row[120..],entry.Product);
            BinaryPrimitives.WriteInt32LittleEndian(row[124..],entry.ProductReceiver);
            for (var component=0;component<4;component++) for (var channel=0;channel<3;channel++)
                BinaryPrimitives.WriteInt16LittleEndian(row[(128+component*6+channel*2)..],
                    entry.Receiver.Quantization.Encode(component,channel,entry.Coarse.Component(component)[channel]));
            hash.AppendData(row); stream.Write(row);
        }
        cancellationToken.ThrowIfCancellationRequested(); Span<byte> footer=stackalloc byte[32]; hash.GetHashAndReset(footer); stream.Write(footer);
    }

    public static PbrLightmapSetData Load(Stream stream,ulong maximumBytes=MaximumPayloadBytes,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(stream); cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes<232) throw new InvalidDataException("Lighting set header exceeds its decode budget.");
        try {
            Span<byte> header=stackalloc byte[200]; stream.ReadExactly(header);
            if (!header[..8].SequenceEqual("SIALMSET"u8)) throw new InvalidDataException("Invalid lighting set signature.");
            var productCount=BinaryPrimitives.ReadInt32LittleEndian(header[88..]); var receiverCount=BinaryPrimitives.ReadInt32LittleEndian(header[92..]);
            Admit(productCount,receiverCount,maximumBytes);
            var remaining=(long)productCount*84+(long)receiverCount*152+32;
            if (stream.CanSeek && stream.Length-stream.Position!=remaining) throw new InvalidDataException("Lighting set length differs from its counts.");
            var id=new AssetId(new Guid(header.Slice(8,16),bigEndian:true));
            var scene=Convert.ToHexString(header.Slice(24,32)); var bake=Convert.ToHexString(header.Slice(56,32));
            var settings=PbrLightmapBakeSettings.Decode(header[96..]);
            cancellationToken.ThrowIfCancellationRequested(); using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
            var products=new PbrLightmapProductReference[productCount]; var receivers=new PbrLightmapReceiverReference[receiverCount];
            Span<byte> row=stackalloc byte[152]; Span<float3> values=stackalloc float3[4];
            for (var at=0;at<products.Length;at++) {
                cancellationToken.ThrowIfCancellationRequested(); stream.ReadExactly(row[..84]); hash.AppendData(row[..84]);
                products[at]=new(new(new Guid(row[..16],bigEndian:true)),Convert.ToHexString(row.Slice(16,32)),
                    BinaryPrimitives.ReadInt32LittleEndian(row[80..]),Convert.ToHexString(row.Slice(48,32)));
            }
            for (var at=0;at<receivers.Length;at++) {
                cancellationToken.ThrowIfCancellationRequested(); stream.ReadExactly(row); hash.AppendData(row);
                var receiver=PbrLightmapReceiver.Decode(row[..120]);
                for (var component=0;component<4;component++) for (var channel=0;channel<3;channel++) {
                    var code=BinaryPrimitives.ReadInt16LittleEndian(row[(128+component*6+channel*2)..]);
                    if (code==short.MinValue) throw new InvalidDataException("Invalid signed lighting support code.");
                    values[component][channel]=receiver.Quantization.Decode(component,channel,code);
                }
                receivers[at]=new(receiver,BinaryPrimitives.ReadInt32LittleEndian(row[120..]),BinaryPrimitives.ReadInt32LittleEndian(row[124..]),
                    new(values[0],values[1],values[2],values[3],true));
            }
            Span<byte> footer=stackalloc byte[32]; stream.ReadExactly(footer);
            if (!CryptographicOperations.FixedTimeEquals(footer,hash.GetHashAndReset()) || stream.ReadByte()!=-1)
                throw new InvalidDataException("Lighting set checksum or EOF validation failed.");
            cancellationToken.ThrowIfCancellationRequested(); var result=new PbrLightmapSetData(id,scene,settings,products,receivers,cancellationToken);
            if (bake!=result.BakeIdentity) throw new InvalidDataException("Lighting set input identity differs from its declared dependencies.");
            return result;
        }
        catch (EndOfStreamException error) { throw new InvalidDataException("Truncated lighting set.",error); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid lighting set data.",error); }
    }
}
