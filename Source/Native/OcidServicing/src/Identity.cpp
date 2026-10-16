// OCID - 装配标识实现

#include "ocid/Identity.h"

#include <algorithm>
#include <cwchar>
#include <cwctype>

namespace ocid {

    namespace {

        std::wstring ToLower(const std::wstring& s)
        {
            std::wstring r = s;
            for (auto& c : r) c = (wchar_t)std::towlower(c);
            return r;
        }

        std::wstring Trim(const std::wstring& s)
        {
            size_t b = 0, e = s.size();
            while (b < e && std::iswspace(s[b])) ++b;
            while (e > b && std::iswspace(s[e - 1])) --e;
            return s.substr(b, e - b);
        }

        /// 把版本拆成 4 段数字。解析失败返回 false（调用方按"字符串不等"处理）。
        bool ParseVersion4(const std::wstring& v, uint32_t out[4])
        {
            out[0] = out[1] = out[2] = out[3] = 0;
            int idx = 0;
            size_t i = 0;
            while (i < v.size() && idx < 4) {
                uint64_t n = 0;
                size_t start = i;
                while (i < v.size() && v[i] >= L'0' && v[i] <= L'9') {
                    n = n * 10 + (uint64_t)(v[i] - L'0');
                    if (n > 0xFFFFFFFFull) n = 0xFFFFFFFFull;
                    ++i;
                }
                if (i == start) return false;         // 该段没有数字
                out[idx++] = (uint32_t)n;
                if (i < v.size() && v[i] == L'.') ++i;
                else break;
            }
            // 其余段补 0；多出来的段忽略（真实实现也只比 4 段）
            return idx > 0;
        }

    } // namespace

    // ── 属性 ────────────────────────────────────────────────────────────────

    void AssemblyIdentity::SetAttribute(const std::wstring& name, const std::wstring& value)
    {
        std::wstring k = ToLower(Trim(name));
        if (k.empty()) return;
        std::wstring v = Trim(value);
        if (v.empty()) {
            // 空值 = 删除该属性。理由：清单里会出现 `language=""`，
            // 若保留成空值，"有 language 属性和没有"就会不等，与真实实现不符。
            attrs_.erase(k);
            return;
        }
        attrs_[k] = v;
    }

    bool AssemblyIdentity::GetAttribute(const std::wstring& name, std::wstring& out) const
    {
        auto it = attrs_.find(ToLower(Trim(name)));
        if (it == attrs_.end()) return false;
        out = it->second;
        return true;
    }

    bool AssemblyIdentity::HasAttribute(const std::wstring& name) const
    {
        return attrs_.find(ToLower(Trim(name))) != attrs_.end();
    }

    void AssemblyIdentity::RemoveAttribute(const std::wstring& name)
    {
        attrs_.erase(ToLower(Trim(name)));
    }

    std::wstring AssemblyIdentity::Attr(const std::wstring& lowerKey) const
    {
        auto it = attrs_.find(lowerKey);
        return it == attrs_.end() ? std::wstring() : it->second;
    }

    // ── 版本范围 ────────────────────────────────────────────────────────────

    VersionScope AssemblyIdentity::Scope() const
    {
        // 清单里写的是字符串 "nonSxS" / "SxS"（不区分大小写）。
        // 早期真实实现里也有过数字形式，所以两种都认。
        std::wstring s = Attr(L"versionscope");
        if (s.empty()) return VersionScope::Neutral;
        if (s == L"1" || _wcsicmp(s.c_str(), L"nonSxS") == 0) return VersionScope::NonSxS;
        if (s == L"2" || _wcsicmp(s.c_str(), L"SxS") == 0)    return VersionScope::SxS;
        return VersionScope::Neutral;
    }

    void AssemblyIdentity::SetScope(VersionScope s)
    {
        // ⚠ 存**字符串**（与清单一致），不是数字 —— 反编译里是
        //   `__wcsicmp(String1, L"nonSxS")` 这样比的。
        switch (s) {
            case VersionScope::NonSxS: SetAttribute(L"versionscope", L"nonSxS"); break;
            case VersionScope::SxS:    SetAttribute(L"versionscope", L"SxS");    break;
            default:                   RemoveAttribute(L"versionscope");         break;
        }
    }

    std::wstring AssemblyIdentity::EffectiveVersion() const
    {
        // 真实实现（GetKeyFormFromDefinitionIdentityLessVersion @0x10095203）：
        //     if (versionScope == L"nonSxS") version := L"0.0.0.0";
        //     else                           version := 解析 version 属性;
        // ⇒ nonSxS 的版本一律当 0.0.0.0。
        if (Scope() == VersionScope::NonSxS) return L"0.0.0.0";
        return Version();
    }

    // ── 规范字符串形式（5 段） ──────────────────────────────────────────────

    //
    // 依据 `CCbsIdentity::SaveAsStringId` @0x100A4303 的 `L"%s%c%s%c%s%c%s%c%s"`：
    //     5 段、4 个分隔符 —— name~version~arch~publicKeyToken~language
    // **versionScope 不入串。**
    //
    // 分隔符用 `~`（镜像里目录/清单名普遍用 `~` 连接 identity 的这几段）。
    //
    std::wstring AssemblyIdentity::ToString(bool includeVersion) const
    {
        std::wstring s;
        s += Name();
        s += L"~";
        s += includeVersion ? Version() : std::wstring();
        s += L"~";
        s += Architecture();
        s += L"~";
        s += PublicKeyToken();
        s += L"~";
        s += Language();
        return s;
    }

    bool AssemblyIdentity::TryParse(const std::wstring& s, AssemblyIdentity& out)
    {
        out.Clear();

        std::vector<std::wstring> seg;
        size_t start = 0;
        while (true) {
            size_t p = s.find(L'~', start);
            if (p == std::wstring::npos) { seg.push_back(s.substr(start)); break; }
            seg.push_back(s.substr(start, p - start));
            start = p + 1;
        }
        if (seg.size() < 2) {
            if (!Trim(seg[0]).empty()) { out.SetName(Trim(seg[0])); return true; }
            return false;
        }

        // 5 段形式：name~version~arch~token~language
        out.SetName(seg[0]);
        if (seg.size() > 1) out.SetVersion(seg[1]);
        if (seg.size() > 2) out.SetArchitecture(seg[2]);
        if (seg.size() > 3) out.SetPublicKeyToken(seg[3]);
        if (seg.size() > 4) out.SetLanguage(seg[4]);
        return true;
    }

    std::wstring AssemblyIdentity::KeyFormLessVersion() const
    {
        // 语义：去掉版本的规范字符串；版本按 EffectiveVersion 处理 ——
        // 对 nonSxS 来说 EffectiveVersion()=="0.0.0.0"，而"LessVersion"把它也省掉，
        // 所以这里统一输出 versionless 形式。
        return ToString(false);
    }

    // ── "组件名"格式（目录名/清单名用） ────────────────────────────────────

    std::wstring AssemblyIdentity::CultureSegment() const
    {
        // 实测：identity 里 language="neutral"，而目录名里该段是 `none`。
        std::wstring lang = Language();
        if (lang.empty() || _wcsicmp(lang.c_str(), L"neutral") == 0) return L"none";
        return lang;
    }

    std::wstring AssemblyIdentity::ComponentNamePrefix() const
    {
        // <arch>_<name>_<token>_<version>_<culture>
        // 真实例：arm_acpi.inf_31bf3856ad364e35_6.2.8061.0_none
        // ⚠ 注意顺序与标识字符串【不同】：这里 arch 在最前，version 在第 4 段。
        std::wstring s;
        s += Architecture();
        s += L"_";
        s += Name();
        s += L"_";
        s += PublicKeyToken();
        s += L"_";
        s += Version();
        s += L"_";
        s += CultureSegment();
        return s;
    }

    // ── 比较 ────────────────────────────────────────────────────────────────

    bool AssemblyIdentity::IsEqual(const AssemblyIdentity& o) const
    {
        // 属性几乎总是不多（5~8 个），逐项比已经足够快，且语义最直白。
        return attrs_ == o.attrs_;
    }

    bool AssemblyIdentity::IsFastEqual(const AssemblyIdentity& o) const
    {
        // 快路径：规范字符串相同 ⇒ 必然相等（规范字符串是全部属性的函数）。
        // 不同时**不能**直接判不等 —— 属性顺序/别名差异会体现在字符串上，
        // 所以回落到逐属性比较。这正是真实实现 IsFastEqual 的语义。
        if (ToString(true) == o.ToString(true)) return true;
        return IsEqual(o);
    }

    bool AssemblyIdentity::IsVersionlessEqual(const AssemblyIdentity& o) const
    {
        return ToString(false) == o.ToString(false);
    }

    bool AssemblyIdentity::IsRelated(const AssemblyIdentity& o) const
    {
        // "相关" = 同名 + 同架构 + 同公钥标记，但（版本或语言不同的）另一份。
        // CBS 用它把父子包/本地化包关联起来。
        if (Name().empty() || o.Name().empty()) return false;
        if (ToLower(Name()) != ToLower(o.Name())) return false;
        if (ToLower(Architecture()) != ToLower(o.Architecture())) return false;
        if (ToLower(PublicKeyToken()) != ToLower(o.PublicKeyToken())) return false;
        // 完全相等不算"相关"（那叫相等）
        return !IsEqual(o);
    }

    // ── IdentitySet ─────────────────────────────────────────────────────────

    bool IdentitySet::Add(const AssemblyIdentity& id)
    {
        std::wstring k = id.Key();
        if (index_.find(k) != index_.end()) return false;
        index_[k] = items_.size();
        items_.push_back(id);
        return true;
    }

    bool IdentitySet::Contains(const AssemblyIdentity& id) const
    {
        return index_.find(id.Key()) != index_.end();
    }

} // namespace ocid
