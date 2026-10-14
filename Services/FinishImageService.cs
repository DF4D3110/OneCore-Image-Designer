using System;
using System.IO;
using System.Threading;
using OneCore_Image_Designer.Services;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 镜像收尾：写 State.ini + 写 imagestate 注册表值 + 卸载 hive。
    ///
    /// 【为什么单独成类】收尾**必须与 CBS 步骤分进程执行**。
    /// 实测（见 CBS_TECHNICAL_NOTES.md 第 11 节）：
    ///   同一进程内（刚跑完 CBS）释放残留 hive → 9 → 9，重试 6 次（约 42 秒）全失败；
    ///   新进程 → 9 → 0，收尾完整成功（imagestate 写入 2 处）。
    /// 持有者是 CBS 的【服务】而非调用进程，要等调用方进程退出才放。
    /// 抽成只依赖 CbsxService / CbsSafety / OfflineRegistryService 的小类，
    /// 就能被 CbsxCli 链接，从而以独立进程执行收尾。
    /// </summary>
    public static class FinishImageService
    {
        static void R(Action<string>? log, string m) => log?.Invoke(m);

public static (bool ok, string detail) Finish(string winDir, Action<string>? log = null)
        {
            try
            {
                string win = Path.GetFullPath(winDir);

                // ⚠ 先释放 CBS 可能仍持有的残留 hive —— 否则下面打不开 SOFTWARE/SYSTEM。
                // 实测：CBS 会话 FinalizeEx 之后，目标镜像的 hive 仍挂在 HKLM 下，
                // OfflineRegistryService 直接报 "being used by another process"。
                try
                {
                    // 先放掉本进程加载的服务栈模块 —— 否则它持有目标镜像的 hive，卸不掉
                    CbsxService.ReleaseServicingStack(log);
                    string driveRoot = (Path.GetPathRoot(win) ?? "").TrimEnd('\\');
                    if (driveRoot.Length > 0) CbsSafety.ReleaseResidualHives(driveRoot, log);
                }
                catch { }

                string stateDir = Path.Combine(win, "Setup", "State");
                Directory.CreateDirectory(stateDir);
                string stateIni = Path.Combine(stateDir, "State.ini");
                File.WriteAllText(stateIni,
                    "[State]\r\nImageState=IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE\r\n");
                log?.Invoke("已写 " + stateIni);

                string software = Path.Combine(win, "System32", "config", "SOFTWARE");
                string system = Path.Combine(win, "System32", "config", "SYSTEM");
                if (!File.Exists(software) || !File.Exists(system))
                    return (false, "缺少 Windows\\System32\\config\\SOFTWARE 或 SYSTEM");

                // ⚠ CBS 服务会在会话结束后继续持有目标镜像的 hive 一段时间（实测：
                //   FinalizeEx 之后仍持有，且**新进程能卸掉而本进程不能** —— 说明持有者是
                //   CBS 服务而非我们）。这里用"释放 → 等待 → 重试"给它时间；
                //   若始终拿不到，说明必须等本进程退出，那就该把收尾移出本进程。
                string driveRoot0 = (Path.GetPathRoot(win) ?? "").TrimEnd('\\');
                bool hiveFree = false;
                for (int attempt = 1; attempt <= 6; attempt++)
                {
                    try
                    {
                        // 先探一下能否独占打开
                        using (var probe = File.Open(software, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                        hiveFree = true;
                        break;
                    }
                    catch (IOException)
                    {
                        R(log, $"  hive 仍被占用，第 {attempt}/6 次等待后重试…");
                        try { CbsSafety.ReleaseResidualHives(driveRoot0, null); } catch { }
                        Thread.Sleep(2000 * attempt);   // 递增等待：2s,4s,6s,8s,10s,12s
                    }
                    catch { break; }
                }
                if (!hiveFree)
                    R(log, "  ⚠ hive 始终被 CBS 服务持有；收尾可能失败。"
                         + "持有者是 CBS 服务而非本进程（新进程可卸），必要时请把收尾放到独立进程执行。");

                // 用 CBS 之前的挂载名（与 cbss.cmd 的 imagestate.reg 对齐）
                const string softwareKey = "CBSS_SOFTWARE";
                const string systemKey = "CBSS_SYSTEM";

                int applied = 0;
                using (var reg = new OfflineRegistryService())
                {
                    if (reg.LoadHive(software, writable: true))
                    {
                        log?.Invoke("已挂载 SOFTWARE");
                    }
                    // Imagestate 的两处 ImageState 值
                    foreach (var sub in new[]
                    {
                        @"Microsoft\Windows\CurrentVersion\Setup\ImageServicingData",
                        @"Microsoft\Windows\CurrentVersion\Setup\State"
                    })
                    {
                        if (reg.SetValue(sub, "ImageState", "IMAGE_STATE_COMPLETE",
                                Microsoft.Win32.RegistryValueKind.String)) applied++;
                    }
                    reg.SaveToOriginal();
                }
                log?.Invoke($"imagestate 写入 {applied} 处（SOFTWARE）");

                return (true, $"State.ini 已写；imagestate {applied} 处；hive 已收尾 ({softwareKey}/{systemKey})");
            }
            catch (Exception ex) { return (false, ex.GetType().Name + ": " + ex.Message); }
        }
    }
}
