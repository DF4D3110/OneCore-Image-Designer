// OCID - WCM 配置模式实现

#include "ocid/WcmSchema.h"

#include "detail/XmlPull.h"

#include <cwchar>

namespace ocid {

    using detail::XmlNode;
    using detail::XmlParser;
    using detail::FindDescendant;

    namespace {

        std::wstring Trim(const std::wstring& s)
        {
            size_t b = 0, e = s.size();
            while (b < e && (s[b] == L' ' || s[b] == L'\t' || s[b] == L'\r' || s[b] == L'\n')) ++b;
            while (e > b && (s[e-1] == L' ' || s[e-1] == L'\t' || s[e-1] == L'\r' || s[e-1] == L'\n')) --e;
            return s.substr(b, e - b);
        }

        /// 从 `regkey('HKEY_CLASSES_ROOT\Unknown\shell')` 里取出引号内那段。
        /// 认不出的形态返回空串，由调用方记 warning。
        std::wstring ExtractRegKeyPath(const std::wstring& handler)
        {
            std::wstring h = Trim(handler);
            if (h.empty()) return L"";

            // 必须是以 regkey 开头的调用形态
            const wchar_t* k = L"regkey";
            size_t n = wcslen(k);
            if (h.size() < n || _wcsnicmp(h.c_str(), k, n) != 0) return L"";

            size_t lp = h.find(L'(', n);
            if (lp == std::wstring::npos) return L"";
            size_t rp = h.rfind(L')');
            if (rp == std::wstring::npos || rp <= lp) return L"";

            std::wstring inner = Trim(h.substr(lp + 1, rp - lp - 1));
            if (inner.size() >= 2 &&
                ((inner.front() == L'\'' && inner.back() == L'\'') ||
                 (inner.front() == L'"'  && inner.back() == L'"'))) {
                inner = inner.substr(1, inner.size() - 2);
            }
            return Trim(inner);
        }

        /// 把含根的逻辑路径拆成 (根, 根之下)。
        /// 只认 `HKEY_*` 开头的（真实数据里就是这几种）。
        bool SplitRoot(const std::wstring& full, std::wstring& root, std::wstring& sub)
        {
            root.clear();
            sub.clear();
            if (full.empty()) return false;

            size_t sep = full.find(L'\\');
            std::wstring head = (sep == std::wstring::npos) ? full : full.substr(0, sep);

            static const wchar_t* roots[] = {
                L"HKEY_CLASSES_ROOT", L"HKEY_LOCAL_MACHINE", L"HKEY_CURRENT_USER",
                L"HKEY_USERS", L"HKEY_CURRENT_CONFIG",
                L"HKCR", L"HKLM", L"HKCU", L"HKU", L"HKCC",
            };
            for (const wchar_t* r : roots) {
                if (_wcsicmp(head.c_str(), r) == 0) {
                    root = head;
                    sub = (sep == std::wstring::npos) ? L"" : full.substr(sep + 1);
                    return true;
                }
            }
            return false;
        }

    } // namespace

    // ── WcmSettingItem ──────────────────────────────────────────────────────

    std::wstring WcmSettingItem::RegistryKeyPath() const
    {
        return ExtractRegKeyPath(handler);
    }

    // ── 解析 ────────────────────────────────────────────────────────────────

    WcmSchemaParseResult WcmConfigurationSchema::Parse(const std::wstring& xsdText)
    {
        WcmSchemaParseResult r;
        items_.clear();
        byName_.clear();

        if (xsdText.empty()) { r.error = L"空文本"; return r; }

        XmlParser p(xsdText);
        XmlNode root;
        if (!p.ParseDocument(root)) {
            r.error = p.error.empty() ? L"XML 解析失败" : p.error;
            return r;
        }

        // 入口可能给的是 <configurationSchema>、<xsd:schema>，也可能外面还包了 <configuration>。
        // 直接全树找 `element` 节点即可 —— 用 FindDescendant 的收集版更稳。
        std::vector<const XmlNode*> elems;
        detail::CollectByLocalName(root, L"element", elems);

        // 如果 root 自己就是 element，CollectByLocalName 不会包含它，这里补一下
        if (_wcsicmp(root.LocalName().c_str(), L"element") == 0) elems.insert(elems.begin(), &root);

        for (const XmlNode* e : elems) {
            WcmSettingItem it;
            if (const auto* v = e->FindAttr(L"name"))           it.name = *v;
            if (const auto* v = e->FindAttr(L"type"))           it.xsdType = *v;
            if (const auto* v = e->FindAttr(L"default"))        it.defaultValue = *v;
            if (const auto* v = e->FindAttr(L"wcm:displayName"))it.displayName = *v;
            if (const auto* v = e->FindAttr(L"wcm:handler"))    it.handler = *v;
            if (const auto* v = e->FindAttr(L"wcm:legacyName")) it.legacyName = *v;
            if (const auto* v = e->FindAttr(L"wcm:legacyType")) it.legacyType = *v;
            if (const auto* v = e->FindAttr(L"wcm:scope"))      it.scope = *v;
            if (const auto* v = e->FindAttr(L"wcm:subScope"))   it.subScope = *v;
            if (const auto* v = e->FindAttr(L"wcm:description"))it.description = *v;
            if (const auto* v = e->FindAttr(L"wcm:migrate"))    it.migrate = *v;

            if (it.name.empty()) {
                r.warnings.push_back(L"<xsd:element> 没有 name，已跳过");
                continue;
            }
            if (it.handler.empty()) {
                // 没有 handler 的 element 不是"可执行的设置"（可能是纯类型声明）
                r.warnings.push_back(L"设置项 " + it.name + L" 没有 wcm:handler（非可执行设置）");
            } else if (it.RegistryKeyPath().empty()) {
                r.warnings.push_back(L"设置项 " + it.name + L" 的 handler 形态认不出：" + it.handler);
            }

            byName_[it.name] = items_.size();
            items_.push_back(std::move(it));
        }

        r.ok = true;
        return r;
    }

    const WcmSettingItem* WcmConfigurationSchema::Find(const std::wstring& name) const
    {
        auto it = byName_.find(name);
        return it == byName_.end() ? nullptr : &items_[it->second];
    }

    size_t WcmConfigurationSchema::RegistrySettingCount() const
    {
        size_t n = 0;
        for (const auto& i : items_) if (i.IsRegistrySetting()) ++n;
        return n;
    }

    // ── 转成注册表操作 ──────────────────────────────────────────────────────

    std::vector<WcmRegistryOperation> ToRegistryOperations(
        const WcmConfigurationSchema& schema,
        std::vector<std::wstring>* warnings)
    {
        std::vector<WcmRegistryOperation> ops;

        for (const auto& it : schema.Items()) {
            std::wstring keyPath = it.RegistryKeyPath();
            if (keyPath.empty()) continue;   // 非注册表设置，跳过（warnings 已在解析时记过）

            WcmRegistryOperation op;
            if (!SplitRoot(keyPath, op.root, op.subKey)) {
                if (warnings) warnings->push_back(L"键路径没有可识别的根：" + keyPath);
                continue;
            }
            op.valueName  = it.legacyName;                        // 空 = 默认值
            op.valueType  = it.legacyType.empty() ? L"REG_SZ" : it.legacyType;
            op.data       = it.defaultValue;
            op.scope      = it.scope;
            op.sourceItem = it.name;
            ops.push_back(std::move(op));
        }

        return ops;
    }

    // ── 值转换 ──────────────────────────────────────────────────────────────

    ConvertedValue ConvertSettingValue(const WcmSettingItem& item)
    {
        ConvertedValue cv;
        const std::wstring& t = item.legacyType;

        if (_wcsicmp(t.c_str(), L"REG_DWORD") == 0) {
            cv.isDword = true;
            const std::wstring s = Trim(item.defaultValue);
            try {
                if (s.size() > 2 && s[0] == L'0' && (s[1] == L'x' || s[1] == L'X'))
                    cv.dword = (uint32_t)wcstoul(s.c_str() + 2, nullptr, 16);
                else
                    cv.dword = (uint32_t)wcstoul(s.c_str(), nullptr, 10);
                cv.ok = true;
            } catch (...) {
                cv.error = L"REG_DWORD 解析失败：" + s;
            }
            return cv;
        }

        if (_wcsicmp(t.c_str(), L"REG_BINARY") == 0) {
            cv.isBinary = true;
            // hexBinary：文本本身就是字节序列。**不做大小端翻转** —— 实测语义就是这样。
            std::wstring h;
            for (wchar_t c : item.defaultValue) {
                if ((c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') || (c >= L'A' && c <= L'F'))
                    h += c;
            }
            if (h.size() % 2) h = L"0" + h;
            cv.bytes.reserve(h.size() / 2);
            for (size_t i = 0; i + 1 < h.size(); i += 2) {
                auto hexv = [](wchar_t c) -> int {
                    if (c >= L'0' && c <= L'9') return c - L'0';
                    if (c >= L'a' && c <= L'f') return c - L'a' + 10;
                    if (c >= L'A' && c <= L'F') return c - L'A' + 10;
                    return 0;
                };
                cv.bytes.push_back((uint8_t)((hexv(h[i]) << 4) | hexv(h[i + 1])));
            }
            cv.ok = true;
            return cv;
        }

        // REG_SZ / REG_EXPAND_SZ / 其它字符串类
        cv.text = item.defaultValue;
        cv.ok = true;
        return cv;
    }

    // ── 两套声明机制的统一收集 ──────────────────────────────────────────────

    void RegistryKeyToOperations(const RegistryKeySpec& key,
                                 std::vector<WcmRegistryOperation>& out)
    {
        std::wstring root, sub;
        if (SplitRoot(key.keyName, root, sub)) {
            for (const auto& v : key.values) {
                WcmRegistryOperation op;
                op.root       = root;
                // 清单里 keyName 可能带尾随反斜杠（如 "HKEY_CLASSES_ROOT\Foo\"），
                // 直接拿去拼路径会多出一个空段。统一去掉尾部反斜杠与空白。
                while (!sub.empty() && (sub.back() == L'\\' || sub.back() == L' ')) sub.pop_back();
                op.subKey     = sub;
                op.valueName  = v.name;                       // 空 = 默认值
                op.valueType  = v.valueType.empty() ? L"REG_SZ" : v.valueType;
                op.data       = v.value;
                op.owner      = v.owner;
                op.operationHint = v.operationHint;
                op.scope      = L"";                          // ① 不带 scope，由 handler 根决定落点
                op.mechanism  = L"registryKeys";
                op.sourceItem = key.keyName + (v.name.empty() ? L"\\defaultValue" : (L"\\" + v.name));
                out.push_back(std::move(op));
            }
        }
        // 真实数据里 registryKey 是扁平的（每个 keyName 都写全路径），
        // 但万一出现嵌套写法也一并处理，免得静默丢设置。
        for (const auto& sk : key.subKeys) RegistryKeyToOperations(sk, out);
    }

    RegistryOperationBatch CollectManifestRegistryOperations(
        const ComponentManifest& manifest,
        std::vector<std::wstring>* warnings)
    {
        RegistryOperationBatch batch;

        // ① `<registryKeys>`（5282 条量级，带 owner/operationHint）
        for (const auto& k : manifest.registryKeys) {
            size_t before = batch.operations.size();
            RegistryKeyToOperations(k, batch.operations);
            batch.fromRegistryKeys += batch.operations.size() - before;
        }

        // ② `<configurationSchema>`（533 条量级，带 wcm:handler/legacy*）
        if (!manifest.configurationSchema.xsdText.empty()) {
            WcmConfigurationSchema schema;
            auto sr = schema.Parse(manifest.configurationSchema.xsdText);
            if (sr.ok) {
                auto ops = ToRegistryOperations(schema, warnings);
                for (auto& op : ops) {
                    op.mechanism = L"wcm";
                    batch.operations.push_back(std::move(op));
                }
                batch.fromWcmSchema = ops.size();
            } else if (warnings) {
                warnings->push_back(L"configurationSchema 解析失败：" + sr.error);
            }
        }

        return batch;
    }

} // namespace ocid
