using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;

namespace FamiStudio
{
    public static class Compression
    {
        public const int ProjectBrotliFormatVersion = 1006;
        const uint ProjectBrotliFlag = 0x80000000;

        // Disk projects have a versioned codec. Undo and clipboard retain the
        // existing unmarked Deflate APIs below.
        public static byte[] CompressProjectBytes(byte[] buffer)
        {
            using (var stream = new MemoryStream(buffer.Length))
            {
                stream.Write(BitConverter.GetBytes((uint)buffer.Length | ProjectBrotliFlag), 0, 4);
                using (var compressor = new BrotliStream(stream, CompressionLevel.SmallestSize, true))
                    compressor.Write(buffer, 0, buffer.Length);
                return stream.ToArray();
            }
        }

        public static byte[] DecompressProjectBytes(byte[] compressedBuffer, int projectVersion)
        {
            if (compressedBuffer == null || compressedBuffer.Length < 4)
                throw new InvalidDataException("Project compressed payload header is truncated.");

            uint header = BitConverter.ToUInt32(compressedBuffer, 0);
            bool brotli = (header & ProjectBrotliFlag) != 0;
            if (brotli && projectVersion < ProjectBrotliFormatVersion)
                throw new InvalidDataException("This project version does not support Brotli compression.");

            int length = (int)(header & ~ProjectBrotliFlag);
            var buffer = new byte[length];
            if (brotli)
            {
                using (var decoder = new BrotliDecoder())
                {
                    var status = decoder.Decompress(compressedBuffer.AsSpan(4), buffer,
                        out int consumed, out int written);
                    if (status == OperationStatus.DestinationTooSmall && written == length)
                    {
                        // The decoder needs output space to consume an empty
                        // stream or a final marker after exactly filling the
                        // destination. Any actual extra byte is a size error.
                        Span<byte> finalByte = stackalloc byte[1];
                        status = decoder.Decompress(compressedBuffer.AsSpan(4 + consumed), finalByte,
                            out int finalConsumed, out int finalWritten);
                        consumed += finalConsumed;
                        if (finalWritten != 0)
                            throw new InvalidDataException("Project Brotli payload exceeds its declared length.");
                    }
                    if (status != OperationStatus.Done || written != length || consumed != compressedBuffer.Length - 4)
                        throw new InvalidDataException("Project Brotli payload is incomplete or has an invalid length.");
                }
            }
            else
            {
                // Versions through1005 use Deflate. Unmarked1006 projects are
                // accepted as well, including files from development builds.
                using (var input = new MemoryStream(compressedBuffer, 4, compressedBuffer.Length - 4, false))
                using (var decoder = new DeflateStream(input, CompressionMode.Decompress))
                {
                    int totalRead = 0;
                    while (totalRead < length)
                    {
                        int read = decoder.Read(buffer, totalRead, length - totalRead);
                        if (read == 0)
                            throw new InvalidDataException("Project Deflate payload is shorter than its declared length.");
                        totalRead += read;
                    }
                    if (decoder.ReadByte() != -1)
                        throw new InvalidDataException("Project Deflate payload exceeds its declared length.");
                }
            }
            return buffer;
        }

        public static byte[] CompressBytes(byte[] buffer, CompressionLevel level)
        {
            var stream = new MemoryStream(buffer.Length);
            stream.Write(BitConverter.GetBytes(buffer.Length), 0, 4);
            DeflateStream compStream = new DeflateStream(stream, level, true);
            compStream.Write(buffer, 0, buffer.Length);
            compStream.Close();
            var compressedBuffer = new byte[stream.Length];
            Array.Copy(stream.GetBuffer(), compressedBuffer, compressedBuffer.Length);
            stream.Close();
            return compressedBuffer;
        }

        public static byte[] DecompressBytes(byte[] compressedBuffer, int offset = 0)
        {
            var inputStream = new MemoryStream(compressedBuffer);
            inputStream.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[4];
            inputStream.Read(bytes, 0, 4);
            var decompressedSize = BitConverter.ToInt32(bytes, 0);
            DeflateStream compStream = new DeflateStream(inputStream, CompressionMode.Decompress);
            var buffer = new byte[decompressedSize];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = compStream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
            compStream.Close();
            return buffer;
        }
    }
}
