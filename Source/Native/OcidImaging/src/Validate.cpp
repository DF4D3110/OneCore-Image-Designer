// OCID Imaging / Common —— XSD 校验（MSXML6，IDispatch 晚绑定）
//
// 【为什么不自己写规则】OEMInput / FeatureManifest 的约束是一条条 XSD 写出来的
// （必填项、枚举、子树顺序、版本间不断收紧的细节），手写既对不齐也会随代际漂移。
// 仓库 `Schemas\OEMInput.V*.xsd` 已经是从真 featureapi.dll 归组抽出的 XSD，直接复用。
//
// 【为什么用 IDispatch 晚绑定，而不是 #import <msxml6.dll> 或 <msxml6.h>】
// 1) Common.h 会拉进 <windows.h>，而 windows.h 又拉进 <msxml.h>（MSXML2 的 COM 接口）；
//    #import 生成的 msxml6.tlh 里同名接口（IXMLDOMDocument 等）与之冲突，报 C2011。
// 2) SDK 里确有 msxml6.h，但它与 msxml.h 同样会双份定义 IXMLDOM* 接口。
// 3) 晚绑定只依赖 IDispatch，一个头文件都不用，也不怕 MSXML 版本差异。
// 代价是每次调用都要按名字查 DISPID —— 校验只做几次，完全无所谓。
//
// 【为什么 XSD 走 loadXML，而不是把文件路径交给 schemaCache.add】
// 实测 schemaCache.add(ns, "C:\...\x.xsd") 在本进程里恒返回 0x80070005（E_ACCESSDENIED）；
// 同一份文件、同一个命名空间在 PowerShell 里 add 却成功 —— 说明不是路径/权限问题，
// 而是 MSXML "自己按名字打开 schema 文件" 那条路径的行为差异。
// 因此这里改成：本进程自己读 XSD 文本 → loadXML 进一个 DOMDocument60
// → 再把该文档对象交给 add（VARIANT 里放 VT_DISPATCH），绕开 MSXML 取文件那一环。
//
// 校验语义：DOMDocument60 + XMLSchemaCache60，validateOnParse=true。
// load() 返回 false 即为"格式错或校验失败"，此时 parseError 里有 reason/line/linepos。
#include "ocid/imaging/Common.h"

#include <objbase.h>
#include <oleauto.h>

#include <vector>

namespace ocid::imaging
{
namespace
{

// ── 极小的 IDispatch 辅助 ─────────────────────────────────────────────
class Dispatch
{
public:
    Dispatch() = default;
    Dispatch(const Dispatch&) = delete;
    Dispatch& operator=(const Dispatch&) = delete;
    ~Dispatch() { Reset(); }

    void Reset()
    {
        if (object_ != nullptr)
        {
            object_->Release();
            object_ = nullptr;
        }
    }

    bool Create(const wchar_t* progId)
    {
        CLSID clsid{};
        if (FAILED(CLSIDFromProgID(progId, &clsid)))
        {
            return false;
        }
        return SUCCEEDED(CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, IID_IDispatch,
                                          reinterpret_cast<void**>(&object_)));
    }

    IDispatch* get() const { return object_; }
    bool valid() const { return object_ != nullptr; }

private:
    IDispatch* object_ = nullptr;
};

// 取属性 / 调方法；flags 用 DISPATCH_PROPERTYGET / DISPATCH_METHOD / DISPATCH_PROPERTYPUT*。
// 参数按 C 调用顺序给，内部反转成 DISPPARAMS 的逆序（这是 IDispatch 的约定）。
HRESULT Invoke(IDispatch* object, const wchar_t* name, WORD flags, VARIANT* args, unsigned count,
               VARIANT* result)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }
    LPOLESTR nameCopy = const_cast<LPOLESTR>(name);
    DISPID id = 0;
    HRESULT hr = object->GetIDsOfNames(IID_NULL, &nameCopy, 1, LOCALE_USER_DEFAULT, &id);
    if (FAILED(hr))
    {
        return hr;
    }

    std::vector<VARIANT> reversed;
    reversed.reserve(count);
    for (unsigned i = 0; i < count; ++i)
    {
        reversed.push_back(args[count - 1 - i]);
    }

    DISPID putId = DISPID_PROPERTYPUT;
    DISPPARAMS params{};
    params.rgvarg = reversed.empty() ? nullptr : reversed.data();
    params.cArgs = count;
    if ((flags & DISPATCH_PROPERTYPUT) != 0)
    {
        params.rgdispidNamedArgs = &putId;
        params.cNamedArgs = 1;
    }

    return object->Invoke(id, IID_NULL, LOCALE_USER_DEFAULT, flags, &params, result, nullptr, nullptr);
}

HRESULT Invoke1(IDispatch* object, const wchar_t* name, WORD flags, VARIANT* arg, VARIANT* result)
{
    return Invoke(object, name, flags, arg, arg == nullptr ? 0u : 1u, result);
}

std::string FormatHresult(HRESULT hr, const char* what)
{
    char buffer[160] = {};
    _snprintf_s(buffer, sizeof(buffer), _TRUNCATE, "%s 失败（HRESULT=0x%08lX）", what,
                static_cast<unsigned long>(hr));
    return buffer;
}

// ── 读文本文件（UTF-8 或 UTF-16LE，允许带 BOM）──────────────────────────
bool ReadTextWide(const std::wstring& path, std::wstring& text, std::string& error)
{
    std::vector<std::uint8_t> bytes;
    if (!ReadFileBytes(path, bytes, error) || bytes.empty())
    {
        if (error.empty())
        {
            error = "文件为空：" + WideToUtf8(path);
        }
        return false;
    }

    if (bytes.size() >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
    {
        const std::size_t count = (bytes.size() - 2) / sizeof(wchar_t);
        text.assign(reinterpret_cast<const wchar_t*>(bytes.data() + 2), count);
        return true;
    }

    std::size_t offset = 0;
    if (bytes.size() >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
    {
        offset = 3;
    }
    const std::string utf8(reinterpret_cast<const char*>(bytes.data() + offset), bytes.size() - offset);
    text = Utf8ToWide(utf8);
    return true;
}

// 从 XSD 文本里取 targetNamespace（schemaCache.add 的第一个参数）。
// XSD 属性值里不会出现转义引号，朴素扫描足够。
std::wstring FindTargetNamespace(const std::wstring& text)
{
    const std::wstring marker = L"targetNamespace=\"";
    const std::size_t at = text.find(marker);
    if (at == std::wstring::npos)
    {
        return std::wstring();
    }
    const std::size_t begin = at + marker.size();
    const std::size_t end = text.find(L'"', begin);
    if (end == std::wstring::npos)
    {
        return std::wstring();
    }
    return text.substr(begin, end - begin);
}

// 把 XSD 文本塞进一个 DOMDocument60（不校验、不解析外部实体）。
bool LoadXsdDocument(const std::wstring& path, Dispatch& schema, std::string& error)
{
    std::wstring text;
    if (!ReadTextWide(path, text, error))
    {
        return false;
    }
    if (!schema.Create(L"Msxml2.DOMDocument.6.0"))
    {
        error = "创建 Msxml2.DOMDocument.6.0 失败（系统缺 MSXML6？）";
        return false;
    }

    const wchar_t* const booleanProps[] = {L"async", L"validateOnParse", L"resolveExternals",
                                           L"preserveWhiteSpace"};
    for (int i = 0; i < 4; ++i)
    {
        VARIANT args[1];
        VariantInit(&args[0]);
        args[0].vt = VT_BOOL;
        args[0].boolVal = VARIANT_FALSE;
        VARIANT result;
        VariantInit(&result);
        Invoke1(schema.get(), booleanProps[i], DISPATCH_PROPERTYPUT, args, &result);
        VariantClear(&result);
        VariantClear(&args[0]);
    }

    bool loaded = false;
    VARIANT args[1];
    VariantInit(&args[0]);
    args[0].vt = VT_BSTR;
    args[0].bstrVal = SysAllocString(text.c_str());
    VARIANT result;
    VariantInit(&result);
    const HRESULT hr = Invoke1(schema.get(), L"loadXML", DISPATCH_METHOD, args, &result);
    if (SUCCEEDED(hr) && result.vt == VT_BOOL)
    {
        loaded = result.boolVal != VARIANT_FALSE;
    }
    VariantClear(&result);
    VariantClear(&args[0]);

    if (!loaded)
    {
        error = "XSD 解析失败：" + WideToUtf8(path);
        return false;
    }
    return true;
}

// 从磁盘路径直接 load 一个 XSD（给 xsd:include 用：include 的 schemaLocation 要能
// 相对这个文档的 URL 解析，所以必须走 load(path) 而不是 loadXML(字符串)）。
bool LoadXsdFromPath(const std::wstring& path, Dispatch& schema, std::string& error)
{
    if (!schema.Create(L"Msxml2.DOMDocument.6.0"))
    {
        error = "创建 Msxml2.DOMDocument.6.0 失败（系统缺 MSXML6？）";
        return false;
    }
    const wchar_t* const booleanProps[] = {L"async", L"validateOnParse", L"resolveExternals",
                                           L"preserveWhiteSpace"};
    for (int i = 0; i < 4; ++i)
    {
        VARIANT args[1];
        VariantInit(&args[0]);
        args[0].vt = VT_BOOL;
        args[0].boolVal = VARIANT_FALSE;
        VARIANT result;
        VariantInit(&result);
        Invoke1(schema.get(), booleanProps[i], DISPATCH_PROPERTYPUT, args, &result);
        VariantClear(&result);
        VariantClear(&args[0]);
    }

    bool loaded = false;
    VARIANT args[1];
    VariantInit(&args[0]);
    args[0].vt = VT_BSTR;
    args[0].bstrVal = SysAllocString(path.c_str());
    VARIANT result;
    VariantInit(&result);
    const HRESULT hr = Invoke1(schema.get(), L"load", DISPATCH_METHOD, args, &result);
    if (SUCCEEDED(hr) && result.vt == VT_BOOL)
    {
        loaded = result.boolVal != VARIANT_FALSE;
    }
    VariantClear(&result);
    VariantClear(&args[0]);
    if (!loaded)
    {
        error = "XSD 载入失败：" + WideToUtf8(path);
        return false;
    }
    return true;
}

bool GetErrorText(IDispatch* parseError, const wchar_t* name, std::string& text)
{
    VARIANT result;
    VariantInit(&result);
    if (FAILED(Invoke1(parseError, name, DISPATCH_PROPERTYGET, nullptr, &result)))
    {
        VariantClear(&result);
        return false;
    }
    bool ok = false;
    if (result.vt == VT_BSTR && result.bstrVal != nullptr)
    {
        text = WideToUtf8(result.bstrVal);
        ok = true;
    }
    VariantClear(&result);
    return ok;
}

bool GetErrorNumber(IDispatch* parseError, const wchar_t* name, long& value)
{
    VARIANT result;
    VariantInit(&result);
    if (FAILED(Invoke1(parseError, name, DISPATCH_PROPERTYGET, nullptr, &result)))
    {
        VariantClear(&result);
        return false;
    }
    bool ok = false;
    if (result.vt == VT_I4 || result.vt == VT_I2)
    {
        value = result.lVal;
        ok = true;
    }
    VariantClear(&result);
    return ok;
}

class ComScope
{
public:
    ComScope()
    {
        // 已初始化时返回 RPC_E_CHANGED_MODE / S_FALSE，都算可用。
        hr_ = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        usable_ = SUCCEEDED(hr_) || hr_ == RPC_E_CHANGED_MODE;
    }
    ~ComScope()
    {
        if (SUCCEEDED(hr_))
        {
            CoUninitialize();
        }
    }
    bool usable() const { return usable_; }

private:
    HRESULT hr_ = E_FAIL;
    bool usable_ = false;
};

} // namespace

bool ValidateXmlAgainstSchema(const std::wstring& xmlPath, const std::vector<std::wstring>& schemaPaths,
                              std::string& error)
{
    error.clear();
    if (xmlPath.empty())
    {
        error = "没有给出要校验的 XML 路径";
        return false;
    }
    if (schemaPaths.empty())
    {
        error = "没有给出 XSD 路径";
        return false;
    }

    ComScope com;
    if (!com.usable())
    {
        error = "COM 初始化失败，无法调用 MSXML6";
        return false;
    }

    Dispatch schemaCache;
    if (!schemaCache.Create(L"Msxml2.XMLSchemaCache.6.0"))
    {
        error = "创建 Msxml2.XMLSchemaCache.6.0 失败（系统缺 MSXML6？）";
        return false;
    }

    // 一个 schema 集可能被拆成多个同命名空间的 XSD（ADK 的 FeatureManifest.xsd 就引用了
    // 本来在 Appx.FMAddOnAppX.xsd 里的 AppXType）。MSXML 的 schemaCache.add 以命名空间
    // 为键、同一命名空间只能 add 一次，所以多文件时生成一份 xsd:include 包装文档
    // （放在第一个 XSD 同目录，include 的相对路径才能解析），只 add 这一份。
    std::vector<std::wstring> loadPaths = schemaPaths;
    std::wstring wrapperPath;
    std::wstring wrapperNamespace;
    if (schemaPaths.size() > 1)
    {
        std::wstring firstText;
        std::string ignored;
        if (!ReadTextWide(schemaPaths[0], firstText, ignored))
        {
            error = "读取 XSD 失败：" + WideToUtf8(schemaPaths[0]);
            return false;
        }
        wrapperNamespace = FindTargetNamespace(firstText);
        if (wrapperNamespace.empty())
        {
            error = "XSD 里没有 targetNamespace：" + WideToUtf8(schemaPaths[0]);
            return false;
        }
        // MSXML 的 schemaCache 不处理 xsd:include（实测把 include 包装 add 进去后，
        // 校验仍报"元素未在架构中声明"），所以这里按 include 的语义做**文本级拼接**：
        // 把每个 XSD 根标签的内部内容接到一个根标签下面。三个文件根标签属性完全一致
        // （targetNamespace/elementFormDefault/xmlns 前缀 xs 都相同），拼接后仍然合法。
        std::wstring wrapper;
        std::wstring closeName = L"</xs:schema>";
        bool first = true;
        for (const std::wstring& p : schemaPaths)
        {
            std::wstring text;
            std::string ignored;
            if (!ReadTextWide(p, text, ignored))
            {
                error = "读取 XSD 失败：" + WideToUtf8(p);
                return false;
            }
            std::size_t start = text.find(L"<xs:schema");
            closeName = L"</xs:schema>";
            if (start == std::wstring::npos)
            {
                start = text.find(L"<xsd:schema");
                closeName = L"</xsd:schema>";
            }
            if (start == std::wstring::npos)
            {
                error = "XSD 根元素不是 xs:schema：" + WideToUtf8(p);
                return false;
            }
            const std::size_t openEnd = text.find(L'>', start);
            const std::size_t closeStart = text.rfind(closeName);
            if (openEnd == std::wstring::npos || closeStart == std::wstring::npos || closeStart <= openEnd)
            {
                error = "XSD 根标签不完整：" + WideToUtf8(p);
                return false;
            }
            if (first)
            {
                wrapper = text.substr(start, openEnd - start + 1);
                first = false;
            }
            wrapper += L"\n";
            wrapper += text.substr(openEnd + 1, closeStart - openEnd - 1);
        }
        wrapper += L"\n";
        wrapper += closeName;
        const std::size_t slash = schemaPaths[0].find_last_of(L"\\/");
        const std::wstring dir =
            (slash == std::wstring::npos) ? std::wstring() : schemaPaths[0].substr(0, slash + 1);
        wrapperPath = dir + L"__ocid_schema_wrapper.xsd";
        if (!WriteFileText(wrapperPath, WideToUtf8(wrapper), error))
        {
            error = "写 schema 包装文档失败：" + WideToUtf8(wrapperPath) + "（" + error + "）";
            return false;
        }
        loadPaths.assign(1, wrapperPath);
    }

    for (const std::wstring& schemaPath : loadPaths)
    {
        Dispatch schemaDoc;
        std::wstring nameSpace = wrapperNamespace;
        if (nameSpace.empty())
        {
            if (!LoadXsdDocument(schemaPath, schemaDoc, error))
            {
                if (!wrapperPath.empty()) ::DeleteFileW(wrapperPath.c_str());
                return false;
            }
            std::wstring text;
            std::string ignored;
            if (!ReadTextWide(schemaPath, text, ignored))
            {
                error = "读取 XSD 失败：" + WideToUtf8(schemaPath);
                if (!wrapperPath.empty()) ::DeleteFileW(wrapperPath.c_str());
                return false;
            }
            nameSpace = FindTargetNamespace(text);
        }
        else if (!LoadXsdFromPath(schemaPath, schemaDoc, error))
        {
            ::DeleteFileW(wrapperPath.c_str());
            return false;
        }

        // add(namespaceURI, VARIANT)：第二参数放 VT_DISPATCH = "已经解析好的 schema 对象"。
        VARIANT args[2];
        VariantInit(&args[0]);
        VariantInit(&args[1]);
        args[0].vt = VT_BSTR;
        args[0].bstrVal = SysAllocString(nameSpace.c_str());
        args[1].vt = VT_DISPATCH;
        args[1].pdispVal = schemaDoc.get();
        VARIANT result;
        VariantInit(&result);
        const HRESULT hr = Invoke(schemaCache.get(), L"add", DISPATCH_METHOD, args, 2u, &result);
        VariantClear(&result);
        VariantClear(&args[0]);
        // args[1] 是"借来"的接口指针（schemaDoc 仍持有它），这里绝不能 VariantClear，
        // 否则 Release 掉别人的引用 —— 实测会直接 0xC0000005。
        args[1].pdispVal = nullptr;
        if (FAILED(hr))
        {
            error = FormatHresult(hr, "XMLSchemaCache.add");
            error += "：" + WideToUtf8(schemaPath);
            error += "（ns=[" + WideToUtf8(nameSpace) + "]）";
            if (!wrapperPath.empty()) ::DeleteFileW(wrapperPath.c_str());
            return false;
        }
    }
    if (!wrapperPath.empty())
    {
        ::DeleteFileW(wrapperPath.c_str());
    }

    Dispatch document;
    if (!document.Create(L"Msxml2.DOMDocument.6.0"))
    {
        error = "创建 Msxml2.DOMDocument.6.0 失败（系统缺 MSXML6？）";
        return false;
    }

    {
        VARIANT args[1];
        VariantInit(&args[0]);
        args[0].vt = VT_BOOL;
        args[0].boolVal = VARIANT_FALSE;
        VARIANT result;
        VariantInit(&result);
        Invoke1(document.get(), L"async", DISPATCH_PROPERTYPUT, args, &result);
        VariantClear(&result);
        VariantClear(&args[0]);
    }
    {
        VARIANT args[1];
        VariantInit(&args[0]);
        args[0].vt = VT_BOOL;
        args[0].boolVal = VARIANT_TRUE;
        VARIANT result;
        VariantInit(&result);
        Invoke1(document.get(), L"validateOnParse", DISPATCH_PROPERTYPUT, args, &result);
        VariantClear(&result);
        VariantClear(&args[0]);
    }
    {
        VARIANT args[1];
        VariantInit(&args[0]);
        args[0].vt = VT_DISPATCH;
        args[0].pdispVal = schemaCache.get();
        VARIANT result;
        VariantInit(&result);
        Invoke1(document.get(), L"schemas", DISPATCH_PROPERTYPUTREF, args, &result);
        VariantClear(&result);
        args[0].pdispVal = nullptr; // 同上：借用，不能替对方 Release
    }

    bool loaded = false;
    HRESULT loadHr = S_OK;
    {
        VARIANT args[1];
        VariantInit(&args[0]);
        args[0].vt = VT_BSTR;
        args[0].bstrVal = SysAllocString(xmlPath.c_str());
        VARIANT result;
        VariantInit(&result);
        loadHr = Invoke1(document.get(), L"load", DISPATCH_METHOD, args, &result);
        if (SUCCEEDED(loadHr) && result.vt == VT_BOOL)
        {
            loaded = result.boolVal != VARIANT_FALSE;
        }
        VariantClear(&result);
        VariantClear(&args[0]);
    }

    if (loaded)
    {
        return true;
    }

    // 取 parseError 的细节；取不到就退回 HRESULT。
    IDispatch* rawError = nullptr;
    {
        VARIANT result;
        VariantInit(&result);
        if (SUCCEEDED(Invoke1(document.get(), L"parseError", DISPATCH_PROPERTYGET, nullptr, &result)) &&
            result.vt == VT_DISPATCH && result.pdispVal != nullptr)
        {
            rawError = result.pdispVal; // 接管这一次引用
            result.pdispVal = nullptr;
        }
        VariantClear(&result);
    }
    if (rawError != nullptr)
    {
        std::string reason;
        long line = 0;
        long linePos = 0;
        long code = 0;
        GetErrorText(rawError, L"reason", reason);
        GetErrorNumber(rawError, L"line", line);
        GetErrorNumber(rawError, L"linepos", linePos);
        GetErrorNumber(rawError, L"errorCode", code);
        rawError->Release();
        if (!reason.empty())
        {
            char where[128] = {};
            _snprintf_s(where, sizeof(where), _TRUNCATE, "（行 %ld，列 %ld，errorCode=0x%08lX）", line, linePos,
                        static_cast<unsigned long>(code));
            error = reason + where;
            return false;
        }
    }

    if (FAILED(loadHr))
    {
        error = FormatHresult(loadHr, "DOMDocument.load");
    }
    else
    {
        error = "XML 未通过 XSD 校验";
    }
    error += "：" + WideToUtf8(xmlPath);
    return false;
}

} // namespace ocid::imaging
