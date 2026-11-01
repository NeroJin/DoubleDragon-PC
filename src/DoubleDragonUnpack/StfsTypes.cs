using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DoubleDragon.Unpack
{
    /// <summary>STFS 容器的签名类型（对应文件头前 4 字节）。</summary>
    public enum StfsMagic
    {
        Unknown = 0,
        /// <summary>Windows Live 签名，商城下载的 XBLA / DLC / 试玩包。</summary>
        Live,
        /// <summary>PIRS 签名，微软内部/合作伙伴分发。</summary>
        Pirs,
        /// <summary>CON 签名，主机本地生成的存档、设定档。</summary>
        ConsoleSigned
    }

    /// <summary>解包过程中任何可预期的失败都会抛这个异常。</summary>
    public class StfsException : Exception
    {
        public StfsException(string message) : base(message) { }
        public StfsException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>包格式受支持，但内容用到了本解析器未实现的部分（如非连续块链）。</summary>
    public sealed class StfsUnsupportedException : StfsException
    {
        public StfsUnsupportedException(string message) : base(message) { }
    }

    /// <summary>包头部信息。</summary>
    public sealed class StfsHeader
    {
        internal StfsHeader() { }

        public StfsMagic Magic { get; internal set; }

        public string MagicText
        {
            get
            {
                switch (Magic)
                {
                    case StfsMagic.Live: return "LIVE";
                    case StfsMagic.Pirs: return "PIRS";
                    case StfsMagic.ConsoleSigned: return "CON ";
                    default: return "????";
                }
            }
        }

        /// <summary>头部长度（0x340 处，大端）。常见值 0xB300；本游戏是 0xAD0E。</summary>
        public int HeaderSize { get; internal set; }

        /// <summary>内容类型，0x000D0000 = Xbox Live Arcade。</summary>
        public uint ContentType { get; internal set; }

        public string ContentTypeName
        {
            get
            {
                switch (ContentType)
                {
                    case 0x00000001: return "Saved Game";
                    case 0x00000002: return "Marketplace Content";
                    case 0x00000003: return "Publisher";
                    case 0x000B0000: return "Xbox 360 Title";
                    case 0x000D0000: return "Xbox Live Arcade";
                    case 0x00100000: return "Installed Game";
                    case 0x00200000: return "Xbox Original";
                    case 0x00300000: return "Xbox Saved Game";
                    case 0x00500000: return "Avatar Item";
                    case 0x00600000: return "Profile";
                    case 0x00700000: return "Picture";
                    case 0x00900000: return "Video";
                    default: return "0x" + ContentType.ToString("X8", CultureInfo.InvariantCulture);
                }
            }
        }

        /// <summary>Content ID（0x32C 起 0x14 字节），也是包文件名的主体。</summary>
        public byte[] ContentId { get; internal set; }

        public string ContentIdHex
        {
            get
            {
                if (ContentId == null) return string.Empty;
                var sb = new StringBuilder(ContentId.Length * 2);
                foreach (var b in ContentId) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>内容总大小（0x34C 起 8 字节，大端）。</summary>
        public long ContentSize { get; internal set; }

        /// <summary>Title ID（0x360 处，大端）。Double Dragon = 0x5841085F。</summary>
        public uint TitleId { get; internal set; }

        /// <summary>文件表里的条目数（0x39D 处）。</summary>
        public int DataFileCount { get; internal set; }

        /// <summary>卷描述符里的块间隔，本游戏为 1。</summary>
        public int BlockSeparation { get; internal set; }

        public int FileTableBlockCount { get; internal set; }
        public int FileTableBlockNumber { get; internal set; }

        /// <summary>数据区在文件中的起始偏移（块 0 的位置）。</summary>
        public int DataStartOffset { get; internal set; }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} TitleID=0x{1:X8} ({2}) ContentID={3} Size={4:N0} B 头部={5:N0} B 数据区=0x{6:X}",
                MagicText, TitleId, ContentTypeName, ContentIdHex, ContentSize, HeaderSize, DataStartOffset);
        }
    }

    /// <summary>文件表中的一个条目（文件或目录）。</summary>
    public sealed class StfsEntry
    {
        internal StfsEntry() { }

        /// <summary>条目自身的名字，不含路径。</summary>
        public string Name { get; internal set; }

        /// <summary>相对包根目录的路径，用 '/' 分隔（可直接用 Path.Combine 拆开）。</summary>
        public string RelativePath { get; internal set; }

        public bool IsDirectory { get; internal set; }

        /// <summary>文件字节数。目录为 0。</summary>
        public long Size { get; internal set; }

        /// <summary>起始逻辑块号。</summary>
        public int StartBlock { get; internal set; }

        /// <summary>占用块数。</summary>
        public int BlockCount { get; internal set; }

        /// <summary>块是否在包内连续存放（标志位 bit6）。本游戏全部为 true。</summary>
        public bool IsConsecutive { get; internal set; }

        /// <summary>条目在文件表中的序号，调试用。</summary>
        public int Index { get; internal set; }

        /// <summary>父目录在文件表中的序号（-1 表示根目录）。</summary>
        internal int ParentTableIndex { get; set; }

        public override string ToString()
        {
            return IsDirectory
                ? RelativePath + "/"
                : string.Format(CultureInfo.InvariantCulture, "{0} ({1:N0} B)", RelativePath, Size);
        }
    }

    /// <summary>解包选项。</summary>
    public sealed class ExtractOptions
    {
        public ExtractOptions()
        {
            Overwrite = true;
            Staging = false;
            ExpectedTitleId = DoubleDragonPack.TitleId;
        }

        /// <summary>目标目录里已存在同名文件时是否覆盖。</summary>
        public bool Overwrite { get; set; }

        /// <summary>
        /// 先解到 "目标目录.tmp"，全部成功后再整体替换目标目录。
        /// 用于首次安装/更新，避免解到一半失败留下半个资源目录。
        /// </summary>
        public bool Staging { get; set; }

        /// <summary>
        /// 期望的 Title ID。不匹配直接抛异常，防止用户选错包。
        /// 传 null 表示不校验。
        /// </summary>
        public uint? ExpectedTitleId { get; set; }

        /// <summary>是否校验解出来的 default.xex 是合法 XEX（魔数 "XEX2"）。默认开。</summary>
        public bool ValidateExecutable { get; set; } = true;
    }

    /// <summary>解包进度快照（每解完一个文件或累计若干字节回调一次）。</summary>
    public sealed class ExtractProgress
    {
        internal ExtractProgress(string currentFile, int filesDone, int filesTotal,
                                 long bytesDone, long bytesTotal)
        {
            CurrentFile = currentFile;
            FilesDone = filesDone;
            FilesTotal = filesTotal;
            BytesDone = bytesDone;
            BytesTotal = bytesTotal;
        }

        public string CurrentFile { get; private set; }
        public int FilesDone { get; private set; }
        public int FilesTotal { get; private set; }
        public long BytesDone { get; private set; }
        public long BytesTotal { get; private set; }

        /// <summary>0–100。总量未知时为 0。</summary>
        public double Percent
        {
            get { return BytesTotal > 0 ? BytesDone * 100.0 / BytesTotal : 0.0; }
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F1}% ({1}/{2}) {3}",
                Percent, FilesDone, FilesTotal, CurrentFile);
        }
    }

    /// <summary>解包结果。</summary>
    public sealed class ExtractResult
    {
        internal ExtractResult(string directory, int fileCount, long byteCount, TimeSpan elapsed)
        {
            Directory = directory;
            FileCount = fileCount;
            ByteCount = byteCount;
            Elapsed = elapsed;
        }

        public string Directory { get; private set; }
        public int FileCount { get; private set; }
        public long ByteCount { get; private set; }
        public TimeSpan Elapsed { get; private set; }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0} 个文件 / {1:N0} B → {2}（{3:F1} 秒）",
                FileCount, ByteCount, Directory, Elapsed.TotalSeconds);
        }
    }
}
