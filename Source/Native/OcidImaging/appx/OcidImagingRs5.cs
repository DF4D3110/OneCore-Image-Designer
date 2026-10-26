// OcidImagingRs5.exe -- RS5 (17763 / 17705 era) imaging helper.
//
// The RS5 ADK's ImageStorageService uses the *legacy* Storage Spaces ABI
// (CreateStorageSpace / GetStorageSpace / RemoveStorageSpace) instead of the
// FE (20348) ABI that OcidImaging's ocid_pool.<stage>.wcos.dll talks to.
// Rather than re-implement that ABI, this helper drives the ADK's own managed
// layer (Microsoft.WindowsPhone.Imaging from the RS5 imagestorageservicemanaged.dll),
// which is exactly what ImageApp does.
//
// Stages:
//   disk   -- create the VHD: pool + spaces + partition tables + formats
//   build  -- disk + stage + commit an UpdateInput (RS5 UpdateDLL)
//
// Every binary it loads comes from the RS5 ADK root passed on the command line
// (or next to this exe); mixing FE and RS5 ADK files is refused on purpose.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.WindowsPhone.ImageUpdate.Tools.Common;
using Microsoft.WindowsPhone.Imaging;

internal static class Program
{
    private static int _exit = 0;
    private static StreamWriter _log;
    private static readonly object _logLock = new object();

    private static void Say(string message)
    {
        lock (_logLock)
        {
            Console.WriteLine(message);
            Console.Out.Flush();
            if (_log != null)
            {
                _log.WriteLine(message);
                _log.Flush();
            }
        }
    }

    private static int Fail(string message)
    {
        Say("ERROR: " + message);
        return 1;
    }

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Say("FATAL: " + ex.GetType().Name + ": " + ex.Message);
            Say(ex.ToString());
            return 3;
        }
        finally
        {
            if (_log != null)
            {
                _log.Dispose();
                _log = null;
            }
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        string command = args[0].ToLowerInvariant();
        if (command == "-h" || command == "--help" || command == "help")
        {
            Usage();
            return 0;
        }
        if (command == "info")
        {
            PrintInfo();
            return 0;
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            string key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                return Fail("未知参数：" + key);
            }
            string name = key.Substring(2);
            if (name == "skip-update" || name == "probe")
            {
                options[name] = "1";
                continue;
            }
            if (i + 1 >= args.Length)
            {
                return Fail("选项缺少参数：" + key);
            }
            options[name] = args[++i];
        }

        string layout = Get(options, "layout");
        string platform = Get(options, "oem-device-platform");
        string vhd = Get(options, "vhd");
        string logPath = Get(options, "log");
        string osVersion = Get(options, "os-version");
        if (string.IsNullOrEmpty(osVersion))
        {
            osVersion = "10.0.17705.1000";
        }

        if (command != "disk" && command != "build" && command != "mount")
        {
            Usage();
            return Fail("未知子命令：" + command);
        }
        if (string.IsNullOrEmpty(layout) || !File.Exists(layout))
        {
            return Fail("找不到 --layout：" + layout);
        }
        if (string.IsNullOrEmpty(platform) || !File.Exists(platform))
        {
            return Fail("找不到 --oem-device-platform：" + platform);
        }
        if (string.IsNullOrEmpty(vhd))
        {
            return Fail("必须给 --vhd");
        }

        if (!string.IsNullOrEmpty(logPath))
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _log = new StreamWriter(logPath, false, new UTF8Encoding(false));
            _log.AutoFlush = true;
        }

        Say("OcidImagingRs5: 版本 " + typeof(ImageStorageManager).Assembly.GetName().Version);
        Say("OcidImagingRs5: 阶段 RS5（legacy Storage Spaces ABI，经 ADK 托管层）");
        Say("OcidImagingRs5: layout = " + layout);
        Say("OcidImagingRs5: device platform = " + platform);
        Say("OcidImagingRs5: vhd = " + vhd);
        Say("OcidImagingRs5: OSVersion = " + osVersion);

        string fullVhd = Path.GetFullPath(vhd);
        string vhdDir = Path.GetDirectoryName(fullVhd);
        if (!string.IsNullOrEmpty(vhdDir))
        {
            Directory.CreateDirectory(vhdDir);
        }
        if (command != "mount")
        {
            if (File.Exists(fullVhd))
            {
                Say("OcidImagingRs5: 目标 VHD 已存在，先删除");
                File.Delete(fullVhd);
            }
        }

        var logger = new IULogger();
        var parameters = new ImageGeneratorParameters(logger);
        Say("OcidImagingRs5: 解析 DeviceLayout / OEMDevicePlatform ...");
        var platformInput = (OEMDevicePlatformInput)XsdValidator.ReadXMLFromFile(platform, typeof(OEMDevicePlatformInput));
        if (platformInput == null)
        {
            return Fail("OEMDevicePlatform XML 解析失败：" + platform);
        }
        parameters.ProcessInputXML(layout, platformInput);
        Say(string.Format(CultureInfo.InvariantCulture,
            "OcidImagingRs5: SectorSize={0} ChunkSize={1}KiB UEFI={2} 物理 store {3} 个，池 {4} 个",
            parameters.SectorSize, parameters.ChunkSize, parameters.UEFI,
            parameters.Stores.Count, parameters.StoragePools.Count));

        // RS5's service refuses to override the GUID of a "well known" partition
        // (EFIESP/MainOS/OSData/BSP/...).  Layouts that carry an explicit <Id> for
        // those partitions must have it dropped so the service keeps its own value.
        int clearedIds = ClearWellKnownPartitionIds(parameters);
        if (clearedIds > 0)
        {
            Say("OcidImagingRs5: 清掉 " + clearedIds + " 个系统分区的显式 Id（由服务使用固定 GUID）");
        }

        var ffu = new FullFlashUpdateImage(parameters);
        ffu.OSVersion = osVersion;
        ffu.AntiTheftVersion = "1.1";
        Say(string.Format(CultureInfo.InvariantCulture,
            "OcidImagingRs5: manifest 构造完成：stores={0} pools={1} style={2}",
            ffu.StoreCount, ffu.StoragePoolCount, ffu.ImageStyle));

        if (command == "mount")
        {
            return MountExistingVhd(ffu, fullVhd);
        }

        var manager = new ImageStorageManager(logger);
        manager.EnableAutoMount();
        uint partitionStyle = ffu.UEFI ? ImageConstants.PartitionTypeGpt : ImageConstants.PartitionTypeMbr;
        Say("OcidImagingRs5: 造盘（CreateFullFlashImage, partitionStyle=" + partitionStyle + "）...");
        manager.CreateFullFlashImage(ffu, fullVhd, partitionStyle, false);
        Say("OcidImagingRs5: 造盘完成，AssignMountPoints ...");
        manager.AssignMountPoints();

        List<string> partitionNames = EnumeratePartitionNames(ffu);
        Say("OcidImagingRs5: 分区清单：" + string.Join(", ", partitionNames.ToArray()));

        // Sanity: every partition must have a usable volume path.
        int missing = 0;
        foreach (string name in partitionNames)
        {
            string path;
            try
            {
                path = manager.GetPartitionPath(name);
            }
            catch (Exception ex)
            {
                missing++;
                Say("  [缺] " + name + " -> " + ex.Message);
                continue;
            }
            if (string.IsNullOrEmpty(path))
            {
                missing++;
                Say("  [缺] " + name + " -> 空路径");
                continue;
            }
            string fileSystem = "?";
            try
            {
                fileSystem = manager.GetPartitionFileSystem(name);
            }
            catch
            {
            }
            ulong sectors = 0;
            try
            {
                sectors = manager.GetPartitionSizeInSectors(name);
            }
            catch
            {
            }
            Say(string.Format(CultureInfo.InvariantCulture, "  [ok] {0} fs={1} sectors={2} path={3}",
                name, fileSystem, sectors, path));
        }

        if (options.ContainsKey("probe"))
        {
            Say("OcidImagingRs5: 写入探针文件 ...");
            foreach (string name in partitionNames)
            {
                string path;
                try
                {
                    path = manager.GetPartitionPath(name);
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }
                try
                {
                    string probe = Path.Combine(path.TrimEnd('\\'), "ocid_probe.txt");
                    File.WriteAllText(probe, "ocid rs5 probe " + name + "\r\n");
                    Say("  探针写入 " + name + " -> " + probe);
                }
                catch (Exception ex)
                {
                    Say("  探针写入失败 " + name + ": " + ex.Message);
                }
            }
        }

        if (command == "build" && !options.ContainsKey("skip-update"))
        {
            string updateInput = Get(options, "update-input");
            if (string.IsNullOrEmpty(updateInput) || !File.Exists(updateInput))
            {
                return Fail("build 需要 --update-input：" + updateInput);
            }
            string stagingRoot = Get(options, "staging");
            if (string.IsNullOrEmpty(stagingRoot))
            {
                try
                {
                    stagingRoot = manager.GetPartitionPath("Data");
                }
                catch (Exception ex)
                {
                    return Fail("没有 Data 分区可用作 staging 根：" + ex.Message);
                }
            }
            stagingRoot = stagingRoot.TrimEnd('\\') + "\\";
            Directory.CreateDirectory(stagingRoot);
            Say("OcidImagingRs5: staging 根 = " + stagingRoot);

            if (StageAndCommit(manager, updateInput, stagingRoot, logPath) != 0)
            {
                return 1;
            }
        }

        Say("OcidImagingRs5: 卸载并保存 VHD ...");
        manager.DismountVirtualHardDisk(false, false, true);
        var info = new FileInfo(fullVhd);
        if (!info.Exists)
        {
            return Fail("VHD 没有生成：" + fullVhd);
        }
        Say(string.Format(CultureInfo.InvariantCulture,
            "OcidImagingRs5: 完成：{0}（{1:N0} 字节），缺失分区 {2} 个", fullVhd, info.Length, missing));
        return missing == 0 ? 0 : 1;
    }

    /// <summary>
    /// Runs the RS5 UpdateDLL pipeline (stage + commit) exactly like
    /// Imaging.StageImage / Imaging.CommitImage do: same store id array, same
    /// staging location (the Data partition), same pool id hand-off.
    /// </summary>
    private static int StageAndCommit(ImageStorageManager manager, string updateInput, string stagingRoot, string logPath)
    {
        IList<ImageStorage> storages = manager.Storages;
        var storeIds = new ImageStructures.STORE_ID[storages.Count];
        for (int i = 0; i < storages.Count; i++)
        {
            storeIds[i] = storages[i].StoreId;
        }
        Say("OcidImagingRs5: UpdateDLL 初始化（store 数 = " + storeIds.Length + "）...");

        // RS5's UpdateMain is an *internal* type in imaging.dll, so the helper has
        // to reach it through reflection.  The members themselves are public and
        // the semantics match Imaging.StageImage / Imaging.CommitImage exactly.
        Assembly asm = typeof(ImageStorageManager).Assembly;
        Type updateType = asm.GetType("Microsoft.WindowsPhone.Imaging.UpdateMain", true);
        Type phaseType = asm.GetType("Microsoft.WindowsPhone.Imaging.IUPhase", true);
        MethodInfo failedMethod = updateType.GetMethod("FAILED", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        object update = Activator.CreateInstance(updateType, true);
        try
        {
            LogUtil.InteropLogString errorLogger = delegate(string s) { Say("[iu][error] " + s); };
            LogUtil.InteropLogString warningLogger = delegate(string s) { Say("[iu][warn ] " + s); };
            LogUtil.InteropLogString infoLogger = delegate(string s) { Say("[iu][info ] " + s); };
            LogUtil.InteropLogString debugLogger = delegate(string s) { Say("[iu][debug] " + s); };

            object[] initArgs =
            {
                storeIds.Length, storeIds, updateInput, stagingRoot,
                string.IsNullOrEmpty(logPath) ? string.Empty : logPath,
                errorLogger, warningLogger, infoLogger, debugLogger
            };
            int hr = (int)Invoke(updateType, update, "Initialize", initArgs);
            if (Failed(failedMethod, hr))
            {
                return Fail("UpdateMain.Initialize 失败（hr=0x" + hr.ToString("X8") + "）");
            }

            Say("OcidImagingRs5: PrepareUpdate（staging）...");
            Invoke(updateType, update, "RegisterProgressCallback",
                new object[] { Enum.Parse(phaseType, "IUPhase_Staging") });
            hr = (int)Invoke(updateType, update, "PrepareUpdate", new object[0]);
            if (Failed(failedMethod, hr))
            {
                return Fail("UpdateMain.PrepareUpdate 失败（hr=0x" + hr.ToString("X8") + "）");
            }
            try
            {
                Say("OcidImagingRs5: staging 完成，用时 " + Invoke(updateType, update, "GetFileStageTime", new object[0]) + " 秒");
            }
            catch (MissingMethodException)
            {
                Say("OcidImagingRs5: staging 完成");
            }

            Guid poolId = manager.GetIdOfFirstPool();
            if (poolId != Guid.Empty)
            {
                Invoke(updateType, update, "SetPoolId", new object[] { poolId });
                Say("OcidImagingRs5: SetPoolId " + poolId);
            }

            Say("OcidImagingRs5: ExecuteUpdate（commit）...");
            Invoke(updateType, update, "RegisterProgressCallback",
                new object[] { Enum.Parse(phaseType, "IUPhase_Commit") });
            hr = (int)Invoke(updateType, update, "ExecuteUpdate", new object[0]);
            if (Failed(failedMethod, hr))
            {
                return Fail("UpdateMain.ExecuteUpdate 失败（hr=0x" + hr.ToString("X8") + "）");
            }
            Say("OcidImagingRs5: commit 完成");
            return 0;
        }
        finally
        {
            IDisposable disposable = update as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
        }
    }

    private static object Invoke(Type type, object target, string name, object[] arguments)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null)
        {
            throw new MissingMethodException(type.FullName, name);
        }
        return method.Invoke(target, arguments);
    }

    private static bool Failed(MethodInfo failedMethod, int hr)
    {
        return (bool)failedMethod.Invoke(null, new object[] { hr });
    }

    private static readonly string[] WellKnownPartitionNames =
    {
        "EFIESP", "HACK_EFIESP", "MainOS", "OSData", "BSP", "MMOS", "PreInstalled",
        "VIRT_EFIESP", "Data"
    };

    /// <summary>
    /// Drops explicit &lt;Id&gt; values for partitions whose GUID the RS5 service
    /// considers fixed (it throws "Unable to override protected partition ...").
    /// Returns the number of ids that were cleared.
    /// </summary>
    private static int ClearWellKnownPartitionIds(ImageGeneratorParameters parameters)
    {
        int cleared = 0;
        foreach (InputStore store in AllStores(parameters))
        {
            if (store.Partitions == null)
            {
                continue;
            }
            foreach (InputPartition partition in store.Partitions)
            {
                if (partition == null || string.IsNullOrEmpty(partition.Id))
                {
                    continue;
                }
                if (WellKnownPartitionNames.Any(n => string.Equals(n, partition.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    partition.Id = null;
                    cleared++;
                }
            }
        }
        return cleared;
    }

    private static IEnumerable<InputStore> AllStores(ImageGeneratorParameters parameters)
    {
        if (parameters.Stores != null)
        {
            foreach (InputStore store in parameters.Stores)
            {
                yield return store;
            }
        }
        if (parameters.StoragePools != null)
        {
            foreach (InputStoragePool pool in parameters.StoragePools)
            {
                if (pool == null || pool.Stores == null)
                {
                    continue;
                }
                foreach (InputStore store in pool.Stores)
                {
                    yield return store;
                }
            }
        }
    }

    private static List<string> EnumeratePartitionNames(FullFlashUpdateImage ffu)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FullFlashUpdateStore store in ffu.Stores)
        {
            foreach (FullFlashUpdatePartition p in store.Partitions)
            {
                if (seen.Add(p.Name))
                {
                    names.Add(p.Name);
                }
            }
        }
        foreach (FullFlashUpdateStoragePool pool in ffu.StoragePools)
        {
            foreach (FullFlashUpdateStore store in pool.Stores)
            {
                foreach (FullFlashUpdatePartition p in store.Partitions)
                {
                    if (seen.Add(p.Name))
                    {
                        names.Add(p.Name);
                    }
                }
            }
        }
        return names;
    }

    /// <summary>
    /// Mounts an *existing* VHD with the RS5 storage service and reports every
    /// partition path.  This is the probe used to answer "can the RS5 stack read
    /// a pool that was produced by the FE stage?" without creating anything.
    /// </summary>
    private static int MountExistingVhd(FullFlashUpdateImage ffu, string vhd)
    {
        if (!File.Exists(vhd))
        {
            return Fail("找不到 --vhd：" + vhd);
        }

        var logger = new IULogger();
        var manager = new ImageStorageManager(logger);
        Say("OcidImagingRs5: 挂载已有 VHD（RS5 服务）...");
        manager.MountVirtualHardDisk(vhd, false);
        Say("OcidImagingRs5: AssignMountPoints ...");
        manager.AssignMountPoints();

        Guid poolId = Guid.Empty;
        try
        {
            poolId = manager.GetIdOfFirstPool();
            Say("OcidImagingRs5: 第一个池 ID = " + poolId);
        }
        catch (Exception ex)
        {
            Say("OcidImagingRs5: GetIdOfFirstPool 失败：" + ex.Message);
        }

        List<string> names = EnumeratePartitionNames(ffu);
        Say("OcidImagingRs5: 分区清单：" + string.Join(", ", names.ToArray()));
        int missing = 0;
        foreach (string name in names)
        {
            string path;
            try
            {
                path = manager.GetPartitionPath(name);
            }
            catch (Exception ex)
            {
                missing++;
                Say("  [缺] " + name + " -> " + ex.Message);
                continue;
            }
            if (string.IsNullOrEmpty(path))
            {
                missing++;
                Say("  [缺] " + name + " -> 空路径");
                continue;
            }
            string fileSystem = "?";
            try
            {
                fileSystem = manager.GetPartitionFileSystem(name);
            }
            catch
            {
            }
            ulong sectors = 0;
            try
            {
                sectors = manager.GetPartitionSizeInSectors(name);
            }
            catch
            {
            }
            Say(string.Format(CultureInfo.InvariantCulture, "  [ok] {0} fs={1} sectors={2} path={3}",
                name, fileSystem, sectors, path));
        }

        Say("OcidImagingRs5: 卸载（保留文件）...");
        manager.DismountVirtualHardDisk(false, false, false);
        Say("OcidImagingRs5: 挂载探测完成，缺失分区 " + missing + " 个");
        return missing == 0 ? 0 : 1;
    }

    private static string Get(Dictionary<string, string> options, string key)
    {
        string value;
        return options.TryGetValue(key, out value) ? value : null;
    }

    private static void PrintInfo()
    {
        Say("OcidImagingRs5: RS5 阶段 helper");
        Say("  imagestorageservicemanaged: " + typeof(ImageStorageManager).Assembly.Location);
        Say("  imagecommon:                " + typeof(FullFlashUpdateImage).Assembly.Location);
        Say("  toolscommon:                " + typeof(IULogger).Assembly.Location);
        Say("  目录: " + AppDomain.CurrentDomain.BaseDirectory);
    }

    private static void Usage()
    {
        Console.WriteLine("用法:");
        Console.WriteLine("  OcidImagingRs5.exe info");
        Console.WriteLine("  OcidImagingRs5.exe disk  --layout <DeviceLayout.xml> --oem-device-platform <OEMDevicePlatform.xml> --vhd <out.vhd> [--probe] [--os-version <v>] [--log <file>]");
        Console.WriteLine("  OcidImagingRs5.exe build --layout <...> --oem-device-platform <...> --vhd <out.vhd> --oem-input <OEMInput.xml> --packages <dir> [--probe] [--log <file>]");
        Console.WriteLine("  OcidImagingRs5.exe mount --layout <...> --oem-device-platform <...> --vhd <existing.vhd> [--log <file>]");
    }
}
