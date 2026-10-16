// OCID - 装配标识（Assembly Identity）
//
// 这是 OCID 自带服务栈的地基：CBS/CSI/CMI/SMI 之间传递的每个对象都要先能表明"我是谁"。
//
// 模型取自 8061 服务栈的带符号反编译（`CbsCore.dll` 里的真实类 `CCbsIdentity`）：
//   LoadFromStringId / SaveAsStringId / GetStringId / GetVersionlessStringId
//   LoadFromAttributes / GetAttribute / GetVersion
//   IsEqual / IsFastEqual / IsMoniker / IsNull / IsRelated / Clone / Clear
// ⇒ 它是一个**通用属性包**，并带一个**规范字符串形式**。这正是清单里
//   `<assemblyIdentity name=… version=… processorArchitecture=… publicKeyToken=… …/>` 的抽象。
//
// 规范字符串形式（**照抄真实实现，别按直觉猜**）
//
// 依据 8061 带符号反编译 `CbsCore.dll` 的 `CCbsIdentity::SaveAsStringId`（@0x100A4303）：
//     SczAllocFormatted(&v7, L"%s%c%s%c%s%c%s%c%s", lpCriticalSection[1].SpinCount)
// ⇒ **5 个 %s + 4 个 %c ⇒ 规范字符串是【5 段】**：
//     <name>~<version>~<processorArchitecture>~<publicKeyToken>~<language>
//
// ⚠ `versionScope` **不在**这个字符串里（我最初按 6 段写，是错的）。
//
// `versionScope` 在清单里是**字符串** `nonSxS` / `SxS`，而且有一条真实规则
// （见 `?GetKeyFormFromDefinitionIdentityLessVersion@@YGJPAUIDefinitionIdentity@@PAPAG@Z` @0x10095203）：
//     if (versionScope 与 L"nonSxS" 不区分大小写相等)  version := L"0.0.0.0"
//     else                                            version := 解析后的真实版本
// ⇒ 即 **nonSxS 的版本一律按 `0.0.0.0` 处理**。
//
// 另有一个**完全不同的**格式，"组件名"（目录名/清单名用的）：
//     <arch>_<name>_<publicKeyToken>_<version>_<culture>_<hash>
//   · 与标识字符串**不是一回事**，别混用
//   · 其中 culture：`neutral` → **`none`**
//   实测例（镜像里的真实目录）：
//     arm_acpi.inf_31bf3856ad364e35_6.2.8061.0_none_a991cf7a419448c9
//   对应的 identity 是：
//     name="acpi.inf" version="6.2.8061.0" processorArchitecture="arm"
//     language="neutral" publicKeyToken="31bf3856ad364e35" versionScope="nonSxS"
//
// 第三个必须照抄的点：**版本可被省略**（versionless）。CBS 用它的地方很多
// （`GetVersionlessStringId`）—— 例如"按名字+架构找到这个组件的所有版本"。

#pragma once

#include <cstdint>
#include <map>
#include <string>
#include <vector>

namespace ocid {

    /// 版本范围。
    ///
    /// ⚠ 在清单/标识里它是**字符串** `"nonSxS"` / `"SxS"`，不是数字。
    /// 早期 Windows 的 SxS 世界用「中立/非 SxS/ SxS」三分，这里保留三态便于判断，
    /// 但**存进属性的永远是字符串**（`SetScope` 负责转换）。
    enum class VersionScope : int {
        Neutral = 0,   // 属性缺失
        NonSxS  = 1,   // 字符串 "nonSxS"
        SxS     = 2,   // 字符串 "SxS"
    };

    /// 标识里的一个具名属性（name / version / processorArchitecture / publicKeyToken / language / …）。
    ///
    /// 之所以做成字符串到字符串的映射而**不是**一堆固定字段：清单里出现的属性并不固定
    /// （还有 type、culture、buildType、owner、appName…），而且大小写与别名都可能变。
    /// 真实实现也是这么做的（`GetAttribute` / `LoadFromAttributes`）。
    class AssemblyIdentity
    {
    public:
        AssemblyIdentity() = default;

        // ── 属性访问 ────────────────────────────────────────────────────────
        //
        // 属性名**大小写不敏感**（Windows 的这些标识就是这样比较的）。
        // 内部统一以规范小写键存储。
        void        SetAttribute(const std::wstring& name, const std::wstring& value);
        bool        GetAttribute(const std::wstring& name, std::wstring& out) const;
        bool        HasAttribute(const std::wstring& name) const;
        void        RemoveAttribute(const std::wstring& name);
        /// 返回所有属性（键已规范化为小写）。
        const std::map<std::wstring, std::wstring>& Attributes() const { return attrs_; }

        // ── 常用属性的便捷访问 ──────────────────────────────────────────────
        std::wstring Name()                 const { return Attr(L"name"); }
        std::wstring Version()              const { return Attr(L"version"); }
        std::wstring Architecture()         const { return Attr(L"processorarchitecture"); }
        std::wstring PublicKeyToken()       const { return Attr(L"publickeytoken"); }
        std::wstring Language()             const { return Attr(L"language"); }
        std::wstring Type()                 const { return Attr(L"type"); }

        void SetName(const std::wstring& v)           { SetAttribute(L"name", v); }
        void SetVersion(const std::wstring& v)        { SetAttribute(L"version", v); }
        void SetArchitecture(const std::wstring& v)   { SetAttribute(L"processorarchitecture", v); }
        void SetPublicKeyToken(const std::wstring& v) { SetAttribute(L"publickeytoken", v); }
        void SetLanguage(const std::wstring& v)       { SetAttribute(L"language", v); }

        VersionScope Scope() const;
        void         SetScope(VersionScope s);
        /// 直接的字符串形式（`"nonSxS"` / `"SxS"` / 空）。
        std::wstring ScopeString() const { return Attr(L"versionscope"); }

        /// 是否有版本段。真实实现区分 versionless，很多查找都靠它。
        bool HasVersion() const { return !Version().empty(); }

        /// **有效版本**：`nonSxS` 的一律按 `0.0.0.0` 处理（真实实现的规则，见文件头注释）。
        std::wstring EffectiveVersion() const;

        // ── 规范字符串形式 ──────────────────────────────────────────────────
        //
        // **5 段**：name~version~arch~publicKeyToken~language
        // versionScope 不入串。
        //
        // `includeVersion=false` 即真实实现里的 "versionless string id"：
        // 版本段留空，但**分隔符保留**（不能靠丢段来实现）。
        std::wstring ToString(bool includeVersion = true) const;
        static bool  TryParse(const std::wstring& s, AssemblyIdentity& out);

        /// "KeyForm"：真实实现里 `GetKeyFormFromDefinitionIdentityLessVersion` 用的形式，
        /// 即**去掉版本**的规范字符串，且版本按 `EffectiveVersion()`（nonSxS ⇒ 0.0.0.0）语义处理。
        std::wstring KeyFormLessVersion() const;

        // ── "组件名"格式（目录名/清单名用，与上面的标识字符串【不是一回事】） ──
        //
        //     <arch>_<name>_<publicKeyToken>_<version>_<culture>_<hash>
        //   · culture：`neutral` → `none`
        //   · hash 是清单内容的哈希，本函数无法算（需要清单字节），
        //     所以只生成前 5 段；算完 hash 由调用方接上。
        std::wstring ComponentNamePrefix() const;

        /// culture 段的取值：neutral → none，其它原样。
        std::wstring CultureSegment() const;

        // ── 比较 ────────────────────────────────────────────────────────────
        //
        // IsEqual      ：按**全部**属性比（真实实现的语义）
        // IsFastEqual  ：先比规范字符串，不同再逐属性 —— 结果等价，快路径命中率高
        // IsVersionlessEqual：忽略版本
        bool IsEqual(const AssemblyIdentity& o) const;
        bool IsFastEqual(const AssemblyIdentity& o) const;
        bool IsVersionlessEqual(const AssemblyIdentity& o) const;

        /// "是不是一个 moniker"（占位/通配形式，如只给了名字）。真实实现有 `IsMoniker`。
        bool IsMoniker() const { return Name().empty(); }
        bool IsNull() const { return attrs_.empty(); }
        void Clear() { attrs_.clear(); }

        /// 是不是"相关的"标识：同名同架构，但版本/语言不同。
        /// 用于把父子/本地化包关联起来（`IsRelated`）。
        bool IsRelated(const AssemblyIdentity& o) const;

        /// 规范字符串（可直接做 map 的键）。等价于 ToString(true)。
        std::wstring Key() const { return ToString(true); }

    private:
        std::wstring Attr(const std::wstring& lowerKey) const;

        // 键一律小写（见 SetAttribute 的说明）。
        std::map<std::wstring, std::wstring> attrs_;
    };

    /// 标识集合：带去重（按规范字符串）与稳定顺序。
    class IdentitySet
    {
    public:
        /// 返回 false 表示已存在（未插入）。
        bool Add(const AssemblyIdentity& id);
        bool Contains(const AssemblyIdentity& id) const;
        const std::vector<AssemblyIdentity>& Items() const { return items_; }
        size_t Size() const { return items_.size(); }
        void   Clear() { items_.clear(); index_.clear(); }

    private:
        std::vector<AssemblyIdentity> items_;
        std::map<std::wstring, size_t> index_;   // Key() → items_ 下标
    };

} // namespace ocid
