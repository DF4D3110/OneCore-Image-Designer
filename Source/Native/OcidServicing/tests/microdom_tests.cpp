// microdom_tests.cpp —— MdHd（清单 XML 的微 DOM 序列化）测试。
//
// 验收方式：拿真机 8061 ManifestCache 里那份 2,988 字节的 MdHd 对应的清单，
// 换成一个结构相同的小清单，把真机（同一份 wcp.dll）对这个输入产出的字节当作
// golden 硬编码在这里 —— 任何段（MdSp 串池顺序、MdDl 节点记录、MdLc 位置表）
// 出现一字节偏差都会失败。
//
// golden 的来历：这段 XML 喂给真机 wcp 的微 DOM 构造器（8061 x86，ed 与 8419 语料
// 同构），产出 472 字节；本项目实现必须逐字节相同。

#include "ocid/Microdom.h"

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace {

int g_failures = 0;

void Check(bool condition, const std::string& what)
{
    if (!condition)
    {
        std::fprintf(stderr, "FAIL %s\n", what.c_str());
        ++g_failures;
    }
}

std::vector<std::uint8_t> FromHex(const std::string& hex)
{
    std::vector<std::uint8_t> out;
    out.reserve(hex.size() / 2);
    auto nibble = [](char c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    };
    for (std::size_t i = 0; i + 1 < hex.size(); i += 2)
    {
        const int hi = nibble(hex[i]);
        const int lo = nibble(hex[i + 1]);
        if (hi < 0 || lo < 0) return {};
        out.push_back(static_cast<std::uint8_t>((hi << 4) | lo));
    }
    return out;
}

std::string ToHex(const std::vector<std::uint8_t>& bytes)
{
    static const char kDigits[] = "0123456789abcdef";
    std::string out;
    out.reserve(bytes.size() * 2);
    for (const std::uint8_t b : bytes)
    {
        out.push_back(kDigits[b >> 4]);
        out.push_back(kDigits[b & 0x0F]);
    }
    return out;
}

std::uint32_t ReadU32(const std::vector<std::uint8_t>& bytes, std::size_t offset)
{
    return static_cast<std::uint32_t>(bytes[offset]) |
           (static_cast<std::uint32_t>(bytes[offset + 1]) << 8) |
           (static_cast<std::uint32_t>(bytes[offset + 2]) << 16) |
           (static_cast<std::uint32_t>(bytes[offset + 3]) << 24);
}

// 与 golden 同结构的小清单。注意末尾的 CRLF 与两空格缩进都要保留 —— MdSp 会把
// 空白文本节点原样入池，改一个空格字节结果就不同。
const char* const kGoldenXml =
    "<assembly xmlns=\"urn:schemas-microsoft-com:asm.v3\" manifestVersion=\"1.0\">\r\n"
    "  <assemblyIdentity name=\"microdom.test\" version=\"1.0.0.0\" processorArchitecture=\"x86\"/>\r\n"
    "</assembly>\r\n";

}  // namespace

int main()
{
    // 1) 逐字节复刻 golden。
    {
        std::vector<std::uint8_t> out;
        std::string error;
        Check(ocid::BuildMicrodomLayout(kGoldenXml, out, error),
              "BuildMicrodomLayout failed: " + error);
        const std::string expectedHex =
            "4d644864d801000018000000c0000000a0010000980100004d645370a60000000e000000"
            "75726e3a736368656d61732d6d6963726f736f66742d636f6d3a61736d2e763300617373"
            "656d626c7900786d6c6e73006d616e696665737456657273696f6e00312e30000d0a20"
            "2000617373656d626c794964656e74697479006e616d65006d6963726f646f6d2e7465"
            "73740076657273696f6e00312e302e302e300070726f636573736f7241726368697465"
            "637475726500783836000d0a0000004d64446c010000000b0000000100000000000000"
            "07000200010a00008101000002000000ffff0000ffffffff0000000001000000030000"
            "00020304050900000002000000ffffffffffffffff020000000000000002000000ffff"
            "ffffffffffff030000000400000003000000050000000100000003000000ffff0000ff"
            "ffffff00000000060000000607080002000000ffffffffffffffff0700000008000000"
            "02000000ffffffffffffffff090000000a00000002000000ffffffffffffffff0b0000"
            "000c000000030000000d000000030000000d0000004d644474000000004d644c630100"
            "00000d0000000101000001010000010b000001340000014a0000020300000215000002"
            "2a0000023c000002590000030c0000";
        const std::vector<std::uint8_t> expected = FromHex(expectedHex);
        Check(!expected.empty(), "golden hex is malformed");
        Check(out == expected, "MdHd bytes differ from the golden sample");
        if (out != expected && !out.empty())
            std::fprintf(stderr, "  produced(%zu)=%s\n", out.size(), ToHex(out).c_str());
    }

    // 2) 头部与段魔数自洽（防止只对 golden 硬编码）。
    {
        std::vector<std::uint8_t> out;
        std::string error;
        if (ocid::BuildMicrodomLayout(kGoldenXml, out, error) && out.size() >= 24)
        {
            Check(ReadU32(out, 0) == 0x6448644Du, "MdHd signature");
            Check(ReadU32(out, 4) == static_cast<std::uint32_t>(out.size()), "ulTotalSize");
            const std::uint32_t sp = ReadU32(out, 8);
            const std::uint32_t dl = ReadU32(out, 12);
            const std::uint32_t lc = ReadU32(out, 16);
            const std::uint32_t dt = ReadU32(out, 20);
            Check(ReadU32(out, sp) == 0x7053644Du, "MdSp signature");
            Check(ReadU32(out, dl) == 0x6C44644Du, "MdDl signature");
            Check(ReadU32(out, dt) == 0x7444644Du, "MdDt signature");
            Check(ReadU32(out, lc) == 0x634C644Du, "MdLc signature");
            Check(sp < dl && dl < dt && dt < lc && lc < out.size(), "section order/sp,dll,dt,lc");
        }
        else
        {
            Check(false, "BuildMicrodomLayout failed for the structural check");
        }
    }

    // 3) 空文档 / 畸形输入要有明确失败或空文档行为，不能崩。
    {
        std::vector<std::uint8_t> out;
        std::string error;
        Check(ocid::BuildMicrodomLayout("", out, error), "empty document should build");
        if (!out.empty())
            Check(ReadU32(out, 0) == 0x6448644Du, "empty document signature");
        std::vector<std::uint8_t> out2;
        std::string error2;
        const bool ok = ocid::BuildMicrodomLayout("<assembly><unclosed>", out2, error2);
        Check(!ok || !out2.empty(), "malformed input must fail cleanly or build");
    }

    if (g_failures != 0)
    {
        std::fprintf(stderr, "%d microdom test(s) failed\n", g_failures);
        return 1;
    }
    std::printf("microdom tests passed\n");
    return 0;
}
