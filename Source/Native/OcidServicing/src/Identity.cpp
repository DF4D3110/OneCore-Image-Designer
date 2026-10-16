// OCID - 装配标识实现

#include "ocid/Identity.h"

#include <algorithm>
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
        std::wstring s = Attr(L"versionscope");
        if (s == L"1") return VersionScope::NonSxS;
        if (s == L"2") return VersionScope::SxS;
        return VersionScope::Neutral;
    }

    void AssemblyIdentity::SetScope(VersionScope s)
    {
        switch (s) {
            case VersionScope::NonSxS: SetAttribute(L"versionscope", L"1"); break;
            case VersionScope::SxS:    SetAttribute(L"versionscope", L"2"); break;
            default:                   RemoveAttribute(L"versionscope");   break;
        }
    }

    // ── 规范字符串形式 ──────────────────────────────────────────────────────

    //
    // 六个位置段，用 `~` 连接：
    //     name ~ version ~ processorArchitecture ~ publicKeyToken ~ language ~ versionScope
    //
    // 段为空时留空（分隔符仍要在），例如中立语言：
    //     Foo~1.0.0.0~x86~31bf3856ad364e35~~1
    //
    // versionScope 只写 1/2（neutral 留空）—— 这与镜像里目录名/清单名的实际写法一致。
    //
    std::wstring AssemblyIdentity::ToString(bool includeVersion) const
    {
        std::wstring scope;
        switch (Scope()) {
            case VersionScope::NonSxS: scope = L"1"; break;
            case VersionScope::SxS:    scope = L"2"; break;
            default:                   scope.clear(); break;
        }

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
        s += L"~";
        s += scope;
        return s;
    }

    bool AssemblyIdentity::TryParse(const std::wstring& s, AssemblyIdentity& out)
    {
        // 至少要有 6 段才是完整形式；但真实世界里有只给部分的（如 "Foo~1.0.0.0"），
        // 所以少于 6 段时按顺序填已知的前缀，剩下的留空，而不是直接失败。
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
            // 只有名字也算合法（moniker 形式）
            if (!Trim(seg[0]).empty()) { out.SetName(Trim(seg[0])); return true; }
            return false;
        }

        out.SetName(seg[0]);
        if (seg.size() > 1) out.SetVersion(seg[1]);
        if (seg.size() > 2) out.SetArchitecture(seg[2]);
        if (seg.size() > 3) out.SetPublicKeyToken(seg[3]);
        if (seg.size() > 4) out.SetLanguage(seg[4]);
        if (seg.size() > 5) {
            if (seg[5] == L"1") out.SetScope(VersionScope::NonSxS);
            else if (seg[5] == L"2") out.SetScope(VersionScope::SxS);
        }
        return true;
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
