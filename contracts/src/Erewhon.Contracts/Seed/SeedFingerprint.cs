using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Erewhon.Contracts.Seed;

/// <summary>
/// The single fingerprint of a seed directory. The stand-in reports it as <c>seedSha256</c> in
/// <c>GET /admin/info</c>, and the harness computes it over its own copy of the seed files and
/// requires the two values to be equal, so both call this class and neither re-implements it.
/// </summary>
/// <remarks>
/// <para>The algorithm (<see cref="Algorithm"/>) is fixed, because baselines record its output:</para>
/// <list type="number">
///   <item><description>Take every file directly in the directory
///   (<see cref="SearchOption.TopDirectoryOnly"/>, hidden files included). Subdirectories and
///   their contents are ignored.</description></item>
///   <item><description>Order the files by file name alone, never by full path, using
///   <see cref="StringComparer.Ordinal"/> (so <c>ANQU0010.jpg</c> and <c>Tenders.txt</c> sort
///   before <c>aerials.jpg</c>). The result therefore does not depend on where the directory lives
///   or on the file system's enumeration order.</description></item>
///   <item><description>Feed one SHA-256 computation, for each file in that order, with: the UTF-8
///   bytes of the file name (no BOM); one <c>0x00</c> byte; the content length in bytes as a
///   little-endian signed 64-bit integer (8 bytes); the raw content bytes. Content is never
///   decoded or normalised. The length prefix keeps a byte moved from one file to the next, or an
///   added empty file, from producing the same digest.</description></item>
///   <item><description>Return the digest as 64 lowercase hexadecimal characters.</description></item>
/// </list>
/// <para>Files are opened for reading only and shared for reading, so the hashed directory is never
/// written or locked against readers. The class holds no state and is safe to call concurrently.</para>
/// </remarks>
public static class SeedFingerprint
{
    /// <summary>Descriptor of the algorithm <see cref="ComputeSha256Hex"/> implements.</summary>
    public const string Algorithm = "sha256-name-nul-len64le-bytes-v1";

    private const int ReadBufferSize = 81920;

    /// <summary>Computes the seed fingerprint of <paramref name="directory"/>.</summary>
    /// <param name="directory">The seed directory, absolute or relative to the current directory.</param>
    /// <returns>The SHA-256 digest as 64 lowercase hexadecimal characters.</returns>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is null.</exception>
    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    /// <exception cref="IOException">
    /// A file changed length while it was being hashed, or the directory could not be listed or a
    /// file in it could not be opened or read.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A file in the directory cannot seek, so its length cannot be taken, such as a FIFO once a
    /// writer has opened it.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Permission to list the directory, or to open a file in it for reading, is denied.
    /// </exception>
    public static string ComputeSha256Hex(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Seed directory '{Path.GetFullPath(directory)}' does not exist.");
        }

        var files = Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Select(path => (Name: Path.GetFileName(path), Path: path))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthPrefix = stackalloc byte[sizeof(long)];
        var buffer = new byte[ReadBufferSize];

        foreach (var (name, path) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData([0x00]);

            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 0,
                Options = FileOptions.SequentialScan,
            });

            // The prefix is written before the content is read, so the bytes actually read must
            // match it; otherwise the digest would describe a file that never existed.
            var length = stream.Length;
            BinaryPrimitives.WriteInt64LittleEndian(lengthPrefix, length);
            hash.AppendData(lengthPrefix);

            long read = 0;
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, count);
                read += count;
            }

            if (read != length)
            {
                throw new IOException(
                    $"Seed file '{Path.GetFullPath(path)}' changed while it was being hashed: " +
                    $"expected {length} bytes, read {read}.");
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
