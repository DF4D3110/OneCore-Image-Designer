using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OneCore_Image_Designer.Services
{
    /// <summary>
    /// 离线注册表编辑器服务
    /// 使用 RegLoadKey API 加载 hive 文件（支持系统 hive：SYSTEM/SOFTWARE/DEFAULT 等）
    /// 先复制到临时目录避免文件锁定，需要管理员权限
    /// </summary>
    public class OfflineRegistryService : IDisposable
    {
        #region 日志

        private static readonly string LogDir = Path.Combine(Path.GetTempPath(), "OneCoreImageDesigner_Logs");
        private static readonly string LogFile = Path.Combine(LogDir, $"registry_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        private static void Log(string message)
        {
            try
            {
                if (!Directory.Exists(LogDir))
                    Directory.CreateDirectory(LogDir);
                string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
                System.Diagnostics.Debug.WriteLine(line);
            }
            catch { }
        }

        public static string GetLogFilePath() => LogFile;

        #endregion

        #region P/Invoke

        private static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegLoadKey(IntPtr hKey, string lpSubKey, string lpFile);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);

        // 特权相关
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 启用当前进程的指定特权
        /// </summary>
        private static bool EnablePrivilege(string privilegeName)
        {
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tokenHandle))
                    return false;

                try
                {
                    if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
                        return false;

                    var tp = new TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Privileges = new LUID_AND_ATTRIBUTES
                        {
                            Luid = luid,
                            Attributes = SE_PRIVILEGE_ENABLED
                        }
                    };

                    return AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
                finally
                {
                    CloseHandle(tokenHandle);
                }
            }
            catch
            {
                return false;
            }
        }

        #endregion

        private RegistryKey? _rootKey;
        private bool _writable;
        private string _hivePath = "";
        private string _tempHivePath = "";
        private string _subKeyName = "";
        private bool _disposed = false;
        private bool _hasChanges = false;

        /// <summary>
        /// 是否有未保存的修改
        /// </summary>
        public bool HasChanges => _hasChanges;

        public bool IsLoaded => _rootKey != null;
        public bool IsWritable => _writable;
        public string HivePath => _hivePath;
        public RegistryKey? RootKey => _rootKey;

        /// <summary>
        /// 加载 hive 文件
        /// </summary>
        public bool LoadHive(string hivePath, bool writable = false)
        {
            Log($"=== LoadHive 开始 ===");
            Log($"hivePath: {hivePath}");
            Log($"writable: {writable}");

            try
            {
                if (!File.Exists(hivePath))
                {
                    Log($"❌ 文件不存在: {hivePath}");
                    throw new FileNotFoundException($"Hive 文件不存在: {hivePath}");
                }

                Log($"文件存在，大小: {new FileInfo(hivePath).Length:N0} 字节");

                Dispose();

                // 检查管理员权限
                bool isAdmin = IsRunningAsAdmin();
                Log($"管理员权限: {(isAdmin ? "是" : "否")}");
                if (!isAdmin)
                {
                    Log($"❌ 需要管理员权限");
                    throw new UnauthorizedAccessException("RegLoadKey 需要管理员权限，请以管理员身份运行程序");
                }

                // 复制到临时目录
                string tempDir = Path.Combine(Path.GetTempPath(), "OCID_RegHive_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                _tempHivePath = Path.Combine(tempDir, Path.GetFileName(hivePath));
                Log($"复制到临时目录: {_tempHivePath}");
                File.Copy(hivePath, _tempHivePath, true);
                Log($"复制完成，临时文件大小: {new FileInfo(_tempHivePath).Length:N0} 字节");

                _subKeyName = "OCID_OfflineHive_" + Guid.NewGuid().ToString("N").Substring(0, 16);
                Log($"子键名: HKLM\\{_subKeyName}");

                // 启用 RegLoadKey 所需的特权
                bool restoreOk = EnablePrivilege("SeRestorePrivilege");
                bool backupOk = EnablePrivilege("SeBackupPrivilege");
                Log($"SeRestorePrivilege: {(restoreOk ? "已启用" : "启用失败")}");
                Log($"SeBackupPrivilege: {(backupOk ? "已启用" : "启用失败")}");

                Log($"调用 RegLoadKey...");
                int result = RegLoadKey(HKEY_LOCAL_MACHINE, _subKeyName, _tempHivePath);
                Log($"RegLoadKey 返回: {result}");

                if (result != 0)
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                    _tempHivePath = ""; _subKeyName = "";
                    string errorMsg = $"RegLoadKey 失败 (错误码: {result})";
                    errorMsg += $"\n管理员权限: {(isAdmin ? "是" : "否")}";
                    errorMsg += $"\nSeRestorePrivilege: {(restoreOk ? "已启用" : "启用失败")}";
                    errorMsg += $"\nSeBackupPrivilege: {(backupOk ? "已启用" : "启用失败")}";
                    Log($"❌ {errorMsg}");
                    throw new System.ComponentModel.Win32Exception(result, errorMsg);
                }

                // 登记进挂载台账 —— 让程序自己知道"我挂在哪、挂的是副本还是原件"。
                // ⚠ 注意挂的是**临时目录里的副本**（上面 File.Copy），不是镜像里的真 hive；
                //   而 CBS 挂的是**真 hive**。台账把这两者分开标注，避免误判"我改过了为什么没生效"。
                OfflineHiveLedger.RecordOurs(_subKeyName, _tempHivePath, "离线注册表浏览/编辑（副本）");

                Log($"打开根键: HKLM\\{_subKeyName}, writable={writable}");
                _rootKey = Registry.LocalMachine.OpenSubKey(_subKeyName, writable);
                if (_rootKey == null)
                {
                    Log($"❌ 根键打开失败");
                    RegUnLoadKey(HKEY_LOCAL_MACHINE, _subKeyName);
                    try { Directory.Delete(tempDir, true); } catch { }
                    _tempHivePath = ""; _subKeyName = "";
                    throw new InvalidOperationException("Hive 加载成功但无法打开根键");
                }

                Log($"✅ 根键打开成功");
                Log($"  子键数: {_rootKey.SubKeyCount}");
                Log($"  值数: {_rootKey.ValueCount}");
                Log($"  子键列表: {string.Join(", ", _rootKey.GetSubKeyNames().Take(20))}");

                _writable = writable;
                _hivePath = hivePath;
                Log($"=== LoadHive 完成 ===");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ LoadHive 异常: {ex.GetType().Name}: {ex.Message}");
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// 检查当前进程是否以管理员身份运行
        /// </summary>
        public static bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 打开键（根键不使用 using，避免被意外关闭）
        /// </summary>
        private RegistryKey? OpenKey(string? keyPath, out bool shouldDispose)
        {
            if (_rootKey == null)
            {
                shouldDispose = false;
                return null;
            }
            if (string.IsNullOrEmpty(keyPath))
            {
                shouldDispose = false;  // 根键不 Dispose
                return _rootKey;
            }
            shouldDispose = true;
            return _rootKey.OpenSubKey(keyPath);
        }

        /// <summary>
        /// 获取键信息（子键数、值数）
        /// </summary>
        public (int subKeyCount, int valueCount) GetKeyInfo(string? keyPath = null)
        {
            if (_rootKey == null) return (0, 0);
            try
            {
                var key = OpenKey(keyPath, out bool shouldDispose);
                try
                {
                    return (key?.SubKeyCount ?? 0, key?.ValueCount ?? 0);
                }
                finally
                {
                    if (shouldDispose) key?.Dispose();
                }
            }
            catch { return (0, 0); }
        }

        public string[] GetSubKeyNames(string? keyPath = null)
        {
            if (_rootKey == null)
            {
                Log($"GetSubKeyNames: _rootKey 为 null");
                return Array.Empty<string>();
            }
            try
            {
                Log($"GetSubKeyNames: path='{keyPath ?? "(root)"}'");
                var key = OpenKey(keyPath, out bool shouldDispose);
                try
                {
                    if (key == null)
                    {
                        Log($"GetSubKeyNames: 键打开失败 path='{keyPath}'");
                        return Array.Empty<string>();
                    }
                    var names = key.GetSubKeyNames();
                    Log($"GetSubKeyNames: 返回 {names.Length} 个子键: {string.Join(", ", names.Take(10))}");
                    return names;
                }
                finally
                {
                    if (shouldDispose) key?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log($"GetSubKeyNames 异常: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        public string[] GetValueNames(string? keyPath = null)
        {
            if (_rootKey == null) return Array.Empty<string>();
            try
            {
                Log($"GetValueNames: path='{keyPath ?? "(root)"}'");
                var key = OpenKey(keyPath, out bool shouldDispose);
                try
                {
                    if (key == null)
                    {
                        Log($"GetValueNames: 键打开失败 path='{keyPath}'");
                        return Array.Empty<string>();
                    }
                    var names = key.GetValueNames();
                    Log($"GetValueNames: 返回 {names.Length} 个值: {string.Join(", ", names.Take(10))}");
                    return names;
                }
                finally
                {
                    if (shouldDispose) key?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log($"GetValueNames 异常: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// 获取值（path 在前，valueName 在后）
        /// </summary>
        public object? GetValue(string keyPath, string valueName)
        {
            if (_rootKey == null) return null;
            try
            {
                var key = OpenKey(keyPath, out bool shouldDispose);
                try
                {
                    return key?.GetValue(valueName);
                }
                finally
                {
                    if (shouldDispose) key?.Dispose();
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 获取值类型（path 在前，valueName 在后）
        /// </summary>
        public RegistryValueKind GetValueKind(string keyPath, string valueName)
        {
            if (_rootKey == null) return RegistryValueKind.Unknown;
            try
            {
                var key = OpenKey(keyPath, out bool shouldDispose);
                try
                {
                    return key?.GetValueKind(valueName) ?? RegistryValueKind.Unknown;
                }
                finally
                {
                    if (shouldDispose) key?.Dispose();
                }
            }
            catch { return RegistryValueKind.Unknown; }
        }

        /// <summary>
        /// 设置值（path 在前，valueName 在后）。
        /// </summary>
        /// <remarks>
        /// ⚠ 这里**不能**复用 <see cref="OpenKey"/>：它用 <c>OpenSubKey</c> 只读打开，
        /// 且键不存在时返回 null。用它写值有两个叠加后果：
        ///   ① 只读句柄上的写入不生效；
        ///   ② 干净镜像里本就不存在的键（例如 imagestate 的
        ///      <c>Microsoft\Windows\CurrentVersion\Setup\ImageServicingData</c>）
        ///      直接失败。
        /// 实测现象：E2E 组装流程里 <c>imagestate 写入 0 处</c>，而 hive 本身挂载成功。
        /// 因此改用 <c>CreateSubKey(writable: true)</c> —— 既可写，又能按需建键。
        /// </remarks>
        public bool SetValue(string keyPath, string valueName, object? value, RegistryValueKind valueKind)
        {
            if (_rootKey == null || !_writable) return false;

            RegistryKey? key = null;
            bool shouldDispose = false;
            try
            {
                Log($"SetValue: path='{keyPath}', name='{valueName}', kind={valueKind}");

                if (string.IsNullOrEmpty(keyPath))
                {
                    key = _rootKey;                 // 根键不 Dispose
                }
                else
                {
                    // CreateSubKey 在键已存在时等价于可写打开，不存在时创建
                    key = _rootKey.CreateSubKey(keyPath, writable: true);
                    shouldDispose = true;
                }

                if (key == null)
                {
                    Log($"SetValue: 无法创建/打开键 path='{keyPath}'");
                    return false;
                }

                key.SetValue(valueName, value, valueKind);
                key.Flush();
                _hasChanges = true;
                Log($"SetValue: 成功，已标记为有修改");
                return true;
            }
            catch (Exception ex)
            {
                Log($"SetValue 异常: {ex.Message}");
                return false;
            }
            finally
            {
                if (shouldDispose) key?.Dispose();
            }
        }

        /// <summary>
        /// 搜索注册表
        /// </summary>
        public List<RegistrySearchResult> Search(string searchTerm, bool searchInKeys, bool searchInValues, bool searchInData, int maxResults = 500)
        {
            var results = new List<RegistrySearchResult>();
            if (_rootKey == null || string.IsNullOrEmpty(searchTerm)) return results;

            string searchLower = searchTerm.ToLowerInvariant();
            SearchRecursive(_rootKey, "", searchLower, searchInKeys, searchInValues, searchInData, results, maxResults);
            return results;
        }

        private void SearchRecursive(RegistryKey root, string currentPath, string searchLower,
            bool searchInKeys, bool searchInValues, bool searchInData,
            List<RegistrySearchResult> results, int maxResults)
        {
            if (results.Count >= maxResults) return;

            try
            {
                bool shouldDispose;
                RegistryKey? key;
                if (string.IsNullOrEmpty(currentPath))
                {
                    key = root;
                    shouldDispose = false;  // 根键不 Dispose
                }
                else
                {
                    key = root.OpenSubKey(currentPath);
                    shouldDispose = true;
                }

                try
                {
                    if (key == null) return;

                    // 搜索键名
                    if (searchInKeys)
                    {
                        string keyName = currentPath.Split('\\').LastOrDefault() ?? "(root)";
                        if (keyName.ToLowerInvariant().Contains(searchLower))
                        {
                            results.Add(new RegistrySearchResult
                            {
                                Path = currentPath,
                                ValueName = "(键名)",
                                ValueKind = RegistryValueKind.Unknown,
                                ValueData = keyName
                            });
                        }
                    }

                    // 搜索值名和值数据
                    foreach (var valueName in key.GetValueNames())
                    {
                        if (results.Count >= maxResults) break;

                        bool match = false;
                        if (searchInValues && valueName.ToLowerInvariant().Contains(searchLower))
                            match = true;

                        string valueStr = "";
                        if (!match && searchInData)
                        {
                            var value = key.GetValue(valueName);
                            valueStr = FormatValue(value, key.GetValueKind(valueName));
                            if (valueStr.ToLowerInvariant().Contains(searchLower))
                                match = true;
                        }

                        if (match)
                        {
                            if (string.IsNullOrEmpty(valueStr))
                            {
                                var value = key.GetValue(valueName);
                                valueStr = FormatValue(value, key.GetValueKind(valueName));
                            }
                            results.Add(new RegistrySearchResult
                            {
                                Path = currentPath,
                                ValueName = valueName,
                                ValueKind = key.GetValueKind(valueName),
                                ValueData = valueStr
                            });
                        }
                    }

                    // 递归子键
                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        if (results.Count >= maxResults) break;
                        string subPath = string.IsNullOrEmpty(currentPath) ? subKeyName : currentPath + "\\" + subKeyName;
                        SearchRecursive(root, subPath, searchLower, searchInKeys, searchInValues, searchInData, results, maxResults);
                    }
                }
                finally
                {
                    if (shouldDispose) key?.Dispose();
                }
            }
            catch { }
        }

        /// <summary>
        /// 导出指定键为 .reg 文件（path 在前，outputPath 在后）
        /// </summary>
        public bool ExportKey(string keyPath, string outputPath)
        {
            if (_rootKey == null) return false;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Windows Registry Editor Version 5.00");
                sb.AppendLine();

                var key = OpenKey(keyPath, out bool shouldDispose);
                try
                {
                    if (key == null) return false;

                    string regPath = "[HKEY_LOCAL_MACHINE\\" + (string.IsNullOrEmpty(keyPath) ? _subKeyName : _subKeyName + "\\" + keyPath) + "]";
                    ExportKeyRecursive(key, regPath, sb);

                    File.WriteAllText(outputPath, sb.ToString(), Encoding.Unicode);
                    return true;
                }
                finally
                {
                    if (shouldDispose) key?.Dispose();
                }
            }
            catch { return false; }
        }

        private void ExportKeyRecursive(RegistryKey key, string regPath, StringBuilder sb)
        {
            try
            {
                sb.AppendLine(regPath);

                var defaultValue = key.GetValue("");
                if (defaultValue != null)
                    sb.AppendLine("@=" + FormatRegValue(defaultValue, key.GetValueKind("")));

                foreach (var valueName in key.GetValueNames())
                {
                    if (valueName == "") continue;
                    var value = key.GetValue(valueName);
                    var kind = key.GetValueKind(valueName);
                    sb.AppendLine("\"" + valueName.Replace("\"", "\\\"") + "\"=" + FormatRegValue(value, kind));
                }

                sb.AppendLine();

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey != null)
                    {
                        string subRegPath = regPath.TrimEnd(']') + "\\" + subKeyName + "]";
                        ExportKeyRecursive(subKey, subRegPath, sb);
                    }
                }
            }
            catch { }
        }

        private string FormatRegValue(object? value, RegistryValueKind kind)
        {
            if (value == null) return "\"\"";
            try
            {
                return kind switch
                {
                    RegistryValueKind.String or RegistryValueKind.ExpandString => "\"" + value.ToString()?.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
                    RegistryValueKind.DWord => "dword:" + Convert.ToUInt32(value).ToString("X8"),
                    RegistryValueKind.QWord => "hex(b):" + BitConverter.ToString(BitConverter.GetBytes(Convert.ToUInt64(value))).Replace("-", ","),
                    RegistryValueKind.MultiString => "hex(7):" + string.Join(",", Encoding.Unicode.GetBytes(string.Join("\0", (string[])value) + "\0\0").Select(b => b.ToString("X2"))),
                    RegistryValueKind.Binary => "hex:" + string.Join(",", ((byte[])value).Select(b => b.ToString("X2"))),
                    _ => "\"" + value.ToString() + "\""
                };
            }
            catch { return "\"\""; }
        }

        public static string FormatValue(object? value, RegistryValueKind kind)
        {
            if (value == null) return "(null)";
            try
            {
                return kind switch
                {
                    RegistryValueKind.String or RegistryValueKind.ExpandString => value.ToString() ?? "",
                    RegistryValueKind.DWord => $"0x{Convert.ToUInt32(value):X8} ({Convert.ToUInt32(value)})",
                    RegistryValueKind.QWord => $"0x{Convert.ToUInt64(value):X16} ({Convert.ToUInt64(value)})",
                    RegistryValueKind.MultiString => string.Join(" | ", (string[])value),
                    RegistryValueKind.Binary => $"{((byte[])value).Length} bytes",
                    _ => value.ToString() ?? ""
                };
            }
            catch { return value.ToString() ?? ""; }
        }

        /// <summary>
        /// 保存修改到原文件（先关闭注册表，再把临时文件复制回原文件）
        /// </summary>
        /// <returns>是否成功</returns>
        public bool SaveToOriginal()
        {
            if (!_writable || !_hasChanges)
            {
                Log($"SaveToOriginal: 无需保存 (writable={_writable}, hasChanges={_hasChanges})");
                return true;
            }

            if (string.IsNullOrEmpty(_hivePath) || string.IsNullOrEmpty(_tempHivePath))
            {
                Log($"SaveToOriginal: 路径为空，无法保存");
                return false;
            }

            try
            {
                Log($"SaveToOriginal: 开始保存到原文件: {_hivePath}");

                // 先关闭注册表键
                try { _rootKey?.Close(); } catch { }
                _rootKey = null;

                // 卸载 hive
                if (!string.IsNullOrEmpty(_subKeyName))
                {
                    try { RegUnLoadKey(HKEY_LOCAL_MACHINE, _subKeyName); } catch { }
                    OfflineHiveLedger.ForgetOurs(_subKeyName);
                }

                // 等待文件释放
                System.Threading.Thread.Sleep(200);

                // 备份原文件
                string backupPath = _hivePath + ".bak";
                try
                {
                    if (File.Exists(_hivePath))
                    {
                        File.Copy(_hivePath, backupPath, true);
                        Log($"SaveToOriginal: 已备份原文件到: {backupPath}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"SaveToOriginal: 备份失败（不影响保存）: {ex.Message}");
                }

                // 复制临时文件回原文件
                File.Copy(_tempHivePath, _hivePath, true);
                Log($"SaveToOriginal: ✅ 已保存到原文件: {_hivePath}");

                _hasChanges = false;

                // 重新加载 hive，以便继续编辑
                Log($"SaveToOriginal: 重新加载 hive 以便继续编辑");
                ReloadHive();

                return true;
            }
            catch (Exception ex)
            {
                Log($"SaveToOriginal: ❌ 保存失败: {ex.Message}");
                // 尝试重新加载
                try { ReloadHive(); } catch { }
                return false;
            }
        }

        /// <summary>
        /// 重新加载 hive（保存后调用）
        /// </summary>
        private void ReloadHive()
        {
            if (string.IsNullOrEmpty(_hivePath) || string.IsNullOrEmpty(_tempHivePath)) return;

            // 重新复制到临时目录
            string tempDir = Path.GetDirectoryName(_tempHivePath) ?? "";
            if (!Directory.Exists(tempDir))
                Directory.CreateDirectory(tempDir);
            File.Copy(_hivePath, _tempHivePath, true);

            _subKeyName = "OCID_OfflineHive_" + Guid.NewGuid().ToString("N").Substring(0, 16);

            EnablePrivilege("SeRestorePrivilege");
            EnablePrivilege("SeBackupPrivilege");

            int result = RegLoadKey(HKEY_LOCAL_MACHINE, _subKeyName, _tempHivePath);
            if (result == 0)
            {
                // 重新挂载也要登记（旧挂载点已被 Dispose 注销）
                OfflineHiveLedger.RecordOurs(_subKeyName, _tempHivePath, "离线注册表浏览/编辑（副本·重载）");
                _rootKey = Registry.LocalMachine.OpenSubKey(_subKeyName, _writable);
                Log($"ReloadHive: 成功，子键名: {_subKeyName}");
            }
            else
            {
                Log($"ReloadHive: 失败，错误码: {result}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _rootKey?.Close(); } catch { }
            _rootKey = null;

            if (!string.IsNullOrEmpty(_subKeyName))
            {
                try { RegUnLoadKey(HKEY_LOCAL_MACHINE, _subKeyName); } catch { }
                OfflineHiveLedger.ForgetOurs(_subKeyName);
                _subKeyName = "";
            }

            if (!string.IsNullOrEmpty(_tempHivePath))
            {
                try
                {
                    string? tempDir = Path.GetDirectoryName(_tempHivePath);
                    if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch { }
                _tempHivePath = "";
            }

            _hivePath = "";
            _writable = false;
        }

        ~OfflineRegistryService() { Dispose(); }
    }

    public class RegistrySearchResult
    {
        public string Path { get; set; } = "";
        public string ValueName { get; set; } = "";
        public RegistryValueKind ValueKind { get; set; }
        public string ValueData { get; set; } = "";
    }
}
