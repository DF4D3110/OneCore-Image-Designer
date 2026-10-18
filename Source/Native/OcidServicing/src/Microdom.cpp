// microdom.cpp —— MdHd 微 DOM 序列化实现（C++17 / MSVC，无第三方依赖、无异常控制流）
//
// 逐节对应 E:\ocid_verify\microdom_spec.md：
//   §2  顶层头与四段物理顺序（MdSp → MdDl → MdDt → MdLc，段间 4 字节对齐）
//   §3  字符串池 MdSp（intern 顺序 = 插入序）
//   §4  DomLayout MdDl（节点记录表）
//   §5  PositionData MdLc（有位置节点的 (行,列)）
//   §6  Doctype MdDt（无 DTD 恒为空）
// 行为以真机 ManifestCache 样本为准（8061 小样本 2,988 B + 8419 语料 3,041 份，逐字节一致）。

#include "ocid/Microdom.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace ocid {
namespace {

// ---------------------------------------------------------------------------
// §2 魔数（小端 u32）与常量
// ---------------------------------------------------------------------------
constexpr std::uint32_t kSigMdHd = 0x6448644Du;  // 'MdHd'
constexpr std::uint32_t kSigMdSp = 0x7053644Du;  // 'MdSp'
constexpr std::uint32_t kSigMdDl = 0x6C44644Du;  // 'MdDl'
constexpr std::uint32_t kSigMdDt = 0x7444644Du;  // 'MdDt'
constexpr std::uint32_t kSigMdLc = 0x634C644Du;  // 'MdLc'

constexpr std::uint32_t kNoString = 0xFFFFFFFFu;  // §3.1 空串/无串 ID
constexpr std::uint32_t kHeaderSize = 24;         // §2 顶层头长度
constexpr std::uint32_t kAttrDefNone = 0xFFFFu;   // §4.2 无 DTD 时 attribute-list 编号

// §3.3 属性命名空间表里的两个固定 URI
constexpr const char kXmlNamespace[] = "http://www.w3.org/XML/1998/namespace";
constexpr const char kXmlnsNamespace[] = "http://www.w3.org/2000/xmlns/";

// §7 内存节点类型码（其中只有下列几种会出现在 manifest 路径上）
enum NodeType : std::uint16_t {
  kNtElement = 0x00,
  kNtAttribute = 0x01,
  kNtEndTag = 0x02,                  // 0 字节，但进全局节点表/MdLc 计数（§4.6）
  kNtEmptyClose = 0x03,              // 同上
  kNtPcData = 0x04,
  kNtCData = 0x05,
  kNtProcessingInstruction = 0x0C,
  kNtComment = 0x0D,
  kNtDocument = 0x0E,
};

// ---------------------------------------------------------------------------
// 位置换算（§5：行、列均 1 基）
//
// 行分隔符：\r、\n、\r\n 各算一次换行（与原型 _LC 的正则一致）。
// 列 = 该行行首到目标字节之间的 UTF-8 码点数 + 1。
// 原型在解码后的 str 上按字符计数，这里按字节扫描但只对"码点起始字节"计数，
// 因此对合法 UTF-8 输入与原型完全等价。
// ---------------------------------------------------------------------------
class LineIndex {
 public:
  explicit LineIndex(const std::string& raw) {
    lineStarts_.push_back(0);
    for (std::size_t i = 0; i < raw.size(); ++i) {
      const unsigned char c = static_cast<unsigned char>(raw[i]);
      if (c == '\r') {
        if (i + 1 < raw.size() && raw[i + 1] == '\n') ++i;  // \r\n 算一次换行
        lineStarts_.push_back(i + 1);
      } else if (c == '\n') {
        lineStarts_.push_back(i + 1);
      }
    }
    // cpPrefix_[i] = raw[0..i) 内的码点数（UTF-8 续字节 0x80..0xBF 不计）
    cpPrefix_.resize(raw.size() + 1);
    cpPrefix_[0] = 0;
    for (std::size_t i = 0; i < raw.size(); ++i) {
      const unsigned char c = static_cast<unsigned char>(raw[i]);
      const bool isContinuation = (c >= 0x80 && c <= 0xBF);
      cpPrefix_[i + 1] = cpPrefix_[i] + (isContinuation ? 0 : 1);
    }
  }

  void Position(std::size_t index, std::uint32_t& line, std::uint32_t& column) const {
    if (index > cpPrefix_.size() - 1) index = cpPrefix_.size() - 1;  // 越界保护
    // 二分：找最后一个 lineStarts_[k] <= index
    std::size_t lo = 0, hi = lineStarts_.size() - 1;
    while (lo < hi) {
      const std::size_t mid = (lo + hi + 1) / 2;
      if (lineStarts_[mid] <= index) {
        lo = mid;
      } else {
        hi = mid - 1;
      }
    }
    line = static_cast<std::uint32_t>(lo + 1);
    column = static_cast<std::uint32_t>(cpPrefix_[index] - cpPrefix_[lineStarts_[lo]] + 1);
  }

 private:
  std::vector<std::size_t> lineStarts_;
  std::vector<std::size_t> cpPrefix_;
};

// ---------------------------------------------------------------------------
// 最小 XML 词法器
// ---------------------------------------------------------------------------

// 空白判定：`\s` 的 ASCII 部分（manifest 里不会出现 Unicode 空白）
inline bool IsSpaceByte(char c) {
  return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v';
}

// 名字字符的"排除集"：原型用正则 [^\s=/>]+ 取名字
inline bool IsNameExcluded(char c) { return IsSpaceByte(c) || c == '=' || c == '/' || c == '>'; }

// 等价于 Python 的 str.rstrip() / str.strip()（ASCII 空白）
std::string RStrip(const std::string& s) {
  std::size_t end = s.size();
  while (end > 0 && IsSpaceByte(s[end - 1])) --end;
  return s.substr(0, end);
}
std::string Strip(const std::string& s) {
  std::size_t begin = 0;
  while (begin < s.size() && IsSpaceByte(s[begin])) ++begin;
  std::size_t end = s.size();
  while (end > begin && IsSpaceByte(s[end - 1])) --end;
  return s.substr(begin, end - begin);
}

// 在 s 里从 from 起找下一段连续"名字字符"，等价于 re.search(r"[^\s=/>]+", s, from)
bool FindNameRun(const std::string& s, std::size_t from, std::size_t& start, std::size_t& len) {
  std::size_t p = from;
  while (p < s.size() && IsNameExcluded(s[p])) ++p;
  if (p >= s.size()) return false;
  std::size_t q = p;
  while (q < s.size() && !IsNameExcluded(s[q])) ++q;
  start = p;
  len = q - p;
  return true;
}

enum class TokKind {
  kText,     // 文本（含纯空白）
  kElement,  // 开始标签 / 空元素
  kEnd,      // 结束标签
  kCData,
  kComment,
  kPi,       // 处理指令（<?xml ?> 声明也先当作 PI，构建时丢弃）
  kDecl,     // <!DOCTYPE ...> 等声明（构建时忽略，§3.3）
};

struct AttrTok {
  std::string name;
  std::string value;
  std::uint32_t line = 1;  // 属性名第一个字符的位置（§5）
  std::uint32_t col = 1;
};

struct Token {
  TokKind kind = TokKind::kText;
  std::string name;               // 元素名 / 结束标签名 / PI target
  std::string text;               // 文本、CDATA、注释原文；PI 的完整 body
  std::string data;               // PI 数据部分（body 去掉 target，保留前导空白）
  std::vector<AttrTok> attrs;     // 仅开始标签；源码顺序
  bool selfClose = false;
  std::uint32_t line = 1;
  std::uint32_t col = 1;
};

// tokenize：严格照原型 md_build.tokenize 的分支顺序
//   TEXT → <? → <!-- → <![CDATA[ → <! → </ → 开始标签
bool Tokenize(const std::string& raw, std::vector<Token>& toks, std::string& error) {
  const LineIndex index(raw);
  const std::size_t n = raw.size();
  std::size_t i = 0;
  while (i < n) {
    // ---- 文本：到下一个 '<' 为止（含纯空白，§3.3 / §4.4）----
    if (raw[i] != '<') {
      std::size_t j = raw.find('<', i);
      if (j == std::string::npos) j = n;
      Token t;
      t.kind = TokKind::kText;
      t.text = raw.substr(i, j - i);
      index.Position(i, t.line, t.col);
      toks.push_back(std::move(t));
      i = j;
      continue;
    }

    // ---- 处理指令 <?target data?> ----
    if (raw.compare(i, 2, "<?") == 0) {
      const std::size_t j = raw.find("?>", i + 2);
      if (j == std::string::npos) {
        error = "XML 扫描失败：处理指令的 '?>' 未闭合（字节偏移 " + std::to_string(i) + "）";
        return false;
      }
      const std::string body = raw.substr(i + 2, j - (i + 2));
      Token t;
      t.kind = TokKind::kPi;
      t.text = body;
      std::size_t k = 0;
      while (k < body.size() && !IsSpaceByte(body[k])) ++k;
      t.name = body.substr(0, k);   // target
      t.data = body.substr(k);      // 数据部分（构建时再 Strip，见 §3.3）
      index.Position(i, t.line, t.col);
      toks.push_back(std::move(t));
      i = j + 2;
      continue;
    }

    // ---- 注释 <!-- ... --> ----
    if (raw.compare(i, 4, "<!--") == 0) {
      const std::size_t j = raw.find("-->", i + 4);
      if (j == std::string::npos) {
        error = "XML 扫描失败：注释的 '-->' 未闭合（字节偏移 " + std::to_string(i) + "）";
        return false;
      }
      Token t;
      t.kind = TokKind::kComment;
      t.text = raw.substr(i + 4, j - (i + 4));
      index.Position(i, t.line, t.col);  // 位置 = '<!--' 的 '<'（§5）
      toks.push_back(std::move(t));
      i = j + 3;
      continue;
    }

    // ---- CDATA <![CDATA[ ... ]]> ----
    if (raw.compare(i, 9, "<![CDATA[") == 0) {
      const std::size_t j = raw.find("]]>", i + 9);
      if (j == std::string::npos) {
        error = "XML 扫描失败：CDATA 的 ']]>' 未闭合（字节偏移 " + std::to_string(i) + "）";
        return false;
      }
      Token t;
      t.kind = TokKind::kCData;
      t.text = raw.substr(i + 9, j - (i + 9));
      index.Position(i, t.line, t.col);  // 位置 = '<![CDATA[' 的 '<'（§5）
      toks.push_back(std::move(t));
      i = j + 3;
      continue;
    }

    // ---- 其它 <! 声明：<!DOCTYPE ...>（不支持内部子集，§3.3 不进 DOM）----
    if (raw.compare(i, 2, "<!") == 0) {
      const std::size_t j = raw.find('>', i + 2);
      if (j == std::string::npos) {
        error = "XML 扫描失败：声明 '<!...' 的 '>' 未闭合（字节偏移 " + std::to_string(i) + "）";
        return false;
      }
      Token t;
      t.kind = TokKind::kDecl;
      t.text = raw.substr(i, j - (i + 2) + 2 + 1);  // 仅保留原文，构建时忽略
      index.Position(i, t.line, t.col);
      toks.push_back(std::move(t));
      i = j + 1;
      continue;
    }

    // ---- 结束标签 </name> ----
    if (raw.compare(i, 2, "</") == 0) {
      const std::size_t j = raw.find('>', i + 2);
      if (j == std::string::npos) {
        error = "XML 扫描失败：结束标签的 '>' 未闭合（字节偏移 " + std::to_string(i) + "）";
        return false;
      }
      Token t;
      t.kind = TokKind::kEnd;
      t.name = Strip(raw.substr(i + 2, j - (i + 2)));
      index.Position(i, t.line, t.col);
      toks.push_back(std::move(t));
      i = j + 1;
      continue;
    }

    // ---- 开始标签（可能自闭合）：扫描到引号外的 '>' ----
    {
      std::size_t j = i + 1;
      char quote = 0;
      while (j < n) {
        const char c = raw[j];
        if (quote != 0) {
          if (c == quote) quote = 0;
        } else if (c == '"' || c == '\'') {
          quote = c;
        } else if (c == '>') {
          break;
        }
        ++j;
      }
      if (j >= n) {
        error = "XML 扫描失败：开始标签的 '>' 未闭合（字节偏移 " + std::to_string(i) + "）";
        return false;
      }
      const std::size_t bodyEnd = j;
      std::string body = raw.substr(i + 1, bodyEnd - (i + 1));

      Token t;
      t.kind = TokKind::kElement;
      index.Position(i, t.line, t.col);  // 元素位置 = 标签 '<'（§5）

      // 自闭合判定：先 rstrip 再看是否以 '/' 结尾（照原型；非自闭合时 body 保持原样）
      const std::string trimmed = RStrip(body);
      if (!trimmed.empty() && trimmed.back() == '/') {
        t.selfClose = true;
        body = trimmed.substr(0, trimmed.size() - 1);
      }

      // 元素名：body 中第一段名字字符
      std::size_t nameStart = 0, nameLen = 0;
      if (!FindNameRun(body, 0, nameStart, nameLen)) {
        error = "XML 扫描失败：开始标签缺少元素名（字节偏移 " + std::to_string(i) + "）";
        return false;
      }
      t.name = body.substr(nameStart, nameLen);

      // 属性：源码顺序；位置 = 属性名第一个字符（§5）
      std::size_t k = nameStart + nameLen;
      for (;;) {
        std::size_t aStart = 0, aLen = 0;
        if (!FindNameRun(body, k, aStart, aLen)) break;
        k = aStart + aLen;
        while (k < body.size() && IsSpaceByte(body[k])) ++k;
        std::string value;
        if (k < body.size() && body[k] == '=') {
          ++k;
          while (k < body.size() && IsSpaceByte(body[k])) ++k;
          if (k < body.size() && (body[k] == '"' || body[k] == '\'')) {
            const char qc = body[k];
            std::size_t e = body.find(qc, k + 1);
            if (e == std::string::npos) e = body.size();
            value = body.substr(k + 1, e - (k + 1));
            k = e + 1;
          }
        }
        AttrTok a;
        a.name = body.substr(aStart, aLen);
        a.value = std::move(value);
        index.Position(i + 1 + aStart, a.line, a.col);
        t.attrs.push_back(std::move(a));
      }

      toks.push_back(std::move(t));
      i = bodyEnd + 1;
      continue;
    }
  }
  return true;
}

// ---------------------------------------------------------------------------
// 字节写入辅助
// ---------------------------------------------------------------------------
struct Writer {
  std::vector<std::uint8_t> bytes;

  void U8(std::uint32_t v) { bytes.push_back(static_cast<std::uint8_t>(v & 0xFFu)); }
  void U16(std::uint32_t v) {
    U8(v);
    U8(v >> 8);
  }
  void U32(std::uint32_t v) {
    U8(v);
    U8(v >> 8);
    U8(v >> 16);
    U8(v >> 24);
  }
  void Blob(const std::string& s) { bytes.insert(bytes.end(), s.begin(), s.end()); }
  // §4.1 / §5 的宽度码：1 → u8、2 → u16、3 → u32
  void Width(std::uint32_t w, std::uint32_t v) {
    if (w == 1) {
      U8(v);
    } else if (w == 2) {
      U16(v);
    } else {
      U32(v);
    }
  }
  void Pad4() {
    while (bytes.size() % 4 != 0) bytes.push_back(0);
  }
  void Patch32(std::size_t offset, std::uint32_t v) {
    bytes[offset + 0] = static_cast<std::uint8_t>(v & 0xFFu);
    bytes[offset + 1] = static_cast<std::uint8_t>((v >> 8) & 0xFFu);
    bytes[offset + 2] = static_cast<std::uint8_t>((v >> 16) & 0xFFu);
    bytes[offset + 3] = static_cast<std::uint8_t>((v >> 24) & 0xFFu);
  }
};

inline std::uint32_t Align4(std::uint32_t v) { return (v + 3u) & ~3u; }

// §4.1 / §5 宽度码：≤0xFF → 1、≤0xFFFF → 2、否则 3
inline std::uint32_t WidthCode(std::uint64_t v) {
  if (v <= 0xFF) return 1;
  if (v <= 0xFFFF) return 2;
  return 3;
}

// ---------------------------------------------------------------------------
// DOM 节点与作用域帧
// ---------------------------------------------------------------------------
struct Node {
  std::uint16_t type = 0;
  std::uint32_t line = 0;
  std::uint32_t col = 0;
  std::int32_t uidx = -1;   // §4.7 有位置节点序号（子数组里的数字）；闭合节点为 -1
  std::int32_t seq = -1;    // §7 结构序号（文档节点 = 0）
  std::uint32_t prefix = kNoString;  // §7 +0x44
  std::uint32_t uri = kNoString;     // §7 +0x48
  std::uint32_t local = kNoString;   // §7 +0x4C
  std::uint32_t value = kNoString;   // §7 +0x50
  std::int32_t parent = -1;
  std::vector<std::uint32_t> children;  // nodes_ 下标，创建顺序（属性在前）
};

struct ScopeFrame {
  std::unordered_map<std::string, std::string> prefixToUri;  // xmlns:pfx="U"
  std::string defaultNs;                                     // xmlns="U"
};

// ---------------------------------------------------------------------------
// 构建器
// ---------------------------------------------------------------------------
class Builder {
 public:
  bool Build(const std::string& raw, std::string& error);
  void Assemble(std::vector<std::uint8_t>& out) const;

 private:
  // §3.1 intern：空串 → -1（kNoString）且不入池；已存在则复用旧 ID
  std::uint32_t Intern(const std::string& s);
  // §4.7 新建节点；positioned = 有位置节点（分配 uidx/seq 并计入 MdDl/MdLc 的条目）
  std::int32_t NewNode(std::uint16_t type, std::int32_t parent, bool positioned,
                       std::uint32_t line, std::uint32_t column);
  void ResolveElementQName(const std::string& qname, std::string& uri,
                           std::string& prefix, std::string& local) const;
  void ResolveAttrQName(const std::string& qname, std::string& uri,
                        std::string& prefix, std::string& local) const;

  bool StartElement(const Token& t);
  void EndElement();
  void AddText(const Token& t);
  void AddLeaf(std::uint16_t type, const Token& t);
  void AddPi(const Token& t);

  void AppendRecord(Writer& w, const Node& n, std::uint32_t width) const;

  std::vector<std::string> strings_;
  std::unordered_map<std::string, std::uint32_t> stringIds_;
  std::vector<Node> nodes_;
  std::vector<ScopeFrame> scope_;
  std::uint32_t positioned_ = 0;  // 有位置节点数（原型 self.uidx）
  std::uint32_t nextSeq_ = 0;     // 原型 self.seq
  std::int32_t current_ = -1;     // 原型 self.cur
};

std::uint32_t Builder::Intern(const std::string& s) {
  if (s.empty()) return kNoString;
  const auto it = stringIds_.find(s);
  if (it != stringIds_.end()) return it->second;
  const std::uint32_t id = static_cast<std::uint32_t>(strings_.size());
  strings_.push_back(s);
  stringIds_.emplace(s, id);
  return id;
}

std::int32_t Builder::NewNode(std::uint16_t type, std::int32_t parent, bool positioned,
                              std::uint32_t line, std::uint32_t column) {
  Node node;
  node.type = type;
  node.parent = parent;
  if (positioned) {
    node.line = line;
    node.col = column;
    node.uidx = static_cast<std::int32_t>(positioned_);
    ++positioned_;
    node.seq = static_cast<std::int32_t>(nextSeq_);
    ++nextSeq_;
  }
  nodes_.push_back(std::move(node));
  const std::int32_t index = static_cast<std::int32_t>(nodes_.size() - 1);
  if (parent >= 0) nodes_[static_cast<std::size_t>(parent)].children.push_back(static_cast<std::uint32_t>(index));
  return index;
}

// §3.3 元素 QName 解析：无前缀 → 默认命名空间；有前缀 → 逐帧向外查 xmlns:pfx
void Builder::ResolveElementQName(const std::string& qname, std::string& uri,
                                  std::string& prefix, std::string& local) const {
  const std::size_t p = qname.find(':');
  if (p == std::string::npos) {
    prefix.clear();
    local = qname;
    uri = scope_.back().defaultNs;
    return;
  }
  prefix = qname.substr(0, p);
  local = qname.substr(p + 1);
  uri.clear();
  for (auto it = scope_.rbegin(); it != scope_.rend(); ++it) {
    const auto f = it->prefixToUri.find(prefix);
    if (f != it->prefixToUri.end()) {
      uri = f->second;
      break;
    }
  }
}

// §3.3 属性 QName 解析：无前缀属性**不**在默认命名空间里；xmlns/xml 两个前缀固定 URI
void Builder::ResolveAttrQName(const std::string& qname, std::string& uri,
                               std::string& prefix, std::string& local) const {
  const std::size_t p = qname.find(':');
  if (p == std::string::npos) {
    prefix.clear();
    local = qname;
    uri.clear();
    return;
  }
  prefix = qname.substr(0, p);
  local = qname.substr(p + 1);
  if (prefix == "xmlns") {
    uri = kXmlnsNamespace;
    return;
  }
  if (prefix == "xml") {
    uri = kXmlNamespace;
    return;
  }
  uri.clear();
  for (auto it = scope_.rbegin(); it != scope_.rend(); ++it) {
    const auto f = it->prefixToUri.find(prefix);
    if (f != it->prefixToUri.end()) {
      uri = f->second;
      break;
    }
  }
}

bool Builder::StartElement(const Token& t) {
  const std::int32_t parent = current_;
  const std::int32_t elem = NewNode(kNtElement, parent, true, t.line, t.col);

  // §3.3 作用域帧：元素自身的 xmlns* 声明**先入帧并 push**，再解析该元素 QName
  ScopeFrame frame;
  frame.defaultNs = scope_.back().defaultNs;  // 默认命名空间向下继承
  for (const AttrTok& a : t.attrs) {
    if (a.name == "xmlns") {
      frame.defaultNs = a.value;
    } else {
      const std::size_t p = a.name.find(':');
      if (p != std::string::npos && p == 5 && a.name.compare(0, 5, "xmlns") == 0) {
        frame.prefixToUri[a.name.substr(p + 1)] = a.value;
      }
    }
  }
  scope_.push_back(std::move(frame));

  std::string uri, prefix, local;
  ResolveElementQName(t.name, uri, prefix, local);
  // §3.3 元素 intern 顺序：uri、prefix、local
  nodes_[static_cast<std::size_t>(elem)].uri = Intern(uri);
  nodes_[static_cast<std::size_t>(elem)].prefix = Intern(prefix);
  nodes_[static_cast<std::size_t>(elem)].local = Intern(local);

  // §3.3 属性（源码顺序）intern 顺序：uri、prefix、local、value
  for (const AttrTok& a : t.attrs) {
    const std::int32_t attr = NewNode(kNtAttribute, elem, true, a.line, a.col);
    std::string auri, apfx, alocal;
    ResolveAttrQName(a.name, auri, apfx, alocal);
    nodes_[static_cast<std::size_t>(attr)].uri = Intern(auri);
    nodes_[static_cast<std::size_t>(attr)].prefix = Intern(apfx);
    nodes_[static_cast<std::size_t>(attr)].local = Intern(alocal);
    nodes_[static_cast<std::size_t>(attr)].value = Intern(a.value);
  }

  if (t.selfClose) {
    scope_.pop_back();
    // §4.6 空元素闭合：0 字节，但进全局节点表、进 MdLc 计数；不作为子节点
    NewNode(kNtEmptyClose, -1, false, 0, 0);
    current_ = parent;
  } else {
    current_ = elem;
  }
  return true;
}

void Builder::EndElement() {
  if (scope_.size() > 1) scope_.pop_back();  // 保护：不弹掉基础帧
  // §4.6 结束标签：0 字节，但进全局节点表、进 MdLc 计数
  NewNode(kNtEndTag, -1, false, 0, 0);
  current_ = current_ >= 0 ? nodes_[static_cast<std::size_t>(current_)].parent : -1;
}

void Builder::AddText(const Token& t) {
  if (current_ < 0) return;  // 根元素之后/之外的散文本：原型同样丢弃
  const std::int32_t n = NewNode(kNtPcData, current_, true, t.line, t.col);
  nodes_[static_cast<std::size_t>(n)].value = Intern(t.text);
}

void Builder::AddLeaf(std::uint16_t type, const Token& t) {
  const std::int32_t n = NewNode(type, current_, true, t.line, t.col);
  nodes_[static_cast<std::size_t>(n)].value = Intern(t.text);
}

void Builder::AddPi(const Token& t) {
  const std::int32_t n =
      NewNode(kNtProcessingInstruction, current_, true, t.line, t.col);
  // §4.4 PI：+4 = target 串 ID、+8 = data 串 ID（无 data → -1）；§3.3 data 去首尾空白
  nodes_[static_cast<std::size_t>(n)].local = Intern(t.name);
  nodes_[static_cast<std::size_t>(n)].value = Intern(Strip(t.data));
}

bool Builder::Build(const std::string& raw, std::string& error) {
  std::vector<Token> toks;
  if (!Tokenize(raw, toks, error)) return false;

  scope_.clear();
  scope_.push_back(ScopeFrame{});  // 基础帧：无前缀声明、无默认命名空间

  // §4.5 / §5 文档节点：类型 0x0E、序号 0、位置恒 (1,1)
  NewNode(kNtDocument, -1, true, 1, 1);
  current_ = 0;

  for (const Token& t : toks) {
    switch (t.kind) {
      case TokKind::kText:
        AddText(t);
        break;
      case TokKind::kElement:
        StartElement(t);
        break;
      case TokKind::kEnd:
        EndElement();
        break;
      case TokKind::kCData:
        AddLeaf(kNtCData, t);
        break;
      case TokKind::kComment:
        AddLeaf(kNtComment, t);
        break;
      case TokKind::kPi:
        // §3.3 XML 声明 <?xml ...?> 整个不进 DOM（大小写不敏感，照原型）
        {
          std::string lower = t.name;
          std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) {
            return static_cast<char>(c >= 'A' && c <= 'Z' ? c - 'A' + 'a' : c);
          });
          if (lower != "xml") AddPi(t);
        }
        break;
      case TokKind::kDecl:
        break;  // §3.3 <!DOCTYPE ...> 不进 DOM（不支持内部子集）
    }
  }
  return true;
}

// §4 节点记录：按全局节点创建顺序书写，每条记录以 1 字节 type 起，记录尾补 0 到 4 字节对齐
void Builder::AppendRecord(Writer& w, const Node& n, std::uint32_t width) const {
  switch (n.type) {
    case kNtElement: {
      std::uint32_t attrCount = 0;
      std::uint32_t contentCount = 0;
      for (const std::uint32_t ci : n.children) {
        if (nodes_[ci].type == kNtAttribute) {
          ++attrCount;
        } else {
          ++contentCount;
        }
      }
      // §4.2 max | (min << 22)；min != 0 时拆成"扩展形式"（+4 = min、+24 = max）
      const std::uint64_t mn = std::min(attrCount, contentCount);
      const std::uint64_t mx = std::max(attrCount, contentCount);
      const std::uint64_t packed = mx | (mn << 22);
      const bool extended = packed >= 0x400000ull;  // 等价于 min != 0

      // §4.2 b0：bit0 = 1；bit7 = (属性数 < 内容子数)（原型为 (0x08 << 4)，
      // §4.2 正文把它写作 bit3，实测/原型都落在 bit7，以原型为准）；
      // bit4..6 = §4.2.1 的未初始化栈位（don't-care），本实现恒写 0。
      w.U8(1u | ((attrCount < contentCount ? 0x08u : 0u) << 4));
      w.U8(extended ? 0x01u : 0x00u);  // b1.bit0 = 扩展形式
      w.U16(0);
      w.U32(static_cast<std::uint32_t>(extended ? mn : mx));
      w.U16(kAttrDefNone);  // +8 attribute-list 编号：无 DTD 恒 0xFFFF
      w.U16(0);
      w.U32(n.prefix);  // +12
      w.U32(n.uri);     // +16
      w.U32(n.local);   // +20
      if (extended) w.U32(static_cast<std::uint32_t>(packed & 0x3FFFFFull));  // +24 = max
      // 子节点序号数组：先属性（源码顺序）、再内容子；长度 = 属性数 + 内容子数
      for (const std::uint32_t ci : n.children) {
        if (nodes_[ci].type == kNtAttribute) {
          w.Width(width, static_cast<std::uint32_t>(nodes_[ci].uidx));
        }
      }
      for (const std::uint32_t ci : n.children) {
        if (nodes_[ci].type != kNtAttribute) {
          w.Width(width, static_cast<std::uint32_t>(nodes_[ci].uidx));
        }
      }
      w.Pad4();
      break;
    }
    case kNtAttribute:  // §4.3 固定 20 字节
      w.U8(0x02);
      w.U8(0);
      w.U8(0);
      w.U8(0);
      w.U32(n.prefix);
      w.U32(n.uri);
      w.U32(n.local);
      w.U32(n.value);
      break;
    case kNtEndTag:      // §4.6 0 字节
    case kNtEmptyClose:  // §4.6 0 字节
      break;
    case kNtPcData:  // §4.4 文本 0x03
      w.U8(0x03);
      w.U8(0);
      w.U8(0);
      w.U8(0);
      w.U32(n.value);
      break;
    case kNtCData:  // §4.4 CDATA 0x13
      w.U8(0x13);
      w.U8(0);
      w.U8(0);
      w.U8(0);
      w.U32(n.value);
      break;
    case kNtComment:  // §4.4 注释 0x06
      w.U8(0x06);
      w.U8(0);
      w.U8(0);
      w.U8(0);
      w.U32(n.value);
      break;
    case kNtProcessingInstruction:  // §4.4 PI 0x05：b0 + 0*3 + target + data + 0
      w.U8(0x05);
      w.U8(0);
      w.U8(0);
      w.U8(0);
      w.U32(n.local);  // target 串 ID
      w.U32(n.value);  // data 串 ID（无 data 为 0xFFFFFFFF）
      w.U32(0);
      break;
    case kNtDocument:  // §4.5 文档记录 0x07
      w.U8(0x07);
      w.U8(0);
      w.U16(static_cast<std::uint32_t>(n.children.size()));
      for (const std::uint32_t ci : n.children) {
        w.Width(width, static_cast<std::uint32_t>(nodes_[ci].uidx));
      }
      w.Pad4();
      break;
    default:
      break;
  }
}

void Builder::Assemble(std::vector<std::uint8_t>& out) const {
  // ---- §3.1 MdSp：magic + 段长 + 串数 + 串池（UTF-8 + NUL，插入序）----
  Writer sp;
  sp.U32(kSigMdSp);
  sp.U32(0);  // 段长稍后回填
  sp.U32(static_cast<std::uint32_t>(strings_.size()));
  for (const std::string& s : strings_) {
    sp.Blob(s);
    sp.U8(0);
  }
  sp.Patch32(4, static_cast<std::uint32_t>(sp.bytes.size()));

  // ---- §4 MdDl ----
  const std::uint32_t widthDl = WidthCode(positioned_);  // §4.1 宽度码由 ulItemCount 决定
  std::uint32_t firstElementChild = 0;
  for (const std::uint32_t ci : nodes_[0].children) {
    if (nodes_[ci].type == kNtElement) {
      firstElementChild = static_cast<std::uint32_t>(nodes_[ci].uidx);
      break;
    }
  }
  Writer dl;
  dl.U32(kSigMdDl);
  dl.U32(widthDl);
  dl.U32(positioned_);       // +8 有位置节点数（= MdLc 条目数）
  dl.U32(firstElementChild); // +12 文档节点的第一个元素子节点
  dl.U32(nodes_[0].seq < 0 ? 0u : static_cast<std::uint32_t>(nodes_[0].seq));  // +16 文档节点序号
  for (const Node& node : nodes_) AppendRecord(dl, node, widthDl);

  // ---- §6 MdDt：无 DTD ⇒ 条目数 0，段长恒 8 ----
  Writer dt;
  dt.U32(kSigMdDt);
  dt.U32(0);

  // ---- §5 MdLc ----
  // 宽度码由文档中最大行/列决定，与 MdDl 的宽度码**各自独立**推导
  std::uint32_t maxCoord = 1;
  for (const Node& node : nodes_) {
    if (node.uidx < 0) continue;
    maxCoord = std::max(maxCoord, std::max(node.line, node.col));
  }
  const std::uint32_t widthPos = WidthCode(maxCoord);
  Writer pos;
  pos.U32(kSigMdLc);
  pos.U32(widthPos);
  pos.U32(static_cast<std::uint32_t>(nodes_.size()));  // +8 全局节点数（含结束标签/空元素闭合）
  for (const Node& node : nodes_) {
    if (node.uidx < 0) continue;  // 结束标签/空元素闭合无位置、无条目
    pos.Width(widthPos, node.line);
    pos.Width(widthPos, node.col);
    pos.U8(0);
    pos.Pad4();
  }

  // ---- §2 顶层拼装：头 24 字节 + 四段；段间 4 字节对齐、填充为 0 ----
  const std::uint32_t offSp = kHeaderSize;
  const std::uint32_t offDl = Align4(offSp + static_cast<std::uint32_t>(sp.bytes.size()));
  const std::uint32_t offDt = Align4(offDl + static_cast<std::uint32_t>(dl.bytes.size()));
  const std::uint32_t offPos = Align4(offDt + static_cast<std::uint32_t>(dt.bytes.size()));
  const std::uint32_t total = offPos + static_cast<std::uint32_t>(pos.bytes.size());

  Writer w;
  w.U32(kSigMdHd);
  w.U32(total);   // +0x04
  w.U32(offSp);   // +0x08
  w.U32(offDl);   // +0x0C
  w.U32(offPos);  // +0x10（注意：位置表在前，doctype 在后）
  w.U32(offDt);   // +0x14
  const auto append_at = [&w](const Writer& seg, std::uint32_t target) {
    while (w.bytes.size() < target) w.bytes.push_back(0);
    w.bytes.insert(w.bytes.end(), seg.bytes.begin(), seg.bytes.end());
  };
  append_at(sp, offSp);
  append_at(dl, offDl);
  append_at(dt, offDt);
  append_at(pos, offPos);

  out = std::move(w.bytes);
}

}  // namespace

bool BuildMicrodomLayout(const std::string& xmlUtf8, std::vector<std::uint8_t>& out,
                         std::string& error) {
  out.clear();
  error.clear();
  Builder builder;
  if (!builder.Build(xmlUtf8, error)) return false;
  builder.Assemble(out);
  return true;
}

}  // namespace ocid

