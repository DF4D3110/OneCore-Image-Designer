#ifndef OCID_DCM_H
#define OCID_DCM_H

// Windows 10 16299+ 的组件清单存放在 WinSxS\Manifests 下时会被压成 DCM 容器：
//   +0x00 "DCM\x01"
//   +0x04 msdelta 补丁流（以 "PA30" 开头）
// 解压 = msdelta!ApplyDeltaB(字典, 补丁)，字典 = servicing stack 的 wcp.dll 资源 (type 614 = 0x266, id 1)。
// 真机验证：16299 x86 的 3 份 DCM 清单解出标准清单 XML。

#include <cstdint>
#include <string>
#include <vector>

namespace ocid {

// 判断一段字节是否是 DCM 压缩清单（"DCM\x01" 魔数）。
bool DcmIsCompressed(const std::vector<std::uint8_t>& data);

// 从 wcp.dll 里取出清单压缩字典（资源 type 614、id 1）。
bool LoadManifestDictionary(const std::wstring& wcpPath,
                            std::vector<std::uint8_t>& dictionary,
                            std::string& error);

// 用字典解开一份 DCM 清单，输出原始清单字节（UTF-8 XML）。
bool DcmDecodeManifest(const std::vector<std::uint8_t>& compressed,
                       const std::vector<std::uint8_t>& dictionary,
                       std::vector<std::uint8_t>& output,
                       std::string& error);

}  // namespace ocid

#endif  // OCID_DCM_H
