using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DoubleDragon.Unpack
{
    /// <summary>
    /// Xbox 360 STFS 内容包（.LIVE / .PIRS / .CON）只读解析器。
    ///
    /// 用法：
    /// <code>
    /// using (var pkg = StfsPackage.Open(path))
    /// {
    ///     foreach (var e in pkg.Entries) Console.WriteLine(e);
    ///     pkg.ExtractTo(destDir, progress: new Progress&lt;ExtractProgress&gt;(p => ...));
    /// }
    /// </code>
    ///
    /// 只做只读解析：不改包、不重新签名、不做任何授权绕过。
    /// </summary>
    public sealed class StfsPackage : IDisposable
    {
        /// <summary>STFS 的块大小。</summary>
        public const int BlockSize = 0x1000;

        // 文件表里的逻辑块号不含哈希块：每 170 个数据块插入 1 个哈希块。
        private const int BlocksPerHashBlock = 0xAA;

        // 头部字段偏移（free60 STFS 规范，Metadata Version 1）。
        private const int OffsetContentId = 0x32C;
        private const int OffsetHeaderSize = 0x340;
        private const int OffsetContentType = 0x344;
        private const int OffsetContentSize = 0x34C;
        private const int OffsetTitleId = 0x360;
        private const int OffsetVolumeDescriptor = 0x379;
        private const int OffsetDataFileCount = 0x39D;

        private const int FileTableEntrySize = 0x40;
        private const int EntriesPerBlock = BlockSize / FileTableEntrySize;   // 64

        private readonly Stream _stream;
        private readonly bool _ownsStream;
        private readonly List<StfsEntry> _entries = new List<StfsEntry>();
        private readonly Dictionary<int, StfsEntry> _byTableIndex = new Dictionary<int, StfsEntry>();

        private int _dataStart;
        private int _hashBias;

        private StfsPackage(Stream stream, bool ownsStream)
        {
            _stream = stream;
            _ownsStream = ownsStream;
            Header = new StfsHeader();
        }

        public StfsHeader Header { get; private set; }

        /// <summary>文件表条目（含目录），顺序与包内文件表一致。</summary>
        public IReadOnlyList<StfsEntry> Entries { get { return _entries; } }

        // ------------------------------------------------------------------
        // 打开
        // ------------------------------------------------------------------

        public static StfsPackage Open(string packagePath)
        {
            if (packagePath == null) throw new ArgumentNullException("packagePath");
            if (!File.Exists(packagePath))
                throw new StfsException("找不到文件：" + packagePath);

            var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read,
                                    FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            try
            {
                return Open(fs, true);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        /// <summary>从可定位的流打开（例如已读进内存的包）。调用方负责流的生命周期。</summary>
        public static StfsPackage Open(Stream stream)
        {
            return Open(stream, false);
        }

        private static StfsPackage Open(Stream stream, bool ownsStream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (!stream.CanRead || !stream.CanSeek)
                throw new StfsException("流必须可读且支持随机定位。");

            var pkg = new StfsPackage(stream, ownsStream);
            try
            {
                pkg.ReadHeader();
                pkg.ReadFileTable();
                pkg.BuildPaths();
            }
            catch
            {
                if (ownsStream) stream.Dispose();
                throw;
            }
            return pkg;
        }

        // ------------------------------------------------------------------
        // 头部
        // ------------------------------------------------------------------

        private void ReadHeader()
        {
            var head = new byte[0x400];
            _stream.Seek(0, SeekOrigin.Begin);
            int read = ReadUpTo(_stream, head, head.Length);
            if (read < 0x3B0) throw new StfsException("文件太小，不像 STFS 容器。");

            string magic = Encoding.ASCII.GetString(head, 0, 4);
            switch (magic)
            {
                case "LIVE": Header.Magic = StfsMagic.Live; break;
                case "PIRS": Header.Magic = StfsMagic.Pirs; break;
                case "CON ": Header.Magic = StfsMagic.ConsoleSigned; break;
                default:
                    throw new StfsException("不是 STFS 容器（前 4 字节是 \""
                        + Printable(magic) + "\"，应为 LIVE / PIRS / CON）。");
            }

            Header.ContentId = new byte[0x14];
            Array.Copy(head, OffsetContentId, Header.ContentId, 0, 0x14);

            Header.HeaderSize = ReadBE32(head, OffsetHeaderSize);
            Header.ContentType = unchecked((uint)ReadBE32(head, OffsetContentType));
            Header.ContentSize = ReadBE64(head, OffsetContentSize);
            Header.TitleId = unchecked((uint)ReadBE32(head, OffsetTitleId));
            Header.DataFileCount = ReadBE32(head, OffsetDataFileCount);

            if (Header.HeaderSize <= 0 || Header.HeaderSize > 0x20000)
                throw new StfsException("头部长度异常（0x" + Header.HeaderSize.ToString("X", CultureInfo.InvariantCulture)
                    + "），包可能已损坏。");

            int vd = OffsetVolumeDescriptor;
            Header.BlockSeparation = head[vd + 2];

            // 文件表块数：规范只说 2 字节，本游戏包是 01 00 —— 小端读才是 1，
            // 而 57 个条目刚好占满一块（64 槽），所以按小端为准，再用大端兜底。
            int countLE = head[vd + 3] | (head[vd + 4] << 8);
            int countBE = (head[vd + 3] << 8) | head[vd + 4];
            Header.FileTableBlockCount = (countLE > 0 && countLE <= 16) ? countLE : countBE;

            Header.FileTableBlockNumber = (head[vd + 5] << 16) | (head[vd + 6] << 8) | head[vd + 7];

            if (Header.FileTableBlockCount <= 0 || Header.FileTableBlockCount > 16)
                throw new StfsException("文件表块数异常（" + Header.FileTableBlockCount + "），包格式不受支持。");

            // 数据区起点 = 头部按 4KB 对齐。
            //   常规包（头部 0xB300）对齐后就是 0xC000，块 0 位于此处；
            //   本游戏的包头部是 0xAD0E，对齐到 0xB000，后面还要空 1 块才是块 0（0xC000），
            //   并且这 1 块的偏移在第一个哈希边界（块 170）之后同样叠加一次。
            int aligned = (Header.HeaderSize + (BlockSize - 1)) & ~(BlockSize - 1);
            bool legacyLayout = (aligned & 0xF000) == 0xB000;
            _dataStart = legacyLayout ? aligned + BlockSize : aligned;
            _hashBias = legacyLayout ? 1 : 0;

            Header.DataStartOffset = _dataStart;
        }

        // ------------------------------------------------------------------
        // 文件表
        // ------------------------------------------------------------------

        private void ReadFileTable()
        {
            // 文件表块号的字节序在规范里没写死（本游戏是 0，两种读法一样），
            // 所以先用大端试解析，解析不出合法条目再换小端。
            if (TryParseFileTable(Header.FileTableBlockNumber)) return;

            int swapped = ((Header.FileTableBlockNumber & 0xFF) << 16)
                        | (Header.FileTableBlockNumber & 0xFF00)
                        | ((Header.FileTableBlockNumber >> 16) & 0xFF);

            if (swapped != Header.FileTableBlockNumber && TryParseFileTable(swapped))
            {
                Header.FileTableBlockNumber = swapped;
                return;
            }

            throw new StfsException("文件表解析失败：块映射不适用于这个包（块号 "
                + Header.FileTableBlockNumber + "）。");
        }

        private bool TryParseFileTable(int blockNumber)
        {
            _entries.Clear();
            _byTableIndex.Clear();

            int tableBytes = Header.FileTableBlockCount * BlockSize;
            var table = new byte[tableBytes];

            long offset = BlockOffset(blockNumber);
            if (offset + tableBytes > _stream.Length) return false;

            _stream.Seek(offset, SeekOrigin.Begin);
            int read = ReadUpTo(_stream, table, tableBytes);
            if (read < FileTableEntrySize) return false;

            int totalEntries = tableBytes / FileTableEntrySize;
            int accepted = 0;

            for (int i = 0; i < totalEntries; i++)
            {
                int off = i * FileTableEntrySize;

                // 全 0 条目 = 文件表结束。
                if (table[off] == 0 && table[off + 0x28] == 0) break;

                byte flags = table[off + 0x28];
                int nameLength = flags & 0x3F;
                if (nameLength <= 0 || nameLength > 0x28)
                {
                    nameLength = 0;
                    while (nameLength < 0x28 && table[off + nameLength] != 0) nameLength++;
                }

                string name = DecodeName(table, off, nameLength);
                if (name.Length == 0)
                {
                    if (accepted == 0) return false;   // 表头就是垃圾，说明块号算错了
                    continue;
                }

                int parent = (table[off + 0x32] << 8) | table[off + 0x33];

                var entry = new StfsEntry
                {
                    Index = i,
                    Name = name,
                    IsDirectory = (flags & 0x80) != 0,
                    IsConsecutive = (flags & 0x40) != 0,
                    BlockCount = ReadLE24(table, off + 0x29),
                    StartBlock = ReadLE24(table, off + 0x2F),
                    Size = ReadBE32(table, off + 0x34),
                    ParentTableIndex = parent == 0xFFFF ? -1 : parent
                };

                _entries.Add(entry);
                _byTableIndex[i] = entry;
                accepted++;
            }

            if (accepted == 0) return false;

            // 至少得有一个条目的大小/块数不像随机数据，否则认为解析失败。
            foreach (var e in _entries)
            {
                if (e.IsDirectory) continue;
                if (e.Size < 0 || e.StartBlock < 0) return false;
            }

            return true;
        }

        /// <summary>按父索引把条目串成相对路径。</summary>
        private void BuildPaths()
        {
            foreach (var entry in _entries)
            {
                var parts = new List<string>();
                StfsEntry cursor = entry;
                int guard = 0;
                while (cursor != null && guard++ < 32)
                {
                    parts.Add(cursor.Name);
                    StfsEntry parent;
                    cursor = cursor.ParentTableIndex >= 0 && _byTableIndex.TryGetValue(cursor.ParentTableIndex, out parent)
                        ? parent
                        : null;
                }
                parts.Reverse();
                entry.RelativePath = string.Join("/", parts.ToArray());
            }
        }

        // ------------------------------------------------------------------
        // 查询
        // ------------------------------------------------------------------

        /// <summary>按相对路径查条目（'/' 与 '\' 等价，不区分大小写）。找不到返回 null。</summary>
        public StfsEntry Find(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            string normalized = relativePath.Replace('\\', '/').TrimStart('/');
            foreach (var e in _entries)
            {
                if (string.Equals(e.RelativePath, normalized, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        public bool Contains(string relativePath)
        {
            return Find(relativePath) != null;
        }

        // ------------------------------------------------------------------
        // 解包
        // ------------------------------------------------------------------

        /// <summary>把包内所有文件和目录解到指定目录。</summary>
        /// <param name="progress">进度回调，可为 null。每 256 KB 或每个文件回调一次。</param>
        /// <param name="cancellationToken">取消令牌，块级别响应。</param>
        public ExtractResult ExtractTo(string destinationDirectory,
                                       ExtractOptions options = null,
                                       IProgress<ExtractProgress> progress = null,
                                       CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrEmpty(destinationDirectory))
                throw new ArgumentNullException("destinationDirectory");
            if (options == null) options = new ExtractOptions();

            ValidateTitleId(options);

            string destination = Path.GetFullPath(destinationDirectory);

            if (!options.Staging)
            {
                return ExtractCore(destination, options, progress, cancellationToken);
            }

            // 先解到 "<目标>.tmp"，成功后整体替换，避免解一半留下半个资源目录。
            string stage = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".tmp";
            if (Directory.Exists(stage)) DeleteDirectory(stage);
            Directory.CreateDirectory(stage);

            try
            {
                ExtractResult staged = ExtractCore(stage, options, progress, cancellationToken);

                string parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                if (Directory.Exists(destination))
                {
                    string old = destination + ".old";
                    if (Directory.Exists(old)) DeleteDirectory(old);
                    Directory.Move(destination, old);
                    Directory.Move(stage, destination);
                    DeleteDirectory(old);
                }
                else
                {
                    Directory.Move(stage, destination);
                }

                return new ExtractResult(destination, staged.FileCount, staged.ByteCount, staged.Elapsed);
            }
            catch
            {
                if (Directory.Exists(stage)) DeleteDirectory(stage);
                throw;
            }
        }

        private void ValidateTitleId(ExtractOptions options)
        {
            if (options.ExpectedTitleId.HasValue && Header.TitleId != options.ExpectedTitleId.Value)
            {
                throw new StfsException(string.Format(CultureInfo.InvariantCulture,
                    "包不匹配：Title ID 是 0x{0:X8}，期望 0x{1:X8}。请确认选对了游戏包。",
                    Header.TitleId, options.ExpectedTitleId.Value));
            }
        }

        private ExtractResult ExtractCore(string destination, ExtractOptions options,
                                          IProgress<ExtractProgress> progress,
                                          CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            Directory.CreateDirectory(destination);

            var files = new List<StfsEntry>();
            var directories = new List<StfsEntry>();
            long totalBytes = 0;

            foreach (var e in _entries)
            {
                if (e.IsDirectory) directories.Add(e);
                else
                {
                    files.Add(e);
                    totalBytes += e.Size;
                }
            }

            string root = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;

            // 目录先建，空目录也能还原。
            foreach (var dir in directories)
            {
                string full = Path.GetFullPath(Path.Combine(root, dir.RelativePath));
                EnsureInside(root, full, dir.RelativePath);
                Directory.CreateDirectory(full);
            }

            var buffer = new byte[BlockSize];
            long bytesDone = 0;
            long lastReported = 0;
            int filesDone = 0;

            foreach (var entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!entry.IsConsecutive)
                {
                    throw new StfsUnsupportedException(
                        "文件使用了非连续块链（" + entry.RelativePath
                        + "），本解析器未实现链式读取。当前游戏包不含此类文件。");
                }

                string full = Path.GetFullPath(Path.Combine(root, entry.RelativePath));
                EnsureInside(root, full, entry.RelativePath);

                string dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(full) && !options.Overwrite)
                {
                    filesDone++;
                    bytesDone += entry.Size;
                    continue;
                }

                using (var output = new FileStream(full, FileMode.Create, FileAccess.Write,
                                                   FileShare.None, 1 << 16))
                {
                    long written = 0;
                    while (written < entry.Size)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int chunk = (int)Math.Min(BlockSize, entry.Size - written);
                        int blockIndex = entry.StartBlock + (int)(written / BlockSize);

                        _stream.Seek(BlockOffset(blockIndex), SeekOrigin.Begin);
                        int got = ReadUpTo(_stream, buffer, chunk);
                        if (got != chunk)
                            throw new StfsException("读取数据块越界：" + entry.RelativePath);

                        output.Write(buffer, 0, chunk);
                        written += chunk;
                        bytesDone += chunk;

                        if (progress != null && bytesDone - lastReported >= 256 * 1024)
                        {
                            lastReported = bytesDone;
                            progress.Report(new ExtractProgress(entry.RelativePath, filesDone,
                                                                files.Count, bytesDone, totalBytes));
                        }
                    }
                }

                filesDone++;
                if (progress != null)
                {
                    progress.Report(new ExtractProgress(entry.RelativePath, filesDone,
                                                        files.Count, bytesDone, totalBytes));
                    lastReported = bytesDone;
                }
            }

            if (options.ValidateExecutable)
            {
                var xex = Find("default.xex");
                if (xex != null)
                {
                    string xexPath = Path.Combine(root, xex.RelativePath);
                    if (File.Exists(xexPath) && !LooksLikeXex(xexPath))
                    {
                        throw new StfsException(
                            "解出来的 default.xex 魔数不是 XEX2 —— 块映射错误或包已损坏。");
                    }
                }
            }

            stopwatch.Stop();
            return new ExtractResult(destination, files.Count, bytesDone, stopwatch.Elapsed);
        }

        /// <summary>把单个条目读进内存（小文件预览用）。</summary>
        public byte[] ReadEntry(StfsEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (entry.IsDirectory) return new byte[0];
            if (!entry.IsConsecutive)
                throw new StfsUnsupportedException(
                    "文件使用了非连续块链（" + entry.RelativePath + "），本解析器未实现链式读取。");

            var data = new byte[entry.Size];
            var buffer = new byte[BlockSize];
            int written = 0;
            while (written < data.Length)
            {
                int chunk = Math.Min(BlockSize, data.Length - written);
                int blockIndex = entry.StartBlock + written / BlockSize;

                _stream.Seek(BlockOffset(blockIndex), SeekOrigin.Begin);
                int got = ReadUpTo(_stream, buffer, chunk);
                if (got != chunk) throw new StfsException("读取数据块越界：" + entry.RelativePath);

                Buffer.BlockCopy(buffer, 0, data, written, chunk);
                written += chunk;
            }
            return data;
        }

        // ------------------------------------------------------------------
        // 块映射
        // ------------------------------------------------------------------

        /// <summary>
        /// 逻辑块号 → 文件偏移。块号不含哈希块：每 170 块插 1 个哈希块，
        /// 本游戏的包（头部 0xAD0E）在首个边界处还多插 1 块，由 _hashBias 表示。
        /// </summary>
        private long BlockOffset(int logicalBlock)
        {
            int level1 = logicalBlock / BlocksPerHashBlock;
            int physical = logicalBlock + level1;
            if (level1 > 0) physical += _hashBias;
            return _dataStart + (long)physical * BlockSize;
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        private static void EnsureInside(string root, string fullPath, string relativePath)
        {
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new StfsException("包内路径越界（疑似恶意包）：" + relativePath);
        }

        private static bool LooksLikeXex(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                var magic = new byte[4];
                if (fs.Read(magic, 0, 4) != 4) return false;
                return magic[0] == (byte)'X' && magic[1] == (byte)'E'
                       && magic[2] == (byte)'X' && magic[3] == (byte)'2';
            }
        }

        private static string DecodeName(byte[] table, int offset, int length)
        {
            if (length <= 0) return string.Empty;

            string name;
            try
            {
                name = new UTF8Encoding(false, true).GetString(table, offset, length);
            }
            catch (ArgumentException)
            {
                name = Encoding.GetEncoding(28591).GetString(table, offset, length);   // Latin-1 兜底
            }

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(char.IsControl(c) ? '_' : c);
            }

            string cleaned = sb.ToString().Trim();
            if (cleaned == "." || cleaned == "..") return string.Empty;
            return cleaned;
        }

        private static string Printable(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsControl(c) ? '?' : c);
            return sb.ToString();
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static int ReadUpTo(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int got = stream.Read(buffer, total, count - total);
                if (got <= 0) break;
                total += got;
            }
            return total;
        }

        private static int ReadBE32(byte[] b, int o)
        {
            return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        }

        private static long ReadBE64(byte[] b, int o)
        {
            long hi = (uint)ReadBE32(b, o);
            long lo = (uint)ReadBE32(b, o + 4);
            return (hi << 32) | lo;
        }

        private static int ReadLE24(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
        }

        public void Dispose()
        {
            if (_ownsStream) _stream.Dispose();
        }
    }
}
