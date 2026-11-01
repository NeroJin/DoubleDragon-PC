using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace DoubleDragon.Loader
{
    internal static class Program
    {
        /// <summary>
        /// 启动器必须和这些文件同目录才能工作（缺一个都直接报错，而不是等游戏崩）。
        /// DoubleDragon.toml 不在这个列表里：缺失时由 MainForm 自动生成模板。
        /// </summary>
        private static readonly string[] RequiredFiles =
        {
            "DoubleDragon.exe",
            "rexruntime.dll",
            "rexgpu-xenos.dll"
        };

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            var missing = new List<string>();
            foreach (string name in RequiredFiles)
            {
                if (!File.Exists(Path.Combine(baseDir, name)))
                {
                    missing.Add(name);
                }
            }

            if (missing.Count > 0)
            {
                MessageBox.Show(
                    L10n.Format("MissingBody", "  " + string.Join("\n  ", missing.ToArray())),
                    L10n.Get("MissingTitle"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            Application.Run(new MainForm(baseDir));
        }
    }
}
