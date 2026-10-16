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
// 规范字符串形式（与 Windows 一致，务必照抄，否则和镜像里已有的键/目录对不上）：
//     <name>~<version>~<architecture>~<publicKeyToken>~<language>~<versionScope>
//   · 无该属性时该段留空，但分隔符要保留
//   · 第 6 段 versionScope：0=neutral，1=nonSxS，2=SxS
//   例：Microsoft-Windows-Shell32~6.2.8061.0~arm~31bf3856ad364e35~zh-CN~1
//
// 另一个必须照抄的点：**版本可被省略**（versionless）。CBS 用它的地方很多
// （`GetVersionlessStringId`）—— 例如"按名字+架构找到这个组件的所有版本"。

#pragma once

#include <cstdint>
#include <map>
#include <string>
#include <vector>

namespace ocid {

    enum class VersionScope : int {
        Neutral = 0,
        NonSxS  = 1,
        SxS     = 2,
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

        /// 是否有版本段。真实实现区分 versionless，很多查找都靠它。
        bool HasVersion() const { return !Version().empty(); }

        // ── 规范字符串形式 ──────────────────────────────────────────────────
        //
        // `includeVersion=false` 即真实实现里的 "versionless string id"。
        std::wstring ToString(bool includeVersion = true) const;
        static bool  TryParse(const std::wstring& s, AssemblyIdentity& out);

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
