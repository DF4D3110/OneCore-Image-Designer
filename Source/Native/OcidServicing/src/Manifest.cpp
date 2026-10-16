// OCID - 组件清单解析实现
//
// 自带一个"够用且正确"的 XML 拉取解析器，而不是引入 MSXML/Expat。
// 理由：清单是**机器生成的规范 XML**（无 DTD、无实体、无混合内容），问题域很窄；
// 而 OCID 的目标之一就是"自带、少依赖"。窄问题域 + 自己控制解析，比接一个大库更好排障。
//
// 支持：声明 `<?xml?> `、注释、CDATA、自闭合标签、单/双引号属性、命名空间前缀（原样保留，
//       如 `xsd:element` / `dsig:DigestValue`）。
// 不支持（清单里不出现）：DTD、内部实体、处理指令（除声明）。

#include "ocid/Manifest.h"

#include "detail/XmlPull.h"

#include <cstdlib>
#include <cwchar>

namespace ocid {

    namespace {

        // ── 一个最小的 XML 事件解析器 ────────────────────────────────────────

        // 本文件原先自带一份 XmlAttr/XmlNode/XmlParser。已抽到 src/detail/XmlPull.h
        // 供 manifest / WCM schema 等多个解析器共用，这里直接别名过来。
        using detail::XmlAttr;
        using detail::XmlNode;
        using detail::XmlParser;
        using detail::FindDescendant;
        using detail::CollectByLocalName;
        using detail::SerializeNode;

        // ── 从 XmlNode 构造模型 ─────────────────────────────────────────────

        bool ParseBoolAttr(const XmlNode& n, const wchar_t* name, bool dflt)
        {
            const std::wstring* v = n.FindAttr(name);
            if (!v) return dflt;
            return _wcsicmp(v->c_str(), L"true") == 0 || *v == L"1";
        }

        /// `<registryValue …>`
        bool ParseRegistryValue(const XmlNode& n, RegistryValueSpec& v, std::wstring& err)
        {
            const std::wstring* nm = n.FindAttr(L"name");
            const std::wstring* vt = n.FindAttr(L"valueType");
            const std::wstring* dv = n.FindAttr(L"value");

            if (!vt) { err = L"<registryValue> 缺 valueType"; return false; }
            // name 可以缺失/为空 —— 那表示默认值，是合法且常见的。
            v.name      = nm ? *nm : std::wstring();
            v.valueType = *vt;
            v.value     = dv ? *dv : std::wstring();   // value 可缺（等于空）
            v.owner     = ParseBoolAttr(n, L"owner", true);
            if (const auto* h = n.FindAttr(L"operationHint")) v.operationHint = *h;
            if (const auto* x = n.FindAttr(L"id"))     v.id = *x;
            if (const auto* x = n.FindAttr(L"LinkId")) v.linkId = *x;
            if (const auto* x = n.FindAttr(L"Ext"))    v.ext = *x;
            if (const auto* x = n.FindAttr(L"IEDownlevelValue")) v.ieDownlevelValue = *x;
            return true;
        }

        void ParseRegistryKey(const XmlNode& n, RegistryKeySpec& k)
        {
            if (const auto* kn = n.FindAttr(L"keyName")) k.keyName = *kn;
            k.owner = ParseBoolAttr(n, L"owner", false);

            // ★ 这几个是**键自己的**属性，与键下的值无关。别漏 ——
            //   `perUserVirtualization` 会影响这个键在用户 hive 里的虚拟化行为，
            //   `securityDescriptor`（子元素）决定键的 ACL，两样都在真实清单里出现。
            if (const auto* x = n.FindAttr(L"perUserVirtualization"))
                k.perUserVirtualization = *x;
            if (const auto* x = n.FindAttr(L"mapDesiredAccessToMaxAllowedAccess"))
                k.mapDesiredAccessToMaxAllowedAccess = *x;
            if (const auto* x = n.FindAttr(L"id"))      k.id = *x;
            if (const auto* x = n.FindAttr(L"version")) k.version = *x;
            if (const auto* x = n.FindAttr(L"profile")) k.profile = *x;

            for (const auto& c : n.children) {
                std::wstring ln = c.LocalName();
                if (_wcsicmp(ln.c_str(), L"registryValue") == 0) {
                    RegistryValueSpec v; std::wstring e;
                    if (ParseRegistryValue(c, v, e)) k.values.push_back(std::move(v));
                } else if (_wcsicmp(ln.c_str(), L"registryKey") == 0) {
                    RegistryKeySpec sk; ParseRegistryKey(c, sk);
                    k.subKeys.push_back(std::move(sk));
                } else if (_wcsicmp(ln.c_str(), L"securityDescriptor") == 0) {
                    // 只带出**引用名**与 buildFilter；SDDL 文本在
                    // <securityDescriptorDefinition> 里按名字定义，由调用方解析。
                    if (const auto* v = c.FindAttr(L"name")) k.securityDescriptorName = *v;
                    if (const auto* v = c.FindAttr(L"buildFilter"))
                        k.securityDescriptorBuildFilter = *v;
                }
            }
        }

        /// 收集 `<configurationSchema>` 下所有 `xsd:element` 的 name（用于检索/统计）。
        void CollectXsdElementNames(const XmlNode& n, std::vector<std::wstring>& out)
        {
            if (_wcsicmp(n.LocalName().c_str(), L"element") == 0) {
                if (const auto* nm = n.FindAttr(L"name")) out.push_back(*nm);
            }
            for (const auto& c : n.children) CollectXsdElementNames(c, out);
        }

        /// 在子树里按**本地名**找第一个匹配的节点（大小写不敏感）。
    } // namespace

    ManifestParseResult ParseComponentManifest(const std::wstring& xmlText,
                                               ComponentManifest& out)
    {
        ManifestParseResult r;

        XmlParser p(xmlText);
        XmlNode root;
        if (!p.ParseDocument(root)) {
            r.error = p.error.empty() ? L"XML 解析失败" : p.error;
            return r;
        }

        if (_wcsicmp(root.LocalName().c_str(), L"assembly") != 0) {
            r.error = L"根元素不是 <assembly>（实际：<" + root.name + L">）";
            return r;
        }

        bool sawIdentity = false;

        for (const auto& child : root.children) {
            const std::wstring ln = child.LocalName();

            if (_wcsicmp(ln.c_str(), L"assemblyIdentity") == 0) {
                sawIdentity = true;
                for (const auto& a : child.attrs) out.identity.SetAttribute(a.name, a.value);
            }
            else if (_wcsicmp(ln.c_str(), L"registryKeys") == 0) {
                for (const auto& k : child.children) {
                    if (_wcsicmp(k.LocalName().c_str(), L"registryKey") == 0) {
                        RegistryKeySpec spec; ParseRegistryKey(k, spec);
                        out.registryKeys.push_back(std::move(spec));
                    }
                }
            }
            else if (_wcsicmp(ln.c_str(), L"dependency") == 0) {
                for (const auto& da : child.children) {
                    if (_wcsicmp(da.LocalName().c_str(), L"dependentAssembly") == 0) {
                        for (const auto& idn : da.children) {
                            if (_wcsicmp(idn.LocalName().c_str(), L"assemblyIdentity") == 0) {
                                DependencySpec d; d.kind = L"dependency";
                                for (const auto& a : idn.attrs) d.identity.SetAttribute(a.name, a.value);
                                out.dependencies.push_back(std::move(d));
                            }
                        }
                    }
                }
            }
            else if (_wcsicmp(ln.c_str(), L"supportedComponent") == 0) {
                for (const auto& idn : child.children) {
                    if (_wcsicmp(idn.LocalName().c_str(), L"supportedComponentIdentity") == 0) {
                        DependencySpec d; d.kind = L"supportedComponent";
                        for (const auto& a : idn.attrs) d.identity.SetAttribute(a.name, a.value);
                        out.dependencies.push_back(std::move(d));
                    }
                }
            }
            else if (_wcsicmp(ln.c_str(), L"configuration") == 0) {
                // ★ configurationSchema 嵌在 <configuration> 里（实测），不是顶层。
                if (const XmlNode* cs = FindDescendant(child, L"configurationSchema")) {
                    SerializeNode(*cs, out.configurationSchema.xsdText);
                    CollectXsdElementNames(*cs, out.configurationSchema.elementNames);
                } else {
                    r.warnings.push_back(L"<configuration> 里没有 configurationSchema");
                }
            }
            else if (_wcsicmp(ln.c_str(), L"configurationSchema") == 0) {
                // 顶层直接出现的情况也认（不同版本可能不一样）
                SerializeNode(child, out.configurationSchema.xsdText);
                CollectXsdElementNames(child, out.configurationSchema.elementNames);
            }
            else if (_wcsicmp(ln.c_str(), L"trustInfo") == 0) {
                // ★ securityDescriptorDefinitions 嵌在 <trustInfo><security><accessControl> 里（实测）。
                std::vector<const XmlNode*> defs;
                CollectByLocalName(child, L"securityDescriptorDefinition", defs);
                for (const XmlNode* d : defs) {
                    std::wstring name, sddl;
                    if (const auto* v = d->FindAttr(L"name")) name = *v;
                    if (const auto* v = d->FindAttr(L"sddl")) sddl = *v;
                    if (sddl.empty()) {
                        // 具体 SDDL 可能在子属性/文本里，把整节点序列化留底
                        SerializeNode(*d, sddl);
                    }
                    if (!name.empty()) out.securityDescriptorDefinitions[name] = sddl;
                }
                SerializeNode(child, out.signatureInfoRaw);   // 先留底，后续模块再解析
            }
            else if (_wcsicmp(ln.c_str(), L"deployment") == 0) {
                for (const auto& d : child.children) {
                    if (_wcsicmp(d.LocalName().c_str(), L"infFile") == 0) out.infFile = d.text.empty() ? std::wstring() : d.text;
                }
                // 有些清单把 infFile 直接写成 <deployment><infFile name="…"/> 之外的形态，
                // 这里如果没取到文本，就从属性里再试一次。
                if (out.infFile.empty()) {
                    for (const auto& d : child.children) {
                        if (_wcsicmp(d.LocalName().c_str(), L"infFile") == 0) {
                            if (const auto* nm = d.FindAttr(L"name")) out.infFile = *nm;
                        }
                    }
                }
            }
            else if (_wcsicmp(ln.c_str(), L"file") == 0) {
                // 真实属性：name / destinationPath / sourceName / sourcePath / importPath
                // 内嵌：<securityDescriptor name="…"/>
                FileSpec f;
                if (const auto* v = child.FindAttr(L"name"))            f.name = *v;
                if (const auto* v = child.FindAttr(L"destinationPath")) f.destinationPath = *v;
                if (const auto* v = child.FindAttr(L"sourceName"))      f.sourceName = *v;
                if (const auto* v = child.FindAttr(L"sourcePath"))      f.sourcePath = *v;
                if (const auto* v = child.FindAttr(L"importPath"))      f.importPath = *v;
                for (const auto& c : child.children) {
                    if (_wcsicmp(c.LocalName().c_str(), L"securityDescriptor") == 0) {
                        if (const auto* v = c.FindAttr(L"name")) f.securityDescriptorName = *v;
                    }
                }
                out.files.push_back(std::move(f));
            }
            else if (_wcsicmp(ln.c_str(), L"directories") == 0) {
                // ★ 有一层 <directories> 包裹 —— 实测如此，别按直觉放到顶层。
                for (const auto& d : child.children) {
                    if (_wcsicmp(d.LocalName().c_str(), L"directory") != 0) continue;
                    DirectorySpec spec;
                    if (const auto* v = d.FindAttr(L"destinationPath")) spec.destinationPath = *v;
                    if (const auto* v = d.FindAttr(L"name"))            spec.destinationPath = *v;  // 兼容别的写法
                    spec.owner = ParseBoolAttr(d, L"owner", false);
                    if (const auto* v = d.FindAttr(L"attributes"))      spec.attributes = *v;
                    for (const auto& c : d.children) {
                        if (_wcsicmp(c.LocalName().c_str(), L"securityDescriptor") == 0) {
                            if (const auto* v = c.FindAttr(L"name")) spec.securityDescriptorName = *v;
                        }
                    }
                    out.directories.push_back(std::move(spec));
                }
            }
            else if (_wcsicmp(ln.c_str(), L"securityDescriptorDefinition") == 0) {
                // 提供 name → SDDL 定义；实际内容可能是属性或子节点文本，两种都收。
                std::wstring name, sddl;
                if (const auto* v = child.FindAttr(L"name")) name = *v;
                if (const auto* v = child.FindAttr(L"sddl")) sddl = *v;
                if (sddl.empty() && !child.text.empty()) sddl = child.text;
                if (!name.empty()) out.securityDescriptorDefinitions[name] = sddl;
            }
            else if (_wcsicmp(ln.c_str(), L"securityDescriptor") == 0) {
                SecurityDescriptorSpec s;
                if (const auto* v = child.FindAttr(L"name")) s.name = *v;
                SerializeNode(child, s.rawText);
                out.securityDescriptors.push_back(std::move(s));
            }
            else if (_wcsicmp(ln.c_str(), L"signatureInfo") == 0) {
                SerializeNode(child, out.signatureInfoRaw);
            }
            else {
                // 认不出但**不致命**：真实清单里还有 migXml/rules/taskScheduler/objectSet 等，
                // 后续模块会逐块实现。把它们记下来 —— 这份列表就是待办清单。
                r.warnings.push_back(L"未处理的元素：<" + child.name + L">");
            }
        }

        if (!sawIdentity) {
            r.error = L"清单里没有 <assemblyIdentity>";
            return r;
        }

        r.ok = true;
        return r;
    }

} // namespace ocid
