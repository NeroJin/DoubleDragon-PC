using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using DoubleDragon.Unpack;

namespace DoubleDragon.Unpack.Cli
{
    /// <summary>
    /// 命令行验证工具（GUI 之外的对照手段）。
    ///   DoubleDragonUnpack list    &lt;包&gt;
    ///   DoubleDragonUnpack extract &lt;包&gt; &lt;目标目录&gt; [--no-overwrite] [--keep-staging]
    ///   DoubleDragonUnpack verify  &lt;包&gt; &lt;参考目录&gt;      解到临时目录并逐文件比对 SHA1
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length < 2)
                {
                    PrintUsage();
                    return 2;
                }

                string command = args[0].ToLowerInvariant();
                switch (command)
                {
                    case "list":
                        return List(args[1]);
                    case "extract":
                        return Extract(args);
                    case "verify":
                        return Verify(args);
                    default:
                        PrintUsage();
                        return 2;
                }
            }
            catch (StfsException ex)
            {
                Console.Error.WriteLine("失败：" + ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("未预期错误：" + ex);
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("用法：");
            Console.WriteLine("  DoubleDragonUnpack list    <包文件>");
            Console.WriteLine("  DoubleDragonUnpack extract <包文件> <目标目录> [--no-overwrite]");
            Console.WriteLine("  DoubleDragonUnpack verify  <包文件> <参考目录>");
        }

        private static int List(string packagePath)
        {
            using (var pkg = StfsPackage.Open(packagePath))
            {
                Console.WriteLine(pkg.Header);
                Console.WriteLine();

                long total = 0;
                int dirs = 0, files = 0;
                foreach (var e in pkg.Entries)
                {
                    if (e.IsDirectory)
                    {
                        dirs++;
                        Console.WriteLine("  [DIR ] " + e.RelativePath);
                    }
                    else
                    {
                        files++;
                        total += e.Size;
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "  [FILE] {0,-46} {1,9:N0} B  块 {2} +{3}",
                            e.RelativePath, e.Size, e.StartBlock, e.BlockCount));
                    }
                }

                Console.WriteLine();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "共 {0} 个文件 / {1} 个目录，合计 {2:N0} 字节。", files, dirs, total));
                return 0;
            }
        }

        private static int Extract(string[] args)
        {
            if (args.Length < 3)
            {
                PrintUsage();
                return 2;
            }

            var options = new ExtractOptions();
            for (int i = 3; i < args.Length; i++)
            {
                if (args[i] == "--no-overwrite") options.Overwrite = false;
                if (args[i] == "--no-validate") options.ValidateExecutable = false;
            }

            var progress = new Progress<ExtractProgress>(p =>
            {
                string name = p.CurrentFile ?? string.Empty;
                if (name.Length > 40) name = name.Substring(0, 40);
                Console.Write("\r" + p.Percent.ToString("F1", CultureInfo.InvariantCulture)
                    + "%  " + name.PadRight(40));
            });

            var result = DoubleDragonPack.Install(args[1], args[2], options, progress, CancellationToken.None);
            Console.WriteLine();
            Console.WriteLine("完成：" + result);
            return 0;
        }

        private static int Verify(string[] args)
        {
            if (args.Length < 3)
            {
                PrintUsage();
                return 2;
            }

            string reference = Path.GetFullPath(args[2]);
            string temp = Path.Combine(Path.GetTempPath(), "DoubleDragonUnpack_verify_" + Guid.NewGuid().ToString("N"));

            try
            {
                var result = DoubleDragonPack.Install(args[1], temp, null, null, CancellationToken.None);
                Console.WriteLine("已解到临时目录：" + result);

                int same = 0, diff = 0, missing = 0;
                var diffs = new List<string>();

                foreach (var file in Directory.GetFiles(temp, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(temp.Length).TrimStart('\\', '/');
                    string expected = Path.Combine(reference, relative);

                    if (!File.Exists(expected))
                    {
                        missing++;
                        diffs.Add("仅在包里：" + relative);
                        continue;
                    }

                    if (Sha1(file) == Sha1(expected)) same++;
                    else
                    {
                        diff++;
                        diffs.Add("内容不同：" + relative);
                    }
                }

                // 反向检查：参考目录里多出来的文件
                foreach (var file in Directory.GetFiles(reference, "*", SearchOption.AllDirectories))
                {
                    string relative = file.Substring(reference.Length).TrimStart('\\', '/');
                    string actual = Path.Combine(temp, relative);
                    if (!File.Exists(actual))
                    {
                        missing++;
                        diffs.Add("参考目录独有：" + relative);
                    }
                }

                Console.WriteLine();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "比对结果：一致 {0}，不同 {1}，缺失 {2}", same, diff, missing));
                foreach (var d in diffs) Console.WriteLine("  " + d);

                return (diff == 0 && missing == 0) ? 0 : 1;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(temp)) Directory.Delete(temp, true);
                }
                catch (IOException) { }
            }
        }

        private static string Sha1(string path)
        {
            using (var sha = SHA1.Create())
            using (var fs = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
