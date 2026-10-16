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
#include "ocid/RegistryValue.h"
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

} // extern "C"
