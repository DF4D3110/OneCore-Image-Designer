// OCID - 服务栈原生库的 C 导出面（供 C# 通过 P/Invoke 调用）
//
// ── 为什么要有这个文件 ────────────────────────────────────────────────────
//
// 到这一轮为止，规则实现（Identity / Manifest / WcmSchema / RegistryValue /
// CmiInstaller）**只存在于测试和 ocid_manifest.exe 里**，产品那侧的 C#
// (`Services/OcidSmiService.cs`) 一个都没用上：它自己另写了一套类型解析和字节拼装
// （`ToRegistryKind` / `ConvertData`），和 C++ 侧**并行漂移**。
//
// 用户的方向是"程序完全自带、重写以 C++ 为主"。那就必须让 C# 能调到 C++ 的规则，
// 否则"一份实现"这个前提不成立 —— 而注册表字节这类东西两边不一致会**静默写坏**。
//
// ⇒ 本文件把规则以稳定 C ABI 导出（`__cdecl` + .def 去修饰名），C# 侧用
//    `[DllImport("OcidServicing.dll", CallingConvention = Cdecl)]` 调。
//
// ── 约定 ──────────────────────────────────────────────────────────────────
//
//  · 字符串一律 UTF-16（`wchar_t`）；C# 用 `CharSet.Unicode`。
//  · 字节缓冲由调用方提供，函数返回所需长度；`cap` 不够 ⇒ 返回 `OCID_ERR_BUFFER`
//    且把 `*outLen` 写成需要的长度（调用方据此扩容重试，避免两趟猜测）。
//  · **不抛异常**：任何 std::exception 都被兜住转成 `OCID_ERR_INTERNAL`，
//    因为异常穿过 P/Invoke 边界会直接杀进程。
//  · 失败时把原因写进 `errBuf`（可空）。规则失败（类型未知/文本非法）**不是**
//    异常情况，见下面各自的返回码。

#include "ocid/CmiInstaller.h"
#include "ocid/MacroExpander.h"
#include "ocid/RegistryValue.h"
#include "ocid/SecurityDescriptor.h"
#include "ocid/WcmSchema.h"

#include <cstring>
#include <string>
#include <vector>

using namespace ocid;

namespace {

    /// 返回码。0 恒为成功。
    enum
    {
        OCID_OK = 0,
        OCID_ERR_ARGUMENT = 1,   // 空指针 / 长度不一致
        OCID_ERR_BUFFER = 2,     // 调用方缓冲不足（*outLen 已写成所需长度）
        OCID_ERR_UNKNOWN_TYPE = 3,
        OCID_ERR_BAD_VALUE = 4,
        OCID_ERR_MERGE = 5,      // 类型不同 / 长度非法 ⇒ 调用方应跳过该值
        OCID_ERR_INTERNAL = 6,   // 未预期异常
    };

    void SetErr(wchar_t* buf, unsigned int cap, const std::wstring& msg)
    {
        if (!buf || cap == 0) return;
        unsigned int n = (unsigned int)msg.size();
        if (n > cap - 1) n = cap - 1;
        if (n) memcpy(buf, msg.c_str(), n * sizeof(wchar_t));
        buf[n] = L'\0';
    }

    int CopyOut(const std::vector<uint8_t>& bytes, unsigned char* out, unsigned int cap,
                unsigned int* outLen)
    {
        if (outLen) *outLen = (unsigned int)bytes.size();
        if (bytes.empty()) return OCID_OK;          // 合法的空值（例如 REG_NONE）
        if (!out) return OCID_ERR_ARGUMENT;
        if (cap < bytes.size()) return OCID_ERR_BUFFER;
        memcpy(out, bytes.data(), bytes.size());
        return OCID_OK;
    }

    std::wstring W(const wchar_t* s) { return s ? std::wstring(s) : std::wstring(); }

    /// 宽字符串出参：`cap` 以**字符**计（含结尾 NUL）。不够 ⇒ `OCID_ERR_BUFFER`，
    /// 且 `*outLen` 已写成"不含 NUL 的所需长度"，调用方据此扩容重试。
    int CopyOutStr(const std::wstring& s, wchar_t* out, unsigned int cap, unsigned int* outLen)
    {
        if (outLen) *outLen = (unsigned int)s.size();
        if (!out) return s.empty() ? OCID_OK : OCID_ERR_ARGUMENT;
        if (cap < s.size() + 1) return OCID_ERR_BUFFER;
        if (!s.empty()) memcpy(out, s.c_str(), s.size() * sizeof(wchar_t));
        out[s.size()] = L'\0';
        return OCID_OK;
    }

    /// 空指针视为"不覆盖"。
    std::wstring Opt(const wchar_t* s) { return (s && *s) ? std::wstring(s) : std::wstring(); }

    RuntimePaths MakePaths(const wchar_t* windowsDir, const wchar_t* pf, const wchar_t* pfX86,
                           const wchar_t* cf, const wchar_t* pd, const wchar_t* users)
    {
        RuntimePaths p = RuntimePaths::FromWindowsDir( W( windowsDir ) );
        p.ApplyImageOverrides( Opt( pf ), Opt( pfX86 ), Opt( cf ), Opt( pd ), Opt( users ) );
        return p;
    }

} // namespace

extern "C" {

    /// 自检：返回 ABI 版本。C# 侧启动时对一次，防止 DLL/exe 不同步。
    int __cdecl OcidServicingVersion(void)
    {
        return 1;
    }

    /// 清单里的 `valueType` 文本 → 真实 Win32 REG_* 码。0 表示未知类型。
    unsigned int __cdecl OcidRegValueTypeCode(const wchar_t* valueType)
    {
        RegValueType t = RegValueType::Unknown;
        if (!ParseRegValueType(W(valueType), t)) return 0;
        return Win32RegType(t);
    }

    /// 值文本 → 精确要写进 hive 的字节。
    ///
    /// 返回 0 成功 / 3 类型未知 / 4 文本非法 / 2 缓冲不足 / 1 参数错。
    /// `*outHasMacro`：值里还留着未展开的 `$(…)`（上层应告警，见 RegistryValue.h）。
    int __cdecl OcidRegValueEncode(const wchar_t* valueType,
                                   const wchar_t* text,
                                   unsigned char* outBytes, unsigned int cap,
                                   unsigned int* outLen, int* outHasMacro,
                                   wchar_t* errBuf, unsigned int errCap)
    {
        if (outHasMacro) *outHasMacro = 0;
        if (outLen) *outLen = 0;
        try
        {
            RegValueBlob blob = EncodeRegValue(W(valueType), W(text));
            if (!blob.ok)
            {
                SetErr(errBuf, errCap, blob.error);

                RegValueType t = RegValueType::Unknown;
                if (!ParseRegValueType(W(valueType), t)) return OCID_ERR_UNKNOWN_TYPE;
                return OCID_ERR_BAD_VALUE;
            }
            if (outHasMacro) *outHasMacro = blob.hasUnresolvedMacro ? 1 : 0;
            return CopyOut(blob.bytes, outBytes, cap, outLen);
        }
        catch (...)
        {
            SetErr(errBuf, errCap, L"编码时发生未预期异常");
            return OCID_ERR_INTERNAL;
        }
    }

    /// append / prepend 的合并（`CMI::Utility::RawRegistryValue::Merge` @0x10110423）。
    ///
    /// ⚠ 非字符串类型的 append/prepend 语义是**字节拼接**，不是按位 OR。
    /// 返回 5 表示类型不同 / 长度非法 ⇒ 调用方**跳过该值**（不是写一半）。
    int __cdecl OcidRegValueMerge(unsigned int typeCode,
                                  const unsigned char* existing, unsigned int existingLen,
                                  const unsigned char* incoming, unsigned int incomingLen,
                                  int prepend,
                                  unsigned char* outBytes, unsigned int cap, unsigned int* outLen)
    {
        if (outLen) *outLen = 0;
        try
        {
            std::vector<uint8_t> a, b, merged;
            if (existingLen) { if (!existing) return OCID_ERR_ARGUMENT; a.assign(existing, existing + existingLen); }
            if (incomingLen) { if (!incoming) return OCID_ERR_ARGUMENT; b.assign(incoming, incoming + incomingLen); }

            if (!MergeRegValueBytes(typeCode, a, b, prepend != 0, merged)) return OCID_ERR_MERGE;
            return CopyOut(merged, outBytes, cap, outLen);
        }
        catch (...)
        {
            return OCID_ERR_INTERNAL;
        }
    }

    /// ★ 核心：CI 落地决策。
    ///
    /// 输入 = 清单声明（mechanism / operationHint / owner / valueType / value）
    ///      + hive 现状（valueExists / existingTypeCode / existing 字节）。
    /// 输出 = 该不该写（`*outWillWrite`）、写成什么（`outBytes` + `*outTypeCode`）、
    ///        为什么（`noteBuf`；action 的枚举值见 `WriteAction`）。
    ///
    /// 返回 0 表示**决策算出来了**（哪怕结论是"不写"）；非 0 表示参数/编码有错。
    /// 注意 `SkipOwned` 是正常路径（owner 组件 + 值已存在 + replace）。
    int __cdecl OcidCmiResolve(const wchar_t* mechanism,
                               const wchar_t* operationHint,
                               int owner,
                               const wchar_t* valueType,
                               const wchar_t* valueText,
                               int valueExists,
                               unsigned int existingTypeCode,
                               const unsigned char* existing, unsigned int existingLen,
                               unsigned char* outBytes, unsigned int outCap, unsigned int* outLen,
                               unsigned int* outTypeCode,
                               int* outAction, int* outWillWrite,
                               wchar_t* noteBuf, unsigned int noteCap,
                               wchar_t* errBuf, unsigned int errCap)
    {
        if (outLen) *outLen = 0;
        if (outTypeCode) *outTypeCode = 0;
        if (outAction) *outAction = (int)WriteAction::Write;
        if (outWillWrite) *outWillWrite = 0;
        SetErr(noteBuf, noteCap, L"");
        SetErr(errBuf, errCap, L"");

        try
        {
            WcmRegistryOperation op;
            op.mechanism = W(mechanism);
            op.operationHint = W(operationHint);
            op.owner = owner != 0;
            op.valueType = W(valueType);
            op.data = W(valueText);

            ExistingValue cur;
            cur.exists = valueExists != 0;
            cur.typeCode = existingTypeCode;
            if (cur.exists && existingLen)
            {
                if (!existing) return OCID_ERR_ARGUMENT;
                cur.data.assign(existing, existing + existingLen);
            }

            WriteDecision d;
            std::wstring err;
            if (!DecideRegistryWrite(op, cur, d, &err))
            {
                SetErr(errBuf, errCap, err);
                return OCID_ERR_BAD_VALUE;
            }

            if (outAction) *outAction = (int)d.action;
            if (outWillWrite) *outWillWrite = d.WillWrite() ? 1 : 0;
            if (outTypeCode) *outTypeCode = d.typeCode;
            SetErr(noteBuf, noteCap, d.note);

            if (!d.WillWrite())
            {
                // 不写：数据无意义，但长度给 0 让调用方明确知道没有内容。
                if (outLen) *outLen = 0;
                return OCID_OK;
            }
            return CopyOut(d.data, outBytes, outCap, outLen);
        }
        catch (...)
        {
            SetErr(errBuf, errCap, L"决策时发生未预期异常");
            return OCID_ERR_INTERNAL;
        }
    }

    /// 决策名（日志用）：0..3 对应 Write / SkipOwned / SkipMergeFailed / InvalidHint。
    const wchar_t* __cdecl OcidWriteActionName(int action)
    {
        return WriteActionName((WriteAction)action);
    }

    /// 展开清单里的 `$(runtime.*)` 宏。基础路径按 `windowsDir`（例如 `P:\Windows`）推导；
    /// 其余 5 个目录传 NULL 或空串表示"不覆盖"（用约定值）。
    ///
    /// 不认识的宏（`$(build.nttree)` / `$(string.*)` / `$(runtime.telnetClientsGroupSID)` …）
    /// **原样保留**，并把名字写进 `leftoverBuf`（`;` 分隔，便于调用方打日志）。
    /// 残留本身**不算失败**：调用方按返回值 0 成功处理，自己看 `leftoverLen` 决定是否告警。
    int __cdecl OcidExpandMacros(const wchar_t* windowsDir, const wchar_t* text,
                                 wchar_t* outBuf, unsigned int outCap, unsigned int* outLen,
                                 wchar_t* leftoverBuf, unsigned int leftoverCap,
                                 unsigned int* leftoverLen)
    {
        if (outLen) *outLen = 0;
        if (leftoverLen) *leftoverLen = 0;
        try
        {
            const RuntimePaths p = MakePaths(windowsDir, nullptr, nullptr, nullptr, nullptr, nullptr);
            const MacroExpansion r = ExpandMacros(W(text), p);

            std::wstring leftover;
            for (std::size_t i = 0; i < r.unresolved.size(); ++i)
            {
                if (i) leftover += L";";
                leftover += r.unresolved[i];
            }
            if (leftoverLen) *leftoverLen = (unsigned int)leftover.size();
            if (leftoverBuf && leftoverCap >= leftover.size() + 1)
            {
                if (!leftover.empty())
                    memcpy(leftoverBuf, leftover.c_str(), leftover.size() * sizeof(wchar_t));
                leftoverBuf[leftover.size()] = L'\0';
            }

            return CopyOutStr(r.text, outBuf, outCap, outLen);
        }
        catch (...)
        {
            return OCID_ERR_INTERNAL;
        }
    }

    /// 同上，但允许用镜像自己声明的目录覆盖基础路径（`x:\Program Files` 那种占位盘符）。
    int __cdecl OcidExpandMacrosEx(const wchar_t* windowsDir,
                                   const wchar_t* programFilesDir,
                                   const wchar_t* programFilesX86Dir,
                                   const wchar_t* commonFilesDir,
                                   const wchar_t* programDataDir,
                                   const wchar_t* usersDir,
                                   const wchar_t* text,
                                   wchar_t* outBuf, unsigned int outCap, unsigned int* outLen,
                                   wchar_t* leftoverBuf, unsigned int leftoverCap,
                                   unsigned int* leftoverLen)
    {
        if (outLen) *outLen = 0;
        if (leftoverLen) *leftoverLen = 0;
        try
        {
            const RuntimePaths p = MakePaths(windowsDir, programFilesDir, programFilesX86Dir,
                                             commonFilesDir, programDataDir, usersDir);
            const MacroExpansion r = ExpandMacros(W(text), p);

            std::wstring leftover;
            for (std::size_t i = 0; i < r.unresolved.size(); ++i)
            {
                if (i) leftover += L";";
                leftover += r.unresolved[i];
            }
            if (leftoverLen) *leftoverLen = (unsigned int)leftover.size();
            if (leftoverBuf && leftoverCap >= leftover.size() + 1)
            {
                if (!leftover.empty())
                    memcpy(leftoverBuf, leftover.c_str(), leftover.size() * sizeof(wchar_t));
                leftoverBuf[leftover.size()] = L'\0';
            }

            return CopyOutStr(r.text, outBuf, outCap, outLen);
        }
        catch (...)
        {
            return OCID_ERR_INTERNAL;
        }
    }

    /// 已知的 `runtime.*` 宏名表，`;` 分隔（工具/诊断用）。
    int __cdecl OcidKnownMacroNames(wchar_t* outBuf, unsigned int outCap, unsigned int* outLen)
    {
        std::size_t n = 0;
        const wchar_t* const* names = KnownRuntimeMacroNames(&n);
        std::wstring all;
        for (std::size_t i = 0; i < n; ++i)
        {
            if (i) all += L";";
            all += names[i];
        }
        return CopyOutStr(all, outBuf, outCap, outLen);
    }

    /// 把 SDDL 写到**已打开的键句柄**上（`RegSetKeySecurity`）。
    ///
    /// `keyHandle` 是 `HKEY`（C# 侧用 `RegistryKey.Handle.DangerousGetHandle()`）。
    /// `*outApplied`：1 = 已写入，0 = 该 SDDL 是 `(inherit)` 被按语义跳过 —— **两者都算成功**，
    /// 把"跳过"混进失败里会让上层的计数失去意义。
    ///
    /// 返回 0 成功 / 1 参数错 / 4 规则或 Win32 失败（原因在 errBuf）。
    int __cdecl OcidApplyKeySddl(void* keyHandle, const wchar_t* sddl,
                                 int* outApplied,
                                 wchar_t* errBuf, unsigned int errCap)
    {
        if (outApplied) *outApplied = 0;
        if (!keyHandle || !sddl) return OCID_ERR_ARGUMENT;
        try
        {
            std::wstring err;
            const SddlApplyResult r = ApplySddlToKey(keyHandle, W(sddl), err);
            if (r == SddlApplyResult::SkippedInherit) return OCID_OK;   // 合法跳过
            if (r == SddlApplyResult::Failed)
            {
                SetErr(errBuf, errCap, err);
                return OCID_ERR_BAD_VALUE;
            }
            if (outApplied) *outApplied = 1;
            return OCID_OK;
        }
        catch (...)
        {
            SetErr(errBuf, errCap, L"应用 SDDL 时发生未预期异常");
            return OCID_ERR_INTERNAL;
        }
    }

    /// 回读键的安全描述符 → SDDL 文本（`informationBits` 为 0 ⇒ OWNER|GROUP|DACL）。
    int __cdecl OcidReadKeySddl(void* keyHandle, unsigned int informationBits,
                                wchar_t* outBuf, unsigned int outCap, unsigned int* outLen,
                                wchar_t* errBuf, unsigned int errCap)
    {
        if (outLen) *outLen = 0;
        if (!keyHandle) return OCID_ERR_ARGUMENT;
        try
        {
            std::wstring sddl, err;
            if (!ReadKeySddl(keyHandle, informationBits, sddl, err))
            {
                SetErr(errBuf, errCap, err);
                return OCID_ERR_BAD_VALUE;
            }
            return CopyOutStr(sddl, outBuf, outCap, outLen);
        }
        catch (...)
        {
            SetErr(errBuf, errCap, L"回读安全描述符时发生未预期异常");
            return OCID_ERR_INTERNAL;
        }
    }

    /// SDDL 的文本级解读（段存在性 / ACE 计数 / 需设置的 SECURITY_INFORMATION 位）。
    /// 供上层在落盘前决策与告警，不碰注册表。
    ///
    /// `outDaclSids` 是 DACL 的**规范 SID 列表**（`;` 分隔），供回读校验做集合比对
    /// —— 比 ACE 条数稳（Windows 应用 DACL 时会拆分"仅继承"ACE，条数会变）。
    int __cdecl OcidDescribeSddl(const wchar_t* sddl,
                                 int* outHasOwner, int* outHasGroup, int* outHasDacl, int* outHasSacl,
                                 int* outDaclProtected, int* outDaclNoAccessControl,
                                 int* outDaclAceCount, int* outSaclAceCount,
                                 unsigned int* outSecurityInformation,
                                 wchar_t* outDaclSids, unsigned int daclSidsCap,
                                 unsigned int* outDaclSidsLen, int* outDaclSidsAvailable,
                                 wchar_t* errBuf, unsigned int errCap)
    {
        if (outDaclSidsLen) *outDaclSidsLen = 0;
        if (outDaclSidsAvailable) *outDaclSidsAvailable = 0;
        if (!sddl) return OCID_ERR_ARGUMENT;
        try
        {
            SddlInfo info;
            std::wstring err;
            if (!DescribeSddl(W(sddl), info, err))
            {
                SetErr(errBuf, errCap, err);
                return OCID_ERR_BAD_VALUE;
            }
            if (outHasOwner) *outHasOwner = info.hasOwner ? 1 : 0;
            if (outHasGroup) *outHasGroup = info.hasGroup ? 1 : 0;
            if (outHasDacl)  *outHasDacl  = info.hasDacl  ? 1 : 0;
            if (outHasSacl)  *outHasSacl  = info.hasSacl  ? 1 : 0;
            if (outDaclProtected) *outDaclProtected = info.daclProtected ? 1 : 0;
            if (outDaclNoAccessControl) *outDaclNoAccessControl = info.daclNoAccessControl ? 1 : 0;
            if (outDaclAceCount) *outDaclAceCount = info.daclAceCount;
            if (outSaclAceCount) *outSaclAceCount = info.saclAceCount;
            if (outSecurityInformation) *outSecurityInformation = SddlSecurityInformation(info);
            if (outDaclSidsAvailable) *outDaclSidsAvailable = info.daclSidsAvailable ? 1 : 0;

            std::wstring joined;
            for (std::size_t k = 0; k < info.daclSids.size(); ++k)
            {
                if (k) joined += L';';
                joined += info.daclSids[k];
            }
            if (outDaclSids || outDaclSidsLen)
            {
                if (!outDaclSids)
                {
                    // 只问长度（没给缓冲）：按"缓冲不足"回，与 cap 不够时同一个约定
                    // —— `*outDaclSidsLen` 已经是所需长度。别回 OCID_ERR_ARGUMENT：
                    // 那会让"两趟调用"的第一趟看起来像参数错误。
                    return OCID_ERR_BUFFER;
                }
                int rc = CopyOutStr(joined, outDaclSids, daclSidsCap, outDaclSidsLen);
                if (rc != OCID_OK) return rc;   // 缓冲不足：*outDaclSidsLen 已是所需长度
            }
            return OCID_OK;
        }
        catch (...)
        {
            SetErr(errBuf, errCap, L"解读 SDDL 时发生未预期异常");
            return OCID_ERR_INTERNAL;
        }
    }

    /// 把 SDDL 写到**文件/目录路径**上（`SetNamedSecurityInfoW`）—— CSI 落位用。
    ///
    /// 与 <c>OcidApplyKeySddl</c> 的差别不只是 API：文件这条路**必须**由调用层显式说明
    /// DACL 是否受保护（`SetNamedSecurityInfoW` 不看描述符里的 SE_DACL_PROTECTED），
    /// 该判断已做在 `ApplySddlToPath` 内部，C# 侧不需要（也不应该）再算一次。
    int __cdecl OcidApplyPathSddl(const wchar_t* path, const wchar_t* sddl,
                                  int* outApplied,
                                  wchar_t* errBuf, unsigned int errCap)
    {
        if (outApplied) *outApplied = 0;
        if (!path || !sddl) return OCID_ERR_ARGUMENT;
        try
        {
            std::wstring err;
            const SddlApplyResult r = ApplySddlToPath(W(path), W(sddl), err);
            if (r == SddlApplyResult::SkippedInherit) return OCID_OK;   // 合法跳过
            if (r == SddlApplyResult::Failed)
            {
                SetErr(errBuf, errCap, err);
                return OCID_ERR_BAD_VALUE;
            }
            if (outApplied) *outApplied = 1;
            return OCID_OK;
        }
        catch (...)
        {
            SetErr(errBuf, errCap, L"应用路径 SDDL 时发生未预期异常");
            return OCID_ERR_INTERNAL;
        }
    }

    /// 回读文件/目录的安全描述符 → SDDL 文本（`informationBits` 为 0 ⇒ OWNER|GROUP|DACL）。
    int __cdecl OcidReadPathSddl(const wchar_t* path, unsigned int informationBits,
                                 wchar_t* outBuf, unsigned int outCap, unsigned int* outLen,
                                 wchar_t* errBuf, unsigned int errCap)
    {
        if (outLen) *outLen = 0;
        if (!path) return OCID_ERR_ARGUMENT;
        try
        {
            std::wstring sddl, err;
            if (!ReadPathSddl(W(path), informationBits, sddl, err))
            {
                SetErr(errBuf, errCap, err);
                return OCID_ERR_BAD_VALUE;
            }
            return CopyOutStr(sddl, outBuf, outCap, outLen);
        }
        catch (...)
        {
            SetErr(errBuf, errCap, L"回读路径安全描述符时发生未预期异常");
            return OCID_ERR_INTERNAL;
        }
    }

    /// 路径是否存在（都是"路径不存在"这种失败要用的判断，放原生侧统一）。
    int __cdecl OcidPathExists(const wchar_t* path)
    {
        if (!path) return 0;
        return PathExists(W(path)) ? 1 : 0;
    }

} // extern "C"
