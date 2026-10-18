#include "ocid/Dcm.h"

#include <windows.h>

#include <cstring>

#include <msdelta.h>

#pragma comment(lib, "msdelta.lib")

namespace ocid {
namespace {

constexpr std::uint16_t kDictionaryResourceType = 614;  // 0x266
constexpr std::uint16_t kDictionaryResourceId = 1;

}  // namespace

bool DcmIsCompressed(const std::vector<std::uint8_t>& data) {
  static const char kMagic[4] = {'D', 'C', 'M', 0x01};
  return data.size() >= 8 && std::memcmp(data.data(), kMagic, sizeof(kMagic)) == 0;
}

bool LoadManifestDictionary(const std::wstring& wcpPath,
                            std::vector<std::uint8_t>& dictionary,
                            std::string& error) {
  dictionary.clear();
  HMODULE module = ::LoadLibraryExW(wcpPath.c_str(), nullptr,
                                    LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
  if (module == nullptr) {
    error = "cannot load wcp.dll";
    return false;
  }
  HRSRC resource = ::FindResourceW(module, MAKEINTRESOURCEW(kDictionaryResourceId),
                                   MAKEINTRESOURCEW(kDictionaryResourceType));
  if (resource == nullptr) {
    // 少数版本的资源类型号会不同，退一步按名字找（"MANIFESTDICTIONARY"）。
    resource = ::FindResourceW(module, MAKEINTRESOURCEW(kDictionaryResourceId), L"MANIFESTDICTIONARY");
  }
  if (resource == nullptr) {
    ::FreeLibrary(module);
    error = "manifest dictionary resource not found in wcp.dll";
    return false;
  }
  const DWORD size = ::SizeofResource(module, resource);
  HGLOBAL handle = ::LoadResource(module, resource);
  const void* data = handle != nullptr ? ::LockResource(handle) : nullptr;
  if (data == nullptr || size == 0) {
    ::FreeLibrary(module);
    error = "cannot lock manifest dictionary resource";
    return false;
  }
  const auto* bytes = static_cast<const std::uint8_t*>(data);
  dictionary.assign(bytes, bytes + size);
  ::FreeLibrary(module);
  return true;
}

bool DcmDecodeManifest(const std::vector<std::uint8_t>& compressed,
                       const std::vector<std::uint8_t>& dictionary,
                       std::vector<std::uint8_t>& output,
                       std::string& error) {
  output.clear();
  if (!DcmIsCompressed(compressed)) {
    error = "not a DCM manifest";
    return false;
  }
  if (dictionary.empty()) {
    error = "manifest dictionary is empty";
    return false;
  }

  const std::uint8_t* deltaStart = compressed.data() + 4;
  const std::size_t deltaSize = compressed.size() - 4;
  if (deltaSize < 4 || std::memcmp(deltaStart, "PA30", 4) != 0) {
    error = "DCM payload is not an msdelta patch";
    return false;
  }

  DELTA_INPUT source{};
  source.lpStart = const_cast<void*>(reinterpret_cast<const void*>(dictionary.data()));
  source.uSize = static_cast<ULONG_PTR>(dictionary.size());
  source.Editable = FALSE;

  DELTA_INPUT delta{};
  delta.lpStart = const_cast<void*>(reinterpret_cast<const void*>(deltaStart));
  delta.uSize = static_cast<ULONG_PTR>(deltaSize);
  delta.Editable = FALSE;

  DELTA_OUTPUT result{};
  if (!::ApplyDeltaB(DELTA_FLAG_NONE, source, delta, &result)) {
    error = "ApplyDeltaB failed for DCM manifest";
    return false;
  }
  if (result.lpStart == nullptr || result.uSize == 0) {
    if (result.lpStart != nullptr) {
      ::DeltaFree(result.lpStart);
    }
    error = "DCM manifest decompressed to nothing";
    return false;
  }
  const auto* out = static_cast<const std::uint8_t*>(result.lpStart);
  output.assign(out, out + result.uSize);
  ::DeltaFree(result.lpStart);
  return true;
}

}  // namespace ocid

