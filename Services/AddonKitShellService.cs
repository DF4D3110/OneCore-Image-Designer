using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 内置 addonkit（微软 iot-adk-addonkit 17763-v7）shell 启动服务。
    /// addonkit 本体是纯 PowerShell 工具链（非编译型程序），位于程序目录 addonkit\Tools\；
    /// 本服务负责拉起预加载 IoTCoreImaging 模块的交互式 PowerShell 窗口，
    /// 并默认打开内置示例工作区（addonkit\Workspace，即官方 $env:SAMPLEWKS）。
    /// </summary>
    public static class AddonKitShellService
    {
        /// <summary>IoTCoreImaging 模块清单路径（存在即代表内置完整）。</summary>
        public static string ModuleManifest =>
            Path.Combine(AdkToolService.AddonKitRoot, "Tools", "IoTCoreImaging", "IoTCoreImaging.psd1");

        /// <summary>内置示例工作区 XML。</summary>
        public static string SampleWorkspaceXml =>
            Path.Combine(AdkToolService.AddonKitRoot, "Workspace", "IoTWorkspace.xml");

        public static bool IsModuleAvailable => File.Exists(ModuleManifest);

        /// <summary>
        /// 启动交互式 addonkit shell（等同官方 IoTCorePShell.cmd，但以程序目录内置副本为准）。
        /// </summary>
        /// <param name="workspaceXml">要打开的工作区 XML；null 则打开内置示例工作区。</param>
        /// <param name="log">输出提示。</param>
        /// <returns>是否成功启动进程。</returns>
        public static bool LaunchShell(string? workspaceXml = null, IProgress<string>? log = null)
        {
            if (!IsModuleAvailable)
            {
                log?.Report($"未找到内置模块：{ModuleManifest}");
                return false;
            }

            var ws = workspaceXml ?? SampleWorkspaceXml;
            if (!File.Exists(ws))
            {
                log?.Report($"工作区 XML 不存在：{ws}");
                return false;
            }

            var toolsDir = Path.Combine(AdkToolService.AddonKitRoot, "Tools");
            // 与官方 LaunchShell.ps1 等价的单行命令：挂载模块路径 → 导入 → 打开工作区
            var command =
                "$env:PSModulePath = '" + toolsDir + ";' + $env:PSModulePath; " +
                "Import-Module IoTCoreImaging.psd1 -Force; " +
                "Open-IoTWorkspace '" + ws + "'";

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoExit -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"",
                    WorkingDirectory = AdkToolService.AddonKitRoot,
                    UseShellExecute = true
                });
                log?.Report("addonkit shell 已启动（示例工作区：" + ws + "）");
                return true;
            }
            catch (Exception ex)
            {
                log?.Report("启动失败：" + ex.Message);
                return false;
            }
        }
    }
}
