// OcidImaging / Common —— 基础层（控制台、UTF-8、日志、文件、XML DOM）
//
// 【为什么单独一个 DLL】imagegen 的故障几乎都出在四类地方：输入 XML 解析、包 CAB 读取、
// 建盘/分区、收尾写元数据。把每一类隔成独立的 .wcos.dll，出错时看"最后加载/最后调用哪个
// DLL"就能定位，不必在一坨 exe 里翻。Common 只放真正公用的部分。
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace ocid::imaging {

#if defined(OCID_COMMON_BUILD)
#define OCID_COMMON_API __declspec(dllexport)
#else
#define OCID_COMMON_API __declspec(dllimport)
#endif

// ── 控制台与编码 ───────────────────────────────────────────────────────────
// 整个工具的输出约定（与 ocid_manifest / ocid_wssm 一致）：
//   数据走 stdout（UTF-8），诊断走 stderr，退出码 0 = 成功、1 = 失败、2 = 用法错误。
OCID_COMMON_API void ConsoleInit();
OCID_COMMON_API std::string WideToUtf8(const std::wstring& text);
OCID_COMMON_API std::wstring Utf8ToWide(const std::string& text);
OCID_COMMON_API void Out(const std::string& utf8);
OCID_COMMON_API void OutLn(const std::string& utf8);
OCID_COMMON_API void Err(const std::string& utf8);

// ── 日志 ───────────────────────────────────────────────────────────────────
// imageapp 的日志是理解它行为的唯一钥匙（Flash.ImageApp.log）。我们写同样形态的
// `{yyyy/M/d H:mm:ss}  message` 行，方便以后两边直接对时间线。
enum class LogLevel { Debug, Info, Warn, Error };

OCID_COMMON_API bool LogOpen(const std::wstring& path, std::string& error);
OCID_COMMON_API void LogClose();
OCID_COMMON_API void LogWrite(LogLevel level, const std::string& utf8);

// ── 文件与路径 ─────────────────────────────────────────────────────────────
OCID_COMMON_API bool FileExists(const std::wstring& path);
OCID_COMMON_API bool DirExists(const std::wstring& path);
OCID_COMMON_API bool ReadFileBytes(const std::wstring& path, std::vector<std::uint8_t>& out, std::string& error);
OCID_COMMON_API bool WriteFileBytes(const std::wstring& path, const void* data, std::size_t size, std::string& error);
OCID_COMMON_API bool WriteFileText(const std::wstring& path, const std::string& utf8, std::string& error);
OCID_COMMON_API std::wstring CombinePath(const std::wstring& left, const std::wstring& right);
OCID_COMMON_API std::wstring ExeDirectory();
OCID_COMMON_API std::wstring FullPath(const std::wstring& path);
OCID_COMMON_API bool EnsureDirectory(const std::wstring& path, std::string& error);

// ── XML DOM ────────────────────────────────────────────────────────────────
// imageapp 的输入全是 XML（OEMInput / FeatureManifest / DeviceLayout / …），且都不大
// （最大 140 KB 级），所以用 DOM 而不是流式解析——后面按名字到处找节点时省事得多。
struct XmlAttr {
    std::string name;   // 原样（可能带前缀，如 xmlns:xsi）
    std::string local;  // 去掉前缀
    std::string value;  // 已解实体
};

struct XmlNode {
    std::string name;   // 原样
    std::string local;  // 去掉前缀
    std::vector<XmlAttr> attrs;
    std::string text;   // 直接文本内容（已解实体，未 trim）
    std::vector<std::unique_ptr<XmlNode>> kids;
    XmlNode* parent = nullptr;
};

class XmlDoc {
public:
    XmlNode* root = nullptr;
    std::vector<std::unique_ptr<XmlNode>> pool;

    const XmlNode* Root() const { return root; }
};

OCID_COMMON_API std::unique_ptr<XmlDoc> XmlParseFile(const std::wstring& path, std::string& error);
OCID_COMMON_API std::unique_ptr<XmlDoc> XmlParseText(const std::string& utf8, std::string& error);

// ── XSD 校验（复用现成 schema，不重写规则） ───────────────────────────────
// 输入 XML 的约束（OEMInput 的必填项/枚举、FeatureManifest 的子树）全都写在 XSD 里，
// 而且随 build 代际漂移。仓库 `Schemas\OEMInput.V*.xsd` 是已归组抽出的真 XSD，
// 这里只把活交给 Windows 自带的 MSXML6 做，报错原样带回（含行列号）。
OCID_COMMON_API bool ValidateXmlAgainstSchema(const std::wstring& xmlPath,
                                              const std::vector<std::wstring>& schemaPaths,
                                              std::string& error);

// 以下辅助函数都按"局部名"比较，忽略命名空间前缀（清单里 asmv2:/cmiv2:/xsi: 前缀满天飞）。
OCID_COMMON_API const XmlNode* Child(const XmlNode* node, const char* local);
OCID_COMMON_API const XmlNode* Next(const XmlNode* node, const char* local);
OCID_COMMON_API std::vector<const XmlNode*> Children(const XmlNode* node, const char* local);
OCID_COMMON_API std::vector<const XmlNode*> ChildrenAny(const XmlNode* node);
OCID_COMMON_API bool HasAttr(const XmlNode* node, const char* local);
OCID_COMMON_API std::string Attr(const XmlNode* node, const char* local);
OCID_COMMON_API std::string AttrOr(const XmlNode* node, const char* local, const char* fallback);
OCID_COMMON_API std::string Trim(const std::string& text);
OCID_COMMON_API std::string Text(const XmlNode* node);      // 直接文本，trim 后
OCID_COMMON_API std::string TextOf(const XmlNode* node, const char* local);  // 子节点文本
OCID_COMMON_API bool EqualsNoCase(const std::string& left, const std::string& right);

// 序列化：输出缩进 XML（用于我们自己生成 PackageList / DeviceLayout 等产物）。
OCID_COMMON_API void XmlEscapeTo(std::string& out, const std::string& text);

OCID_COMMON_API const char* CommonVersion();

} // namespace ocid::imaging
