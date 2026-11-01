using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DoubleDragon.Unpack
{
    /// <summary>
    /// 面向 Double Dragon 这个游戏的高层封装：校验 Title ID、把资源装到数据目录、判断是否已装好、
    /// 顺便提供一个"在目录里自动找包"的辅助方法给 GUI 用。
    ///
    /// GUI 侧通常只需要：
    /// <code>
    /// var pkg = DoubleDragonPack.FindPackage(dir);           // 或者用户手动选
    /// var result = DoubleDragonPack.Install(pkg, dataDir, null, progress, token);
    /// </code>
    /// </summary>
    public static class DoubleDragonPack
    {
        /// <summary>Double Dragon 的 Title ID。</summary>
        public const uint TitleId = 0x5841085F;

        /// <summary>游戏可执行文件名，用来判断资源是否已装好。</summary>
        public const string ExecutableName = "default.xex";

        /// <summary>default.xex 的正常大小下限，用来识别残缺/错误的解包（实际 5,283,840 B）。</summary>
        private const long MinExecutableSize = 4L * 1024 * 1024;

        /// <summary>打开包（默认不校验 Title ID，交给 Install 校验）。</summary>
        public static StfsPackage Open(string packagePath)
        {
            return StfsPackage.Open(packagePath);
        }

        /// <summary>
        /// 解包安装到 <paramref name="dataDirectory"/>。默认开启暂存 + 整体替换，
        /// 失败时不会破坏已有资源。
        /// </summary>
        public static ExtractResult Install(string packagePath, string dataDirectory,
                                            ExtractOptions options = null,
                                            IProgress<ExtractProgress> progress = null,
                                            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (options == null)
            {
                options = new ExtractOptions
                {
                    Overwrite = true,
                    Staging = true,
                    ExpectedTitleId = TitleId,
                    ValidateExecutable = true
                };
            }

            using (var pkg = StfsPackage.Open(packagePath))
            {
                return pkg.ExtractTo(dataDirectory, options, progress, cancellationToken);
            }
        }

        /// <summary>资源目录是否已经可用（default.xex 存在且大小正常）。</summary>
        public static bool IsDataInstalled(string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory)) return false;
            string xex = Path.Combine(dataDirectory, ExecutableName);
            if (!File.Exists(xex)) return false;
            try
            {
                return new FileInfo(xex).Length >= MinExecutableSize;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// 在目录（含一层子目录）里找 STFS 包：文件名是 40 位十六进制且没有扩展名，
        /// 或者前 4 字节是 LIVE/PIRS/CON。找不到返回 null。
        /// </summary>
        public static string FindPackage(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;

            foreach (var file in EnumerateCandidates(directory))
            {
                if (HasStfsMagic(file)) return file;
            }
            return null;
        }

        private static IEnumerable<string> EnumerateCandidates(string directory)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (IOException)
            {
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var f in files)
            {
                if (Path.GetExtension(f).Length == 0) yield return f;
            }

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var d in subdirs)
            {
                string[] inner;
                try
                {
                    inner = Directory.GetFiles(d);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var f in inner)
                {
                    if (Path.GetExtension(f).Length == 0) yield return f;
                }
            }
        }

        private static bool HasStfsMagic(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var head = new byte[4];
                    if (fs.Read(head, 0, 4) != 4) return false;
                    string magic = System.Text.Encoding.ASCII.GetString(head, 0, 4);
                    return magic == "LIVE" || magic == "PIRS" || magic == "CON ";
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
