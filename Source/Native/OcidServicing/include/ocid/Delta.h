#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace ocid {

// Applies a PA30 delta (msdelta pseudo-LZX patch) to a basis buffer.
//
// The format was reversed from msdelta.dll; see
// Source/Native/OcidServicing/Delta-Format.md. Only RAW payloads (file type 1, the
// shape used by DCM manifests) are implemented; deltas that need the PE pre-processor
// are rejected with a diagnostic so the caller can fall back.
//
// Returns true on success and fills `output`; on failure `error` explains why.
bool ApplyPa30Delta(const std::vector<std::uint8_t>& source,
                    const std::vector<std::uint8_t>& delta,
                    std::vector<std::uint8_t>& output,
                    std::string& error);

// True when `delta` starts with the "PA30" signature.
bool IsPa30Delta(const std::vector<std::uint8_t>& delta);

}  // namespace ocid
