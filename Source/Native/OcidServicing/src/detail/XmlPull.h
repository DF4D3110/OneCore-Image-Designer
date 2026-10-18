// OCID - 内部共享的小型 XML 拉取解析器
//
// 从 Manifest.cpp 里抽出来给多个解析器共用（manifest / WCM schema / 后续其它）。
//
// 为什么自带而不接 MSXML/Expat：清单与 XSD 都是**机器生成的规范 XML**（无 DTD、无实体、
// 无混合内容），问题域很窄；而"少依赖、可排障"本身是 OCID 的目标之一。
//
// 支持：声明 `<?xml?>`、注释、CDATA、自闭合标签、单/双引号属性、命名空间前缀（原样保留）。
// 不支持（真实数据里不出现）：DTD、内部实体、其它处理指令。

#pragma once

#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace ocid {
namespace detail {

    struct XmlAttr
    {
        std::wstring name;
        std::wstring value;
    };

    struct XmlNode
    {
        std::wstring name;                  // 原样保留前缀，如 "xsd:element"
        std::vector<XmlAttr> attrs;
        std::wstring text;                  // 直接文本（不含子元素）
        std::vector<XmlNode> children;
        bool selfClosing = false;

        /// 去掉前缀的本地名（"xsd:element" → "element"）。
        std::wstring LocalName() const
        {
            size_t p = name.find(L':');
            return p == std::wstring::npos ? name : name.substr(p + 1);
        }

        /// 取属性（大小写不敏感）。
        const std::wstring* FindAttr(const std::wstring& n) const
        {
            for (const auto& a : attrs) {
                if (_wcsicmp(a.name.c_str(), n.c_str()) == 0) return &a.value;
            }
            return nullptr;
        }
    };

    struct XmlParser
    {
        const std::wstring& s;
        size_t i = 0;
        std::wstring error;

        explicit XmlParser(const std::wstring& src) : s(src) {}

        bool Eof() const { return i >= s.size(); }
        wchar_t Cur() const { return i < s.size() ? s[i] : L'\0'; }

        void SkipWs()
        {
            while (!Eof() && (Cur() == L' ' || Cur() == L'\t' || Cur() == L'\r' || Cur() == L'\n')) ++i;
        }

        bool StartsWith(const wchar_t* p) const
        {
            size_t n = wcslen(p);
            return s.compare(i, n, p) == 0;
        }

        static bool IsNameStart(wchar_t c)
        {
            return (c >= L'a' && c <= L'z') || (c >= L'A' && c <= L'Z') || c == L'_' || c >= 0x80;
        }

        static bool IsNameChar(wchar_t c)
        {
            return IsNameStart(c) || (c >= L'0' && c <= L'9') || c == L'-' || c == L'.' || c == L':';
        }

        bool ReadName(std::wstring& out)
        {
            if (Eof() || !IsNameStart(Cur())) return false;
            size_t b = i;
            while (!Eof() && IsNameChar(Cur())) ++i;
            out = s.substr(b, i - b);
            return true;
        }

        void SkipCommentOrPi()
        {
            if (StartsWith(L"<!--")) {
                i += 4;
                size_t e = s.find(L"-->", i);
                i = (e == std::wstring::npos) ? s.size() : e + 3;
                return;
            }
            if (StartsWith(L"<?")) {
                i += 2;
                size_t e = s.find(L"?>", i);
                i = (e == std::wstring::npos) ? s.size() : e + 2;
                return;
            }
            if (StartsWith(L"<![")) {
                i += 2;
                size_t e = s.find(L"]]>", i);
                i = (e == std::wstring::npos) ? s.size() : e + 3;
                return;
            }
            size_t e = s.find(L'>', i);
            i = (e == std::wstring::npos) ? s.size() : e + 1;
        }

        /// 转义还原：&lt; &gt; &amp; &quot; &apos; &#NN; &#xNN;
        std::wstring Unescape(const std::wstring& in)
        {
            if (in.find(L'&') == std::wstring::npos) return in;
            std::wstring out;
            out.reserve(in.size());
            for (size_t k = 0; k < in.size(); ++k) {
                if (in[k] != L'&') { out += in[k]; continue; }
                size_t semi = in.find(L';', k);
                if (semi == std::wstring::npos) { out += in[k]; continue; }
                std::wstring ent = in.substr(k + 1, semi - k - 1);
                if      (ent == L"lt")   out += L'<';
                else if (ent == L"gt")   out += L'>';
                else if (ent == L"amp")  out += L'&';
                else if (ent == L"quot") out += L'"';
                else if (ent == L"apos") out += L'\'';
                else if (!ent.empty() && ent[0] == L'#') {
                    long cp = 0;
                    if (ent.size() > 1 && (ent[1] == L'x' || ent[1] == L'X'))
                        cp = wcstol(ent.c_str() + 2, nullptr, 16);
                    else
                        cp = wcstol(ent.c_str() + 1, nullptr, 10);
                    if (cp > 0 && cp <= 0x10FFFF) out += (wchar_t)cp;
                } else {
                    out += L'&'; out += ent; out += L';';
                    continue;
                }
                k = semi;
            }
            return out;
        }

        /// 解析一个元素（Cur() 须在 '<' 上）。
        bool ParseElement(XmlNode& out)
        {
            if (Cur() != L'<') { error = L"内部错误：ParseElement 不在 '<' 上"; return false; }
            ++i;
            if (!ReadName(out.name)) { error = L"元素名缺失"; return false; }

            for (;;) {
                SkipWs();
                if (Eof()) { error = L"标签未闭合"; return false; }
                if (StartsWith(L"/>")) { i += 2; out.selfClosing = true; return true; }
                if (Cur() == L'>') { ++i; break; }
                XmlAttr a;
                if (!ReadName(a.name)) { error = L"属性名非法 @ " + std::to_wstring(i); return false; }
                SkipWs();
                if (Cur() != L'=') { error = L"属性 " + a.name + L" 后缺 '='"; return false; }
                ++i;
                SkipWs();
                wchar_t q = Cur();
                if (q != L'"' && q != L'\'') { error = L"属性 " + a.name + L" 的值未加引号"; return false; }
                ++i;
                size_t b = i;
                while (!Eof() && Cur() != q) ++i;
                if (Eof()) { error = L"属性 " + a.name + L" 的引号未闭合"; return false; }
                a.value = Unescape(s.substr(b, i - b));
                ++i;
                out.attrs.push_back(std::move(a));
            }

            for (;;) {
                if (Eof()) { error = L"元素 " + out.name + L" 未闭合"; return false; }
                if (StartsWith(L"</")) {
                    i += 2;
                    std::wstring close;
                    if (!ReadName(close)) { error = L"结束标签名非法"; return false; }
                    SkipWs();
                    if (Cur() != L'>') { error = L"结束标签 " + close + L" 缺 '>'"; return false; }
                    ++i;
                    if (_wcsicmp(close.c_str(), out.name.c_str()) != 0) {
                        error = L"标签不匹配：<" + out.name + L"> 对 </" + close + L">";
                        return false;
                    }
                    return true;
                }
                if (Cur() == L'<') {
                    if (StartsWith(L"<!--") || StartsWith(L"<?") || StartsWith(L"<![")) {
                        SkipCommentOrPi();
                        continue;
                    }
                    XmlNode child;
                    if (!ParseElement(child)) return false;
                    out.children.push_back(std::move(child));
                    continue;
                }
                size_t b = i;
                while (!Eof() && Cur() != L'<') ++i;
                out.text += Unescape(s.substr(b, i - b));
            }
        }

        /// 解析整篇文档，返回根元素。
        ///
        /// 真机清单里 `<?…?>` 常不止一个：`<?xml …?>` 之后还有
        /// `<?Copyright (c) Microsoft Corporation. All rights reserved.?>`
        /// （如 `x86_microsoft-windows-foundation_…manifest`）。所以开头要**反复**
        /// 跳过处理指令/注释/DOCTYPE，直到真正的根元素。
        bool ParseDocument(XmlNode& root)
        {
            for (;;)
            {
                SkipWs();
                if (StartsWith(L"<!--") || StartsWith(L"<?") || StartsWith(L"<!")) {
                    SkipCommentOrPi();
                    continue;
                }
                break;
            }
            if (Cur() != L'<') { error = L"文档里没有根元素"; return false; }
            return ParseElement(root);
        }
    };

    /// 在子树里按本地名找第一个匹配节点（大小写不敏感），深度上限防跑飞。
    ///
    /// ★ 必须有这个：真实数据里很多重要节点不在顶层，按顶层找会**静默数出 0**（不报错、
    ///   结果为空），比崩溃难发现得多。实测踩过两次：
    ///     <configuration><configurationSchema><xsd:schema>…
    ///     <trustInfo><security><accessControl><securityDescriptorDefinitions>
    inline const XmlNode* FindDescendant(const XmlNode& n, const wchar_t* localName, int depth = 0)
    {
        if (depth > 8) return nullptr;
        for (const auto& c : n.children) {
            if (_wcsicmp(c.LocalName().c_str(), localName) == 0) return &c;
            if (const XmlNode* d = FindDescendant(c, localName, depth + 1)) return d;
        }
        return nullptr;
    }

    inline void CollectByLocalName(const XmlNode& n, const wchar_t* localName,
                                   std::vector<const XmlNode*>& out, int depth = 0)
    {
        if (depth > 8) return;
        for (const auto& c : n.children) {
            if (_wcsicmp(c.LocalName().c_str(), localName) == 0) out.push_back(&c);
            CollectByLocalName(c, localName, out, depth + 1);
        }
    }

    /// XML 转义（属性值/文本通用）。
    ///
    /// ⚠ 必须有这个：解析时 `&quot;` 被**还原**成字面 `"`，
    ///   若重新序列化时原样写回，值里的 `"` 会提前闭合属性 ⇒ 产出的 XML 非法。
    ///   实测就踩在这里：shell32 清单里 `default="&quot;%SystemRoot%\system32\OpenWith.exe&quot; &amp; %1"`
    ///   这类值重新序列化后坏了，错误信息是 `属性名非法 @ 7913`（离真正原因很远，很难直接看出来）。
    inline std::wstring EscapeXml(const std::wstring& in)
    {
        std::wstring out;
        out.reserve(in.size() + 8);
        for (wchar_t c : in) {
            switch (c) {
                case L'&':  out += L"&amp;";  break;
                case L'<':  out += L"&lt;";   break;
                case L'>':  out += L"&gt;";   break;
                case L'"':  out += L"&quot;"; break;
                case L'\'': out += L"&apos;"; break;
                default:    out += c;         break;
            }
        }
        return out;
    }

    /// 把节点重新序列化为 XML 文本（属性值/文本都会正确转义）。
    inline void SerializeNode(const XmlNode& n, std::wstring& out)
    {
        out += L"<";
        out += n.name;
        for (const auto& a : n.attrs) {
            out += L" ";
            out += a.name;
            out += L"=\"";
            out += EscapeXml(a.value);
            out += L"\"";
        }
        if (n.selfClosing) {
            // 保留自闭合形态，但若节点本来有内容就不能自闭合
            if (n.text.empty() && n.children.empty()) { out += L"/>"; return; }
        }
        out += L">";
        out += EscapeXml(n.text);
        for (const auto& c : n.children) SerializeNode(c, out);
        out += L"</";
        out += n.name;
        out += L">";
    }

} // namespace detail
} // namespace ocid
